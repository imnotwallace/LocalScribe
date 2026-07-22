# Matter & Session Q&A (Assistant Chat) Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement design §7.5 plus the chat portions of §7.6 and §7.7 of `docs/plans/2026-07-18-steno-round-design.md` (branch split note in §7): strict-extractive Q&A over exactly two scopes — session (full projected transcript with a raise-ctx-then-excerpt ladder) and matter (per-session summaries newest-first with an explicit included/omitted/no-summary disclosure, never a silent truncation) — with per-claim `[HH:MM:SS]` citation post-validation against the projected rows (±2 s tolerance + `TextDistance` fuzzy match), unverifiable claims rendered visibly flagged and never dropped, per-scope persisted chat history (`assistant\chats.json` via AtomicFile + schema stamp), a session chat pane in the Session Details **Assistant** tab, a Matters **Assistant** tab (matter chat + per-session summary status with a generate CTA), citation click-through to the Read view scrolled to the cited segment, the AI-draft label on all chat output, and visible busy/queued states while a recording blocks the engine.

**Architecture:** A pure Core pipeline plus a thin App shell. Core: `AssistantCitationFormat` (parse/format `[HH:MM:SS]` + claim-line extraction) → `CitationValidator`/`MatterCitationValidator` (verdicts over `DisplayRow`s / included summaries, producing presentation-ready `AnswerLine`+`CitationChip` records) → context builders (`SessionQaContextBuilder` + `QaContextLadder` per-job num_ctx sizing, `ExcerptContextBuilder` over the existing `SearchIndexService.Query`, `MatterQaContextBuilder`) → `AssistantChatStore` (append-only per-scope `chats.json`) → `AssistantQaService` (warm-helper lifecycle over the foundation branch's `IAssistantChatSessionFactory`: session reused while the warmup payload is byte-identical, torn down on close/scope change/context change; engine lease acquired around every model call; persists a turn ONLY on a successful `AssistantDone`). `QaScopeFactory` is the single Core file that calls foundation prompt/budget/shaper members. App: one scope-agnostic `AssistantChatViewModel` + `AssistantChatPanel` UserControl reused by both surfaces; `MatterAssistantViewModel` adds the summary-status list + coverage disclosure; citation chips navigate through the existing `openReadView` + `ReadViewWindow.ShowFindAt(seq, term)` path (the exact wiring the Search page's snippet click already uses). All model interaction in tests goes through scripted fakes (`FakeAssistantChatSession`/`FakeAssistantChatSessionFactory` emitting canned `AssistantEvent` sequences); real-model runs are smoke-only.

**Tech Stack:** C#/.NET 10, WPF, CommunityToolkit.Mvvm, Wpf.Ui, xUnit. LLM runtime = the foundation branch's out-of-process `LocalScribe.Assistant.exe`, consumed ONLY through its stdio-protocol contracts (below) — this branch never touches the helper, models, storage of summaries, or prompts beyond calling them.

## Global Constraints

- **Target branch:** `feat/matter-qa`, created off master **AFTER `feat/llm-foundation-summaries` merges** — this is the LAST of the 7 branches of the steno round (design §1 order). The design spec is already on master; add THIS plan (`docs/plans/2026-07-19-matter-qa-plan.md`) to the branch with a `docs(plans): ...` commit if it is not there yet.
- **Anchors:** existing-code anchors are grounded @ master 7605606 (the `feat/ux-round-2026-07-18` merge). Six other branches land before this one — **re-verify every anchor by its QUOTED context before editing; if drifted, locate by the quoted code, not the line number.**
- **Foundation contracts (LOCKED — these types exist on master by the time this branch starts; consume them verbatim, never rebuild them):**
  - Helper protocol events + `AssistantModelInfo`, `AssistantModelManifest { Installed, DefaultModel }`, `AssistantModelRef(File, Sha256, Backend)`, `AssistantRequest(Op, ModelPath, CtxTokens, Backend, KeepAlive, PayloadJson)`.
  - `AssistantEvent` hierarchy: `AssistantChunk(Text)`, `AssistantProgress(Phase, Current, Total)`, `AssistantDone(Backend, PromptTokens, OutputTokens)`, `AssistantError(Message)`.
  - `IAssistantJobRunner.RunAsync(AssistantRequest, CancellationToken) : IAsyncEnumerable<AssistantEvent>` (spawn-per-job; used by summaries — this branch uses the chat factory instead).
  - `IAssistantChatSessionFactory.StartAsync(AssistantRequest warmupRequest, CancellationToken)` → `IAssistantChatSession { AskAsync(string questionPayloadJson, CancellationToken) : IAsyncEnumerable<AssistantEvent>; DisposeAsync(); }`. Warm-helper semantics: keepAlive=true, teardown on close / 5-min idle / scope change / context change. **The 5-min idle teardown is the foundation session's own responsibility (it owns the process watchdog); THIS branch owns teardown on close, scope change, and context change** (`AssistantQaService.DisposeAsync` / `AssistantChatViewModel.InvalidateContext`).
  - `SummaryVersion(Id, CreatedAt, SourceTranscriptVersion, Model, PromptVersion, ContentMarkdown, Stale)`, `SummaryStore { LoadAsync, AppendAsync, MarkAllStaleAsync }`.
  - `AssistantPrompts { PromptVersion, BuildAnswerPrompt(...) }`, `AssistantInputShaper { StripLeadingTimestamps, BuildSpeakerPreamble }`, `TokenBudget { EstimateTokens, NeedsChunking, ChunkBudgetChars }`, `AssistantGate` (blocked-while-recording lease).
- **CONTRACT-DRIFT rule:** call sites into foundation members whose full signatures the contract list leaves open (`BuildAnswerPrompt(...)`, `TokenBudget.EstimateTokens`, `AssistantInputShaper` members, `AssistantGate`'s acquire member, `SummaryStore`'s ctor/LoadAsync shape, the exact answer-op payload JSON keys) are marked `// CONTRACT:` in the code below with the ASSUMED shape written out in full. Before running each such task, re-verify the real signature on the merged master (grep the type name) and **adapt identifiers/parameter order only — never the behavior this plan fixes**. The tests below deliberately assert behavior (substrings, counts, field values this branch owns), not foundation signatures, so they survive identifier-level drift.
- **LOCKED rules (design §1/§7.5, restated — every task below must hold them):**
  - **Strict-extractive Q&A:** the prompt forbids inference (foundation's `BuildAnswerPrompt` owns the wording); per-claim `[HH:MM:SS]` citations are mandatory; **unverifiable claims are FLAGGED, never dropped** — no code path in this plan removes or rewrites answer text.
  - **No cross-matter scope:** session + matter only; the matter context is hard-scoped to one matter's tagged sessions by construction.
  - **Disclosed degradation:** excerpt mode carries a visible disclosure header; the matter scope lists exactly which sessions were included/omitted/lack summaries — no silent truncation anywhere.
  - **Real-model runs are smoke-only:** every automated test stubs `IAssistantChatSessionFactory`/`IAssistantChatSession` (this plan's fakes) — no test spawns the helper.
  - **AI-draft label on ALL chat output:** `AssistantChatViewModel.AiDraftLabel` rendered on every turn.
  - **Evidentiary:** assistant artifacts are derived work product stored separately (`assistant\` folders); nothing touches transcript files; nothing here writes markers or gates recording.
- 0-warning build gate must hold. Core suite has 2 known pre-existing fixture failures (unrelated); App suite must be fully green.
- Tests: xUnit. Filtered run template: `dotnet test "<testproj>" --filter "FullyQualifiedName~<Name>" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\`
- **Full App-suite gate runs (XamlHygieneTests):** `RepoPaths.SolutionRoot()` walks UP from the test assembly directory to find `.git`, so an App-suite run that includes `XamlHygieneTests` MUST NOT use the Temp isolated BaseOutputPath (it sits outside the repo — 5 false failures). Run full App-suite gates with the default repo-internal output path; keep the isolated path for filtered runs. If the default path hits MSB3027 (app running, locked bin), report and wait — never kill processes.
- IMPORTANT: the LocalScribe app may be running and LOCK bin DLLs (MSB3027 copy error — NOT a compile error). Always use the isolated BaseOutputPath above (every command below already appends it); NEVER kill the user's running app or any other process.
- Test projects: App = `tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj`; Core = `tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj`. App.Tests is a leaf project (NO ProjectReference to Core.Tests) — it `<Compile Include>`-links shared doubles from Core.Tests (`LocalScribe.App.Tests.csproj` lines 30-38); Task 8 links this plan's `AssistantChatFakes.cs` the same way. There is NO `InternalsVisibleTo` anywhere in this repo (verified) — every new member tests call directly must be `public`.
- Never use Unicode emojis in test code or scripts (project rule). All new UI strings are ASCII except the AI-draft label's em dash and the provenance middle dot, both written as C# escapes (`\u2014`, `\u00B7` — the read-view footer precedent at `ReadViewViewModel.cs:198`) so source files stay ASCII.
- Core test classes sit in the global namespace with per-file `using`s (repo convention, e.g. `TextDistanceTests.cs`); temp dirs use the `_root` field pattern (`Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}")` + delete in Dispose).
- Commit style: `feat(core)`/`feat(app)`/`test(...)`/`docs(...)`; every commit message ends with exactly this trailer line:
```
Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
```

### Contract resolutions (verified against `docs/plans/2026-07-19-llm-foundation-summaries-plan.md`, 2026-07-19 — BINDING)

The foundation plan now exists; every `// CONTRACT:`-marked assumed shape below is RESOLVED here. Apply these exact substitutions at the marked sites (this is the CONTRACT-DRIFT rule's "adapt identifiers only" step, done in advance — still re-verify against the merged master before each task):

1. **`TokenBudget.EstimateTokens(int chars)`** — takes a CHAR COUNT, not a string. Everywhere this plan binds the `estimateTokens : Func<string,int>` seam to production (Task 7 `QaScopeFactory`), bind `s => TokenBudget.EstimateTokens(s.Length)`. The seam itself and all tests are unchanged.
2. **`SummaryStore`** — ctor is `SummaryStore(StoragePaths paths)` (one instance, NOT per-session), methods `LoadAsync(string sessionId, CancellationToken)` / `AppendAsync(string sessionId, SummaryVersion, CancellationToken)` / `MarkAllStaleAsync(string sessionId, CancellationToken)`, and the foundation composition exposes it as **`comp.Summaries`**. The Task 11 read (`// CONTRACT:` site in the composition) is therefore `await comp.Summaries.LoadAsync(s.Id, ct)` — do NOT construct a new store.
3. **`AssistantPrompts.BuildAnswerPrompt(string speakerPreamble, string contextText, string question)`** — exact signature; the prompt template ends with `"Question:\n" + question`. The Task 7 `Warmup` site's assumed `(speakerPreamble, contextBody, excerptMode)` call is WRONG as written: pass the excerpt-mode disclosure inside `contextText` (prepend `disclosure + "\n\n"` to the body when in excerpt mode) and pass `""` as `question` for the warmup prefix. Per-question asks pass the real question (resolution 4).
4. **Payload envelopes** — the foundation wire has ONE v1 payload shape for BOTH ops: `AssistantWire.PromptPayload(string prompt, int maxTokens)` → `{"prompt":...,"maxTokens":...}`. DELETE this plan's `AnswerWarmupPayload`/`AnswerQuestionPayload` records and serialize instead: warmup request `PayloadJson = AssistantWire.PromptPayload(BuildAnswerPrompt(preamble, contextTextWithDisclosure, ""), 16)` (tiny cap — the warmup exists to load the model and prefill the shared prefix); each `AskAsync` sends `AssistantWire.PromptPayload(BuildAnswerPrompt(preamble, contextTextWithDisclosure, question), MaxAnswerTokens)` — the FULL prompt each time, byte-identical up to the question, so the helper's KV prefix reuse (the warm-chat contract's whole point) engages; only the question tail is new prefill. `MaxAnswerTokens = 1024` (this plan's constant). Fake-session tests that asserted the old envelope keys assert the `{"prompt":` key and the question substring instead — behavior assertions (question text present, one process, N asks) are unchanged.
5. **`AssistantGate`** — acquire member is `bool TryEnter(out IDisposable lease)`; the refused state carries no reason string, so the VM's queued label derives from `session.State != Idle` (already how Task 8 words it).
6. **Warm-teardown split confirmed:** the foundation's `IAssistantChatSession` owns the process watchdog + 5-min idle teardown; this plan's close/scope-change/context-change teardown stands as written.

### Repo facts the tasks below rely on (verified @ 7605606)

- `SessionProjectionLoader.LoadAsync(StoragePaths, Settings, TimeProvider, string sessionId, CancellationToken)` → `LoadedProjection` whose `Rows : IReadOnlyList<DisplayRow>` is the citation ground truth. `DisplayRow { bool IsMarker; long StartMs; long EndMs; string? DisplayName; string Text; IReadOnlyList<RowSegment> Segments }`; `RowSegment(int Seq, TranscriptSource Source, long StartMs, long EndMs, string ProjectedText, string RawText, bool IsCorrected, bool IsPinned, bool IsSplitChild = false, int PartIndex = 0)`. Rows from the loader carry populated `Segments` (only the live view builds rows without payloads). `TranscriptSource` = `{ Local, Remote, System }`.
- `TextDistance` (`LocalScribe.Core.Projection`): `NormalizedSimilarity(string, string) : double` (char-level, normalized, [0,1]), `ContainmentSimilarity(string, string) : double` (best token-window containment; returns 0 when the shorter normalized string is < 12 chars or < 3 tokens), `Normalize(string) : string` (lowercase invariant, alnum-only, single-space collapsed).
- `SearchIndexService.Query(SearchQuery) : IReadOnlyList<SearchResult>` — pure, thread-safe, empty before the cold build. `SearchQuery(string Text, string? MatterId = null, ...)` AND-matches whitespace-split terms. `SearchResult(SearchSessionEntry Session, IReadOnlyList<SearchHit> Hits, int HitCount)`; `SearchHit(int Seq, int PartIndex, long StartMs, string Speaker, string Snippet, string MatchedTerm, bool MatchesOriginalOnly, bool IsSpeakerNameMatch)`; `SearchSessionEntry.SessionId`. `Seq < 0` = speaker-name hit with no spoken line.
- `StoragePaths`: sessions at `<Root>\sessions\<id>\`, matters at `<Root>\matters\<matterId>\matter.json`; `MattersDir`, `SessionDir(id)`, `MatterJson(matterId)` exist. `AtomicFile.WriteAllTextAsync(path, text, ct)`; `JsonFile.ReadAsync<T>(path, ct)` / `JsonFile.WriteAsync<T>(path, value, ct)` (camelCase via `LocalScribeJson.Options`, atomic); `SchemaGuard.ReadObjectAsync/ReadVersion` (property `schemaVersion`, missing → 1) / `RejectIfNewer(fileVersion, maxSupported, fileKind)` (`NotSupportedException` when newer).
- `MaintenanceService.RunForSessionAsync<T>(sessionId, work, ct)` — per-session single-flight gate; `MaintenanceService.SessionContentChanged : event Action<string>` fires on correction save/split/re-render; `ListSessionsAsync(ct) : Task<SessionCatalogResult>` with `.Sessions` (each: `.Id`, `.Meta.Title`, `.Meta.MatterIds`, `.Session.StartedAtUtc`, `.Session.EndedAtUtc`).
- Read-view click-through: `App.xaml.cs` builds `Action<string> openReadView` (dedup map `readViews`) and the Search page navigates via `window.ShowFindAt(int seq, string term)` (`ReadViewWindow.xaml.cs:254` — stashes the target until loaded, then `OpenFind(term)` + `RowIndexOfSeq(seq)` + `MoveFindTo` + `ScrollIntoView`). A `seq < 0` click-through just opens the window (existing guard). **Lambdas in `App.xaml.cs` cannot reference locals declared later in the method** (documented hoisting rule there) — the citation-navigation slot below is a mutable delegate assigned after `openReadView` exists.
- Session Details = `SessionDetailsWindow.xaml` (plain `TabControl`, `TabItem`s "Details" and "Speakers" @ 7605606; the foundation branch adds an "Assistant" `TabItem` with the summary UI) + `MetadataEditorViewModel` (`(MaintenanceService, SessionViewModel, IUiErrorReporter, Action<Action> dispatch, TimeProvider, Func<string,bool> confirm)` ctor). Matters page = `MattersPage.xaml` (`TabControl SelectedIndex="1"` with "Details"/"Sessions"/"Vocabulary"/"Advanced" `TabItem`s) + `MattersPageViewModel` (`OpenReadViewRequested`, `OpenSessionDetailsRequested`, `SelectAsync` sets `HasSelection = true;` at the end of its dispatch block).
- `IUiErrorReporter { void Report(string context, Exception ex); void Info(string message); }`. App test files define their own private `FakeReporter` (repo convention). `SessionState` = `{ Idle, Recording, Paused, Finalizing }` (`LocalScribe.Core.Live`); the shared `SessionViewModel` local in `App.xaml.cs` is named `session` and exposes `State`.

---

### Task 1: Core — `AssistantCitationFormat` (stamp format/parse + claim-line extraction)

**Files:**
- Create `src\LocalScribe.Core\Assistant\AssistantCitationFormat.cs`
- Create test `tests\LocalScribe.Core.Tests\AssistantCitationFormatTests.cs`

**Interfaces:**
- Produces (namespace `LocalScribe.Core.Assistant` — the same namespace the foundation branch uses, so `using` lines cover both):
  - `public sealed record CitationStamp(string Token, long Ms)` — `Token` is the bare stamp without brackets (`"00:01:05"`).
  - `public sealed record AnswerLineParts(string RawLine, string ClaimText, IReadOnlyList<CitationStamp> Stamps, bool IsClaim)`.
  - `public static class AssistantCitationFormat` with:
    - `public static string Format(long startMs)` — canonical `HH:MM:SS` (zero-padded 2-digit hours, invariant). This is the anchor format the context builders inject per line and the ONLY format validation emits.
    - `public static bool TryParseMs(string token, out long ms)` — accepts `HH:MM:SS`, `H:MM:SS` and `MM:SS` / `M:SS`; hours 0–99, minutes 0–59, seconds 0–59; anything else rejects.
    - `public static IReadOnlyList<CitationStamp> StampsIn(string text)` — every valid bracketed stamp in the text (used by the matter validator to scan summaries).
    - `public static IReadOnlyList<AnswerLineParts> SplitAnswer(string answerMarkdown)` — line-oriented claim extraction: a line is a CLAIM unless it is blank, a markdown header (`#`-prefixed), a section lead-in (ends with `:` after stamp/marker stripping), or empty once valid stamps and list markers (`- `, `* `, `+ `, `1. `, `1) `) are stripped. `ClaimText` = the stripped, whitespace-collapsed text; INVALID stamp-shaped tokens (e.g. `[12:99]`) are left in the text and never parsed.
- Consumes: nothing outside the BCL. Pure and exhaustively unit-tested — the whole citation feature keys off this file.

Steps:
- [x] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\AssistantCitationFormatTests.cs`:
```csharp
using LocalScribe.Core.Assistant;

public class AssistantCitationFormatTests
{
    [Fact]
    public void Format_is_zero_padded_HHMMSS()
    {
        // Design 2026-07-18 section 7.5: the canonical citation anchor. Always 2-digit hours so
        // the model sees ONE shape and the validator round-trips exactly.
        Assert.Equal("00:00:00", AssistantCitationFormat.Format(0));
        Assert.Equal("00:01:05", AssistantCitationFormat.Format(65_000));
        Assert.Equal("01:02:03", AssistantCitationFormat.Format(3_723_000));
        Assert.Equal("00:00:59", AssistantCitationFormat.Format(59_999));   // truncates, never rounds up
    }

    [Theory]
    [InlineData("00:01:05", 65_000L)]
    [InlineData("1:02:03", 3_723_000L)]    // 1-digit hours accepted
    [InlineData("12:34", 754_000L)]        // MM:SS accepted (the model may shorten)
    [InlineData("2:03", 123_000L)]
    public void TryParseMs_accepts_the_stamp_family(string token, long expected)
    {
        Assert.True(AssistantCitationFormat.TryParseMs(token, out long ms));
        Assert.Equal(expected, ms);
    }

    [Theory]
    [InlineData("12:99")]      // seconds out of range
    [InlineData("1:60:00")]    // minutes out of range
    [InlineData("123:00:00")]  // hours cap
    [InlineData("12")]
    [InlineData("a:bc")]
    [InlineData("")]
    public void TryParseMs_rejects_malformed_tokens(string token)
        => Assert.False(AssistantCitationFormat.TryParseMs(token, out _));

    [Fact]
    public void StampsIn_finds_every_valid_bracketed_stamp()
    {
        var stamps = AssistantCitationFormat.StampsIn(
            "He agreed [00:01:05] and later [1:02:03] confirmed; [12:99] is not a stamp.");
        Assert.Equal(2, stamps.Count);
        Assert.Equal(("00:01:05", 65_000L), (stamps[0].Token, stamps[0].Ms));
        Assert.Equal(("1:02:03", 3_723_000L), (stamps[1].Token, stamps[1].Ms));
    }

    [Fact]
    public void SplitAnswer_extracts_claims_with_their_stamps()
    {
        var parts = AssistantCitationFormat.SplitAnswer(
            "# Answer\n" +
            "Key statements:\n" +
            "- The parties agreed to settle for ten thousand dollars [00:01:05]\n" +
            "2. Payment is due Friday [00:02:10] [00:02:15]\n" +
            "\n" +
            "[00:03:00]");
        Assert.Equal(6, parts.Count);
        Assert.False(parts[0].IsClaim);                          // header
        Assert.False(parts[1].IsClaim);                          // section lead-in (trailing colon)
        Assert.True(parts[2].IsClaim);
        Assert.Equal("The parties agreed to settle for ten thousand dollars", parts[2].ClaimText);
        Assert.Equal(new[] { "00:01:05" }, parts[2].Stamps.Select(s => s.Token));
        Assert.True(parts[3].IsClaim);
        Assert.Equal("Payment is due Friday", parts[3].ClaimText);   // list marker + BOTH stamps stripped
        Assert.Equal(2, parts[3].Stamps.Count);
        Assert.False(parts[4].IsClaim);                          // blank
        Assert.False(parts[5].IsClaim);                          // stamp-only line has no claim text
        Assert.Single(parts[5].Stamps);
    }

    [Fact]
    public void SplitAnswer_leaves_invalid_stamp_shapes_in_the_text()
    {
        var parts = AssistantCitationFormat.SplitAnswer("The score was [12:99] in the match [00:01:05]");
        Assert.True(parts[0].IsClaim);
        Assert.Equal("The score was [12:99] in the match", parts[0].ClaimText);
        Assert.Single(parts[0].Stamps);
    }
}
```
- [x] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~AssistantCitationFormatTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\` — expected: `error CS0246: The type or namespace name 'AssistantCitationFormat' could not be found` (the `LocalScribe.Core.Assistant` namespace itself exists by now — the foundation branch created it). ACTUAL: `error CS0103: The name 'AssistantCitationFormat' does not exist in the current context` (same root cause — the `using LocalScribe.Core.Assistant;` line already resolves since the foundation branch is merged, so the compiler reports the unresolved identifier as CS0103 rather than CS0246; still the expected pre-implementation failure).
- [x] **Implement.** Create `src\LocalScribe.Core\Assistant\AssistantCitationFormat.cs`:
```csharp
using System.Globalization;
using System.Text.RegularExpressions;
namespace LocalScribe.Core.Assistant;

/// <summary>One parsed [HH:MM:SS] citation stamp: the bare token (no brackets) and its
/// milliseconds-from-session-start value (design 2026-07-18 section 7.5).</summary>
public sealed record CitationStamp(string Token, long Ms);

/// <summary>One line of an assistant answer, split for citation validation: the raw line, the
/// claim text with valid stamps and list markers stripped, the stamps found, and whether the
/// line is a factual CLAIM (headers, blank lines, section lead-ins and stamp-only lines are
/// not claims and are never flagged).</summary>
public sealed record AnswerLineParts(string RawLine, string ClaimText,
    IReadOnlyList<CitationStamp> Stamps, bool IsClaim);

/// <summary>The single source of truth for the citation stamp shape (design 2026-07-18 section
/// 7.5): context builders inject Format(row.StartMs) anchors per transcript line, the answer
/// prompt requires one per claim, and the validator parses the same family back. Pure.</summary>
public static class AssistantCitationFormat
{
    // [HH:MM:SS] / [H:MM:SS] / [MM:SS] / [M:SS]; range checks live in TryParseMs (the regex
    // over-matches e.g. [12:99] on purpose so an out-of-range token is REJECTED as a stamp and
    // left in the claim text rather than half-parsed).
    private static readonly Regex StampToken =
        new(@"\[(\d{1,3}(?::\d{1,2}){1,2})\]", RegexOptions.Compiled);
    private static readonly Regex ListMarker =
        new(@"^\s*(?:[-*+]|\d{1,3}[.)])\s+", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>Canonical anchor: zero-padded HH:MM:SS, invariant, truncated to whole seconds
    /// (never rounded - a rounded-up anchor could point past the segment start).</summary>
    public static string Format(long startMs)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, startMs));
        return string.Create(CultureInfo.InvariantCulture,
            $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}");
    }

    /// <summary>Parses a bare stamp token. Accepts HH:MM:SS / H:MM:SS / MM:SS / M:SS with
    /// hours 0-99, minutes 0-59, seconds 0-59. Never throws.</summary>
    public static bool TryParseMs(string token, out long ms)
    {
        ms = 0;
        string[] parts = token.Split(':');
        if (parts.Length is not (2 or 3)) return false;
        var nums = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length is 0 or > 2
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out nums[i]))
                return false;
        }
        int hours = parts.Length == 3 ? nums[0] : 0;
        int minutes = parts.Length == 3 ? nums[1] : nums[0];
        int seconds = parts.Length == 3 ? nums[2] : nums[1];
        if (hours > 99 || minutes > 59 || seconds > 59) return false;
        ms = ((hours * 60L + minutes) * 60L + seconds) * 1000L;
        return true;
    }

    /// <summary>Every VALID bracketed stamp in the text, in order. Used by the matter-scope
    /// validator to scan included summaries for a cited time.</summary>
    public static IReadOnlyList<CitationStamp> StampsIn(string text)
    {
        var found = new List<CitationStamp>();
        foreach (Match m in StampToken.Matches(text))
            if (TryParseMs(m.Groups[1].Value, out long ms))
                found.Add(new CitationStamp(m.Groups[1].Value, ms));
        return found;
    }

    /// <summary>Line-oriented claim extraction over the answer markdown. Non-claim lines
    /// (headers, blanks, trailing-colon lead-ins, stamp-only lines) pass through unflagged;
    /// claim lines carry their stamps and the stamp/marker-stripped text the fuzzy match runs
    /// on. Invalid stamp shapes stay in the text (they are not citations).</summary>
    public static IReadOnlyList<AnswerLineParts> SplitAnswer(string answerMarkdown)
    {
        var lines = new List<AnswerLineParts>();
        foreach (string raw in answerMarkdown.Replace("\r\n", "\n").Split('\n'))
        {
            var stamps = new List<CitationStamp>();
            string stripped = StampToken.Replace(raw, m =>
            {
                if (!TryParseMs(m.Groups[1].Value, out long ms)) return m.Value;
                stamps.Add(new CitationStamp(m.Groups[1].Value, ms));
                return " ";
            });
            string claim = MultiSpace.Replace(ListMarker.Replace(stripped, ""), " ").Trim();
            bool isClaim = claim.Length > 0
                && !raw.TrimStart().StartsWith('#')
                && !claim.EndsWith(':');
            lines.Add(new AnswerLineParts(raw, claim, stamps, isClaim));
        }
        return lines;
    }
}
```
- [x] **Run tests and see PASS.** Same filter — expected: all passed (11 including theory cases). ACTUAL: 14 passed (the theory-case tally in this note undercounts: 4 + 6 = 10 theory cases plus 4 `[Fact]` tests = 14, not 11).
- [x] **Commit.**
```
git add src/LocalScribe.Core/Assistant/AssistantCitationFormat.cs tests/LocalScribe.Core.Tests/AssistantCitationFormatTests.cs
git commit -m "feat(core): AssistantCitationFormat - canonical [HH:MM:SS] stamps + claim-line extraction

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Core — `CitationValidator` (session scope, presentation-ready verdicts)

**Files:**
- Create `src\LocalScribe.Core\Assistant\CitationValidator.cs`
- Create test `tests\LocalScribe.Core.Tests\CitationValidatorTests.cs`

**Interfaces:**
- Produces (namespace `LocalScribe.Core.Assistant`):
  - `public sealed record CitationChip(string Stamp, bool Verified, string? SessionId, int Seq, string NavTerm)` — one rendered citation pill. `Verified=false` ⇒ `SessionId=null`, `Seq=-1` (not clickable); `NavTerm` is the find-highlight term the click-through passes to `ReadViewWindow.ShowFindAt`.
  - `public sealed record AnswerLine(string Text, IReadOnlyList<CitationChip> Chips, bool IsClaim, bool Unverifiable, string? Reason)` — one rendered answer line; `Unverifiable=true` renders the "uncited" chip. **The text is NEVER dropped or rewritten** (locked rule) — flagging is additive.
  - `public sealed record ValidatedAnswer(IReadOnlyList<AnswerLine> Lines, int UnverifiableCount)`.
  - `public static class CitationValidator` with:
    - `public const long ToleranceMs = 2000;` (design §7.5: ±2 s).
    - `public const double MatchThreshold = 0.60;` — the fuzzy-match floor for BOTH scoring paths. Rationale: `ContainmentSimilarity` covers claims ≥ its internal 12-char/3-token floor (a paraphrased extractive claim inside a longer turn scores ≈0.8+; unrelated text ≈0.2-0.3); `NormalizedSimilarity` covers short replies ("Yes.") where containment returns 0 by design. 0.60 sits between those bands; a wrong verdict here only mis-FLAGS visibly (never hides content), so no golden-corpus run is required (unlike the dedup floors).
    - `public static ValidatedAnswer Validate(string answerMarkdown, IReadOnlyList<DisplayRow> rows, string sessionId)`.
    - `public static double ClaimScore(string claim, string segmentText)` and `public static string NavTerm(string claim, string rowText)` (public: tests drive them directly; no InternalsVisibleTo in this repo).
- Consumes: `AssistantCitationFormat.SplitAnswer` (Task 1), `TextDistance.ContainmentSimilarity`/`NormalizedSimilarity`/`Normalize`, `DisplayRow`/`RowSegment`.
- Resolution rule (encodes "resolve to a real segment, ±2 s, against projected rows"): a stamp resolves to a NON-marker row when `|row.StartMs - stampMs| <= ToleranceMs` OR the stamp falls inside `[row.StartMs, row.EndMs]` (a claim may cite the middle of a long turn). Among resolving rows the best `ClaimScore` wins. Per-claim verdict: verified if ANY stamp resolves with score ≥ threshold; else `Reason` = `"no citation"` (no stamps) / `"cited time not found in the record"` (no stamp resolved) / `"text does not match the cited segment"` (resolved but no score passed).

Steps:
- [x] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\CitationValidatorTests.cs`. DEVIATION: added two tests beyond the 9 embedded below — `Stamp_bearing_header_line_is_validated_not_silently_unflagged` and `Genuine_header_with_no_stamp_stays_non_claim_and_unflagged` — per a cross-task seam from Task 1's review (a `#`-prefixed line can carry a valid stamp; `SplitAnswer` still sets `IsClaim=false` for it via the header rule, but `Stamps` is populated). See the Implement step below for the corresponding `Validate` change.
```csharp
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

public class CitationValidatorTests
{
    private static DisplayRow Row(int seq, long startMs, long endMs, string name, string text) => new()
    {
        StartMs = startMs, EndMs = endMs, DisplayName = name, Text = text,
        Segments = [new RowSegment(seq, TranscriptSource.Remote, startMs, endMs, text, text, false, false)]
    };

    private static DisplayRow Marker(long startMs, string text)
        => new() { IsMarker = true, StartMs = startMs, EndMs = startMs, Text = text };

    [Fact]
    public void Verified_claim_resolves_and_carries_the_seq_for_click_through()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var v = CitationValidator.Validate(
            "- The parties agreed to settle for ten thousand dollars [00:01:05]", rows, "s1");
        var line = Assert.Single(v.Lines);
        Assert.True(line.IsClaim);
        Assert.False(line.Unverifiable);
        Assert.Equal(0, v.UnverifiableCount);
        var chip = Assert.Single(line.Chips);
        Assert.True(chip.Verified);
        Assert.Equal("s1", chip.SessionId);
        Assert.Equal(3, chip.Seq);
        Assert.Equal("thousand", chip.NavTerm);   // longest claim word (>= 4 chars) found in the row text
    }

    [Fact]
    public void Tolerance_is_exactly_two_seconds_around_the_row_start()
    {
        var rows = new[] { Row(1, 65_000, 66_500, "Alice", "We agreed to settle for ten thousand dollars") };
        var ok = CitationValidator.Validate(
            "The parties agreed to settle for ten thousand dollars [00:01:07]", rows, "s1");
        Assert.False(ok.Lines[0].Unverifiable);                       // 67s vs 65s start = +2000 ms, inside
        var bad = CitationValidator.Validate(
            "The parties agreed to settle for ten thousand dollars [00:01:08]", rows, "s1");
        Assert.True(bad.Lines[0].Unverifiable);                       // +3000 ms and past EndMs
        Assert.Equal("cited time not found in the record", bad.Lines[0].Reason);
        Assert.Equal(1, bad.UnverifiableCount);
    }

    [Fact]
    public void Stamp_inside_a_long_turn_resolves()
    {
        var rows = new[] { Row(5, 10_000, 30_000, "Bob", "We agreed to settle for ten thousand dollars and then talked about the schedule") };
        var v = CitationValidator.Validate("They agreed to settle for ten thousand dollars [00:00:20]", rows, "s1");
        Assert.False(v.Lines[0].Unverifiable);                        // 20s is inside [10s, 30s]
        Assert.Equal(5, v.Lines[0].Chips[0].Seq);
    }

    [Fact]
    public void Resolved_but_mismatched_text_is_flagged_not_dropped()
    {
        var rows = new[] { Row(1, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var v = CitationValidator.Validate("The weather was rainy on Tuesday [00:01:05]", rows, "s1");
        var line = Assert.Single(v.Lines);
        Assert.True(line.Unverifiable);
        Assert.Equal("text does not match the cited segment", line.Reason);
        Assert.Equal("The weather was rainy on Tuesday", line.Text);  // NEVER dropped (locked rule)
        Assert.False(line.Chips[0].Verified);
        Assert.Null(line.Chips[0].SessionId);
        Assert.Equal(1, v.UnverifiableCount);
    }

    [Fact]
    public void Claim_without_any_citation_is_flagged()
    {
        var rows = new[] { Row(1, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var v = CitationValidator.Validate("The parties reached an agreement", rows, "s1");
        Assert.True(v.Lines[0].Unverifiable);
        Assert.Equal("no citation", v.Lines[0].Reason);
    }

    [Fact]
    public void Markers_are_never_citation_targets()
    {
        var rows = new[] { Marker(65_000, "microphone muted") };
        var v = CitationValidator.Validate("The microphone was muted [00:01:05]", rows, "s1");
        Assert.True(v.Lines[0].Unverifiable);
        Assert.Equal("cited time not found in the record", v.Lines[0].Reason);
    }

    [Fact]
    public void Short_reply_verifies_via_whole_string_similarity()
    {
        // ContainmentSimilarity floors out below 12 chars / 3 tokens (by design); the
        // NormalizedSimilarity path must cover a genuine short extract.
        var rows = new[] { Row(7, 5_000, 5_400, "Bob", "Yes.") };
        var v = CitationValidator.Validate("Yes [00:00:05]", rows, "s1");
        Assert.False(v.Lines[0].Unverifiable);
        Assert.Equal(7, v.Lines[0].Chips[0].Seq);
    }

    [Fact]
    public void Headers_and_blank_lines_are_never_flagged()
    {
        var rows = new[] { Row(1, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var v = CitationValidator.Validate("# Answer\n\nKey points:", rows, "s1");
        Assert.Equal(0, v.UnverifiableCount);
        Assert.All(v.Lines, l => Assert.False(l.Unverifiable));
    }

    [Fact]
    public void One_good_stamp_verifies_the_claim_and_the_bad_chip_stays_visible()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var v = CitationValidator.Validate(
            "The parties agreed to settle for ten thousand dollars [00:59:59] [00:01:05]", rows, "s1");
        var line = Assert.Single(v.Lines);
        Assert.False(line.Unverifiable);
        Assert.Equal(2, line.Chips.Count);
        Assert.False(line.Chips[0].Verified);                         // the phantom stamp stays, marked
        Assert.True(line.Chips[1].Verified);
    }
}
```
- [x] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~CitationValidatorTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\` — expected: `error CS0246: The type or namespace name 'CitationValidator' could not be found`. ACTUAL: `error CS0103: The name 'CitationValidator' does not exist in the current context` (11 occurrences, same root cause as Task 1's CS0103-vs-CS0246 note — `using LocalScribe.Core.Assistant;` already resolves).
- [x] **Implement.** Create `src\LocalScribe.Core\Assistant\CitationValidator.cs`. DEVIATION from the plan's embedded body: `Validate`'s skip condition is `bool shouldValidate = part.IsClaim || part.Stamps.Count > 0;` instead of `if (!part.IsClaim)` — a stamp-bearing line is now validated even when `SplitAnswer` classified it `IsClaim=false` (the `#`-header rule), so a claim hidden behind a header prefix cannot silently bypass citation checking. The emitted `AnswerLine.IsClaim` still reports `part.IsClaim` verbatim (not hardcoded `true`) — only the decision to run validation widened, not the reported classification:
```csharp
using LocalScribe.Core.Projection;
namespace LocalScribe.Core.Assistant;

/// <summary>One rendered citation pill of an assistant answer (design 2026-07-18 section 7.5).
/// Verified chips click through to the Read view (SessionId + Seq + a find-highlight NavTerm);
/// unverified chips render visibly "unresolved" and never navigate. Serialized into chats.json
/// as part of the turn so history renders exactly what was shown when answered.</summary>
public sealed record CitationChip(string Stamp, bool Verified, string? SessionId, int Seq, string NavTerm);

/// <summary>One rendered answer line. Unverifiable claims are FLAGGED (the "uncited" chip),
/// never dropped or rewritten - the locked section 7.5 rule; Text is always the full claim.</summary>
public sealed record AnswerLine(string Text, IReadOnlyList<CitationChip> Chips,
    bool IsClaim, bool Unverifiable, string? Reason);

/// <summary>A validated answer: presentation-ready lines plus the flagged-claim count.</summary>
public sealed record ValidatedAnswer(IReadOnlyList<AnswerLine> Lines, int UnverifiableCount);

/// <summary>Session-scope citation post-validation (design 2026-07-18 section 7.5): each cited
/// [HH:MM:SS] must resolve to a real projected row (2 s tolerance on the row start, or inside
/// the row's span) AND the claim text must fuzzy-match that row's text via TextDistance. Pure -
/// the ground truth is the SAME LoadedProjection.Rows every renderer consumes.</summary>
public static class CitationValidator
{
    /// <summary>Design section 7.5: plus/minus 2 s resolution tolerance.</summary>
    public const long ToleranceMs = 2000;

    /// <summary>Fuzzy floor for both scoring paths. ContainmentSimilarity handles claims at or
    /// above its own 12-char/3-token floor (extractive claim inside a longer turn ~0.8+,
    /// unrelated text ~0.2-0.3); NormalizedSimilarity covers short replies where containment
    /// returns 0 by design. A wrong verdict only mis-flags VISIBLY (never hides content), so
    /// this constant is not golden-corpus-gated like the dedup floors.</summary>
    public const double MatchThreshold = 0.60;

    public static ValidatedAnswer Validate(string answerMarkdown, IReadOnlyList<DisplayRow> rows,
        string sessionId)
    {
        var lines = new List<AnswerLine>();
        int unverifiable = 0;
        foreach (var part in AssistantCitationFormat.SplitAnswer(answerMarkdown))
        {
            if (!part.IsClaim)
            {
                lines.Add(new AnswerLine(part.RawLine, [], false, false, null));
                continue;
            }
            var chips = new List<CitationChip>();
            bool anyResolved = false, anyVerified = false;
            foreach (var stamp in part.Stamps)
            {
                DisplayRow? best = null;
                double bestScore = -1;
                foreach (var row in rows)
                {
                    if (row.IsMarker) continue;
                    bool near = Math.Abs(row.StartMs - stamp.Ms) <= ToleranceMs
                        || (stamp.Ms >= row.StartMs && stamp.Ms <= row.EndMs);
                    if (!near) continue;
                    double score = ClaimScore(part.ClaimText, row.Text);
                    if (score > bestScore) { bestScore = score; best = row; }
                }
                if (best is null)
                {
                    chips.Add(new CitationChip(stamp.Token, false, null, -1, ""));
                    continue;
                }
                anyResolved = true;
                bool ok = bestScore >= MatchThreshold;
                anyVerified |= ok;
                int seq = best.Segments.Count > 0 ? best.Segments[0].Seq : -1;
                chips.Add(ok
                    ? new CitationChip(stamp.Token, true, sessionId, seq, NavTerm(part.ClaimText, best.Text))
                    : new CitationChip(stamp.Token, false, null, -1, ""));
            }
            string? reason = anyVerified ? null
                : part.Stamps.Count == 0 ? "no citation"
                : !anyResolved ? "cited time not found in the record"
                : "text does not match the cited segment";
            if (reason is not null) unverifiable++;
            lines.Add(new AnswerLine(part.ClaimText, chips, true, reason is not null, reason));
        }
        return new ValidatedAnswer(lines, unverifiable);
    }

    /// <summary>Best of containment (claim-inside-turn) and whole-string similarity (short
    /// replies below containment's internal floor).</summary>
    public static double ClaimScore(string claim, string segmentText)
        => Math.Max(TextDistance.ContainmentSimilarity(claim, segmentText),
                    TextDistance.NormalizedSimilarity(claim, segmentText));

    /// <summary>The find-bar highlight term for click-through: the longest normalized claim
    /// word (4+ chars) that actually appears in the matched row's text; "" when none does
    /// (ShowFindAt still scrolls to the row - the term only drives highlighting).</summary>
    public static string NavTerm(string claim, string rowText)
    {
        string best = "";
        foreach (string w in TextDistance.Normalize(claim).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (w.Length >= 4 && w.Length > best.Length
                && rowText.Contains(w, StringComparison.OrdinalIgnoreCase))
                best = w;
        return best;
    }
}
```
- [x] **Run tests and see PASS.** Same filter — expected: 9 passed. ACTUAL: 11 passed (9 embedded + the 2 seam-discriminator tests added above). Then re-run Task 1's class too (`--filter "FullyQualifiedName~AssistantCitation"`) to prove no regression — ACTUAL: 14 passed, no regression.
- [x] **Commit.**
```
git add src/LocalScribe.Core/Assistant/CitationValidator.cs tests/LocalScribe.Core.Tests/CitationValidatorTests.cs
git commit -m "feat(core): CitationValidator - per-claim [HH:MM:SS] verdicts over projected rows, flagged never dropped

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Core — `SessionQaContextBuilder` + `QaContextLadder` (full-transcript context, per-job num_ctx)

**Files:**
- Create `src\LocalScribe.Core\Assistant\SessionQaContextBuilder.cs`
- Create test `tests\LocalScribe.Core.Tests\SessionQaContextBuilderTests.cs`

**Interfaces:**
- Produces (namespace `LocalScribe.Core.Assistant`):
  - `public static class QaContextLadder` — `CtxSteps = [8192, 16384, 32768, 65536]` (design §7.2: per-job sizing; ≤16k fits a 6 GB GPU beside the weights, 32k is the operating budget, 48-64k is the CPU-RAM-backed raise), `FitGate = 0.80` (§7.4's 80% gate — trigger BEFORE overflow), `OutputReserveTokens = 1024`, and `public static int? Pick(int promptTokens)` — smallest step where `promptTokens + OutputReserveTokens <= (int)(step * FitGate)`; `null` = nothing fits → excerpt mode.
  - `public sealed record SessionQaContext(string ContextBody, IReadOnlyList<string> SpeakerNames, IReadOnlyList<DisplayRow> Rows, int? CtxTokens)` with `public bool NeedsExcerpts => CtxTokens is null;`.
  - `public static class SessionQaContextBuilder` — `public static SessionQaContext Build(IReadOnlyList<DisplayRow> rows, Func<string, int> estimateTokens, Func<string, string>? stripLine = null)`.
- Body shape (encodes the §7.5 session-scope rules): one line per NON-marker row — `[HH:MM:SS] <DisplayName>: <stripLine(row.Text)>`. Raw leading timestamps in the text are stripped through the injected `stripLine` (production binds the foundation's `AssistantInputShaper.StripLeadingTimestamps` in Task 7's `QaScopeFactory`); the CANONICAL `[HH:MM:SS]` anchor is then injected app-side — without an anchor per line, strict-extractive per-claim citation would be impossible, and the validator's citation index is exactly these `Rows` (same object). Markers are excluded (system notes, not speech). `SpeakerNames` = distinct `DisplayName`s in first-appearance order (`"Unknown speaker"` for null) — the caller feeds them to `AssistantInputShaper.BuildSpeakerPreamble`.
- `estimateTokens` is an injected seam (production binds `TokenBudget.EstimateTokens`; tests pass `s => s.Length / 2` — the §7.4 worst-case 2-chars/token arithmetic) so this task stays pure and foundation-signature-proof.

Steps:
- [x] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\SessionQaContextBuilderTests.cs`:
```csharp
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

public class SessionQaContextBuilderTests
{
    private static DisplayRow Row(int seq, long startMs, string? name, string text) => new()
    {
        StartMs = startMs, EndMs = startMs + 1000, DisplayName = name, Text = text,
        Segments = [new RowSegment(seq, TranscriptSource.Local, startMs, startMs + 1000, text, text, false, false)]
    };

    [Fact]
    public void Ladder_picks_the_smallest_step_that_fits_the_80_percent_gate()
    {
        // (int)(8192*0.8)=6553; (int)(65536*0.8)=52428; reserve 1024 (design 7.2/7.4 numbers).
        Assert.Equal(8192, QaContextLadder.Pick(5000));     // 5000+1024 <= 6553
        Assert.Equal(16384, QaContextLadder.Pick(6000));    // 7024 > 6553 -> next step
        Assert.Equal(65536, QaContextLadder.Pick(51000));   // 52024 <= 52428
        Assert.Null(QaContextLadder.Pick(52000));           // 53024 > 52428 -> excerpt mode
    }

    [Fact]
    public void Body_carries_canonical_anchors_speakers_and_skips_markers()
    {
        var rows = new List<DisplayRow>
        {
            Row(0, 5_000, "Alice", "Hello there"),
            new() { IsMarker = true, StartMs = 6_000, EndMs = 6_000, Text = "microphone muted" },
            Row(1, 65_000, "Bob", "We agreed to settle"),
            Row(2, 70_000, null, "Something else"),
        };
        var ctx = SessionQaContextBuilder.Build(rows, s => s.Length / 2);
        Assert.Equal(
            "[00:00:05] Alice: Hello there\n" +
            "[00:01:05] Bob: We agreed to settle\n" +
            "[00:01:10] Unknown speaker: Something else\n",
            ctx.ContextBody.Replace("\r\n", "\n"));
        Assert.Equal(new[] { "Alice", "Bob", "Unknown speaker" }, ctx.SpeakerNames);
        Assert.Same(rows, ctx.Rows);                         // the validator's ground truth, unchanged
        Assert.Equal(8192, ctx.CtxTokens);
        Assert.False(ctx.NeedsExcerpts);
    }

    [Fact]
    public void StripLine_seam_is_applied_to_each_row_text()
    {
        var rows = new List<DisplayRow> { Row(0, 5_000, "Alice", "12:00 Hello there") };
        var ctx = SessionQaContextBuilder.Build(rows, s => s.Length / 2,
            stripLine: s => s.StartsWith("12:00 ") ? s[6..] : s);
        Assert.Contains("[00:00:05] Alice: Hello there", ctx.ContextBody);
        Assert.DoesNotContain("12:00 Hello", ctx.ContextBody);
    }

    [Fact]
    public void Oversized_transcript_needs_excerpts()
    {
        // 200k chars -> ~100k estimated tokens at the worst-case 2 chars/token: over every step.
        var rows = new List<DisplayRow> { Row(0, 0, "Alice", new string('x', 200_000)) };
        var ctx = SessionQaContextBuilder.Build(rows, s => s.Length / 2);
        Assert.Null(ctx.CtxTokens);
        Assert.True(ctx.NeedsExcerpts);
    }
}
```
- [x] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionQaContextBuilderTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\` — expected: `error CS0246: The type or namespace name 'QaContextLadder' could not be found` (plus `SessionQaContextBuilder`). Actual: `error CS0103: The name 'QaContextLadder' does not exist in the current context` (plus `SessionQaContextBuilder`) — CS0103 not CS0246 because both names are referenced only as static-member-access expressions (`QaContextLadder.Pick(...)`, `SessionQaContextBuilder.Build(...)`), never in a type-position (e.g. a variable declaration or cast), so the compiler reports "name does not exist" rather than "type or namespace not found." Same missing-symbol failure the step intends; not a deviation in substance.
- [x] **Implement.** Create `src\LocalScribe.Core\Assistant\SessionQaContextBuilder.cs`:
```csharp
using System.Text;
using LocalScribe.Core.Projection;
namespace LocalScribe.Core.Assistant;

/// <summary>Per-job num_ctx sizing (design 2026-07-18 sections 7.2 + 7.4-7.5): the smallest
/// ladder step whose 80% gate holds the prompt plus a fixed output reserve. 8k/16k fit a 6 GB
/// GPU beside the weights; 32k is the operating budget; 64k is the CPU-RAM-backed raise; null
/// means even the raise cannot hold the transcript - fall to search-assisted excerpting
/// (section 7.5), never to silent truncation.</summary>
public static class QaContextLadder
{
    public static readonly IReadOnlyList<int> CtxSteps = [8192, 16384, 32768, 65536];
    /// <summary>Section 7.4: the fits-gate triggers at 80% of num_ctx - BEFORE overflow.</summary>
    public const double FitGate = 0.80;
    /// <summary>Tokens reserved for the generated answer.</summary>
    public const int OutputReserveTokens = 1024;

    public static int? Pick(int promptTokens)
    {
        foreach (int step in CtxSteps)
            if (promptTokens + OutputReserveTokens <= (int)(step * FitGate)) return step;
        return null;
    }
}

/// <summary>Session-scope Q&amp;A context (design 2026-07-18 section 7.5): the full projected
/// transcript, one anchored line per spoken row. CtxTokens is the per-job num_ctx pick; null
/// means the raise ladder is exhausted and the caller must build excerpts instead. Rows is the
/// SAME list the citation validator resolves against - anchor and ground truth cannot drift.</summary>
public sealed record SessionQaContext(string ContextBody, IReadOnlyList<string> SpeakerNames,
    IReadOnlyList<DisplayRow> Rows, int? CtxTokens)
{
    public bool NeedsExcerpts => CtxTokens is null;
}

/// <summary>Builds the session-scope prompt body: raw leading timestamps are stripped through
/// the injected seam (production: AssistantInputShaper.StripLeadingTimestamps), then the
/// CANONICAL [HH:MM:SS] anchor is injected app-side - the model can only cite anchors that
/// exist, and the validator parses the same family back (AssistantCitationFormat). Markers are
/// excluded (system notes, not speech). Pure; estimateTokens is a seam (production:
/// TokenBudget.EstimateTokens) so tests stay deterministic.</summary>
public static class SessionQaContextBuilder
{
    public static SessionQaContext Build(IReadOnlyList<DisplayRow> rows,
        Func<string, int> estimateTokens, Func<string, string>? stripLine = null)
    {
        stripLine ??= static s => s;
        var sb = new StringBuilder();
        var names = new List<string>();
        foreach (var row in rows)
        {
            if (row.IsMarker) continue;
            string name = row.DisplayName ?? "Unknown speaker";
            if (!names.Contains(name)) names.Add(name);
            sb.Append('[').Append(AssistantCitationFormat.Format(row.StartMs)).Append("] ")
              .Append(name).Append(": ").Append(stripLine(row.Text)).Append('\n');
        }
        string body = sb.ToString();
        return new SessionQaContext(body, names, rows, QaContextLadder.Pick(estimateTokens(body)));
    }
}
```
- [x] **Run tests and see PASS.** Same filter — expected: 4 passed. Actual: 4 passed.
- [x] **Commit.**
```
git add src/LocalScribe.Core/Assistant/SessionQaContextBuilder.cs tests/LocalScribe.Core.Tests/SessionQaContextBuilderTests.cs
git commit -m "feat(core): SessionQaContextBuilder + QaContextLadder - anchored transcript body, per-job num_ctx raise ladder

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Core — `ExcerptContextBuilder` (search-assisted excerpting, disclosed degradation)

**Files:**
- Create `src\LocalScribe.Core\Assistant\ExcerptContextBuilder.cs`
- Create test `tests\LocalScribe.Core.Tests\ExcerptContextBuilderTests.cs`

**Interfaces:**
- Produces (namespace `LocalScribe.Core.Assistant`):
  - `public sealed record ExcerptQaContext(string ContextBody, string Disclosure, IReadOnlyList<DisplayRow> IncludedRows, int CtxTokens, bool NoMatches)`.
  - `public static class ExcerptContextBuilder` with `NeighborRadius = 2` (each matching row carries ±2 surrounding spoken rows — enough turn context to answer without re-widening), `MaxQueryTerms = 8`, `ExcerptCtxTokens = 32768` (excerpt mode stays at the operating budget), `GapMarker = "[...]"`, `DisclosureText = "Answered from matching excerpts, not the full transcript."` (design §7.5's disclosed wording), and:
    - `public static ExcerptQaContext Build(string question, IReadOnlyList<DisplayRow> rows, string sessionId, Func<SearchQuery, IReadOnlyList<SearchResult>> query, Func<string, int> estimateTokens, Func<string, string>? stripLine = null)`.
- Behavior: question → `TextDistance.Normalize` → distinct terms of 3+ chars (max 8) → one `SearchQuery(term)` each through the injected seam (production: `SearchIndexService.Query` — per-term queries because the engine ANDs whitespace-split terms and a natural-language question would AND to nothing) → hits filtered to `sessionId`, `Seq >= 0` only, mapped to spoken-row indexes by exact `(Seq, PartIndex)` match on `row.Segments`, falling back to nearest `StartMs` within 2 s (rows built without payloads) → rows ranked by distinct-matched-term count desc, then position asc → windows of ±`NeighborRadius` spoken rows merged greedily in rank order while the rendered body passes `(int)(32768 * FitGate) - OutputReserveTokens`; the FIRST window is always kept. Body renders chronologically with `[...]` between non-adjacent windows, same anchored line shape as Task 3. Zero hits → `NoMatches = true` (the service refuses honestly instead of asking the model to answer from nothing).
- Consumes: `SearchQuery`/`SearchResult`/`SearchHit` (`LocalScribe.Core.Search`), `TextDistance.Normalize`, `AssistantCitationFormat.Format`, `QaContextLadder` constants.

Steps:
- [x] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\ExcerptContextBuilderTests.cs`:
```csharp
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Search;

public class ExcerptContextBuilderTests
{
    private static DisplayRow Row(int seq, long startMs, string text) => new()
    {
        StartMs = startMs, EndMs = startMs + 1000, DisplayName = "Alice", Text = text,
        Segments = [new RowSegment(seq, TranscriptSource.Local, startMs, startMs + 1000, text, text, false, false)]
    };

    private static List<DisplayRow> Rows(int count, params (int Index, string Text)[] specials)
    {
        var rows = new List<DisplayRow>();
        for (int i = 0; i < count; i++)
        {
            string text = $"ordinary line number {i}";
            foreach (var s in specials) if (s.Index == i) text = s.Text;
            rows.Add(Row(i, i * 10_000, text));
        }
        return rows;
    }

    /// <summary>Substring-matching stand-in for SearchIndexService.Query, canned to one session.</summary>
    private static Func<SearchQuery, IReadOnlyList<SearchResult>> QueryOver(
        string sessionId, IReadOnlyList<DisplayRow> rows, List<string>? queried = null)
        => q =>
        {
            queried?.Add(q.Text);
            var hits = new List<SearchHit>();
            foreach (var row in rows)
            {
                if (row.IsMarker || !row.Text.Contains(q.Text, StringComparison.OrdinalIgnoreCase)) continue;
                var seg = row.Segments[0];
                hits.Add(new SearchHit(seg.Seq, seg.PartIndex, row.StartMs, "Alice", row.Text, q.Text, false, false));
            }
            return hits.Count == 0 ? []
                : [new SearchResult(new SearchSessionEntry { SessionId = sessionId }, hits, hits.Count)];
        };

    [Fact]
    public void Single_hit_includes_two_neighbors_each_side()
    {
        var rows = Rows(10, (4, "they discussed the settlement amount at length"));
        var ex = ExcerptContextBuilder.Build("what was the settlement figure", rows, "s1",
            QueryOver("s1", rows), s => s.Length / 2);
        Assert.False(ex.NoMatches);
        Assert.Equal(5, ex.IncludedRows.Count);                                  // rows 2..6
        Assert.Equal(new long[] { 20_000, 30_000, 40_000, 50_000, 60_000 },
            ex.IncludedRows.Select(r => r.StartMs));
        Assert.DoesNotContain(ExcerptContextBuilder.GapMarker, ex.ContextBody);
        Assert.Contains("[00:00:40] Alice: they discussed the settlement amount at length", ex.ContextBody);
        Assert.Equal(ExcerptContextBuilder.DisclosureText, ex.Disclosure);
        Assert.Equal(32768, ex.CtxTokens);
    }

    [Fact]
    public void Distant_hits_render_chronologically_with_a_gap_marker()
    {
        var rows = Rows(20, (3, "the alpha clause was disputed"), (15, "the beta clause was accepted"));
        var ex = ExcerptContextBuilder.Build("alpha beta clause", rows, "s1",
            QueryOver("s1", rows), s => s.Length / 2);
        Assert.Equal(10, ex.IncludedRows.Count);                                 // 1..5 and 13..17
        Assert.Contains(ExcerptContextBuilder.GapMarker, ex.ContextBody);
        Assert.True(ex.ContextBody.IndexOf("alpha clause", StringComparison.Ordinal)
            < ex.ContextBody.IndexOf("beta clause", StringComparison.Ordinal));  // chronological
    }

    [Fact]
    public void Hits_from_other_sessions_are_ignored()
    {
        var rows = Rows(10, (4, "the settlement amount"));
        var ex = ExcerptContextBuilder.Build("settlement", rows, "s1",
            QueryOver("OTHER-session", rows), s => s.Length / 2);
        Assert.True(ex.NoMatches);
        Assert.Equal("", ex.ContextBody);
        Assert.Empty(ex.IncludedRows);
    }

    [Fact]
    public void Budget_keeps_the_best_ranked_window_when_nothing_more_fits()
    {
        // Row 4 matches BOTH terms (rank 1); row 15 matches one. A huge token estimate rejects
        // every expansion, but the first-ranked window is ALWAYS kept.
        var rows = Rows(20, (4, "alpha beta together here"), (15, "only beta here"));
        var ex = ExcerptContextBuilder.Build("alpha beta", rows, "s1",
            QueryOver("s1", rows), s => 1_000_000);
        Assert.False(ex.NoMatches);
        Assert.Equal(5, ex.IncludedRows.Count);                                  // rows 2..6 only
        Assert.Equal(20_000, ex.IncludedRows[0].StartMs);
    }

    [Fact]
    public void Short_question_words_are_never_queried()
    {
        var rows = Rows(10, (4, "the settlement amount"));
        var queried = new List<string>();
        ExcerptContextBuilder.Build("is at of settlement", rows, "s1",
            QueryOver("s1", rows, queried), s => s.Length / 2);
        Assert.Equal(new[] { "settlement" }, queried);                           // 2-char terms dropped
    }

    [Fact]
    public void Zero_hits_is_an_honest_no_matches_result()
    {
        var rows = Rows(10);
        var ex = ExcerptContextBuilder.Build("zeppelin", rows, "s1",
            QueryOver("s1", rows), s => s.Length / 2);
        Assert.True(ex.NoMatches);
    }
}
```
- [x] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~ExcerptContextBuilderTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\` — expected: `error CS0246: The type or namespace name 'ExcerptContextBuilder' could not be found`. ACTUAL: `error CS0103: The name 'ExcerptContextBuilder' does not exist in the current context` (8 occurrences — same root cause noted in Tasks 1/2: `using LocalScribe.Core.Assistant;` already resolves the namespace, so the missing-type error surfaces as CS0103 not CS0246).
- [x] **Implement.** Create `src\LocalScribe.Core\Assistant\ExcerptContextBuilder.cs` (verbatim from the plan, no deviation — the real `SearchQuery`/`SearchResult`/`SearchHit`/`SearchSessionEntry`, `DisplayRow`/`RowSegment`, and `QaContextLadder`/`AssistantCitationFormat.Format` signatures were verified against the merged master and matched the plan's Repo-facts exactly; zero identifier drift):
```csharp
using System.Text;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Search;
namespace LocalScribe.Core.Assistant;

/// <summary>Search-assisted excerpt context (design 2026-07-18 section 7.5): question terms
/// against the existing index, matching rows plus surrounding context, and a DISCLOSED
/// degradation header. NoMatches=true means the caller must refuse honestly - the model is
/// never asked to answer from an empty context.</summary>
public sealed record ExcerptQaContext(string ContextBody, string Disclosure,
    IReadOnlyList<DisplayRow> IncludedRows, int CtxTokens, bool NoMatches);

/// <summary>Builds the excerpt context when the raise ladder is exhausted (SessionQaContext
/// .NeedsExcerpts). Per-term queries (the search engine ANDs whitespace-split terms - a
/// natural-language question would AND to nothing); hits map back to projected rows by exact
/// (Seq, PartIndex), falling back to nearest StartMs within 2 s; windows are ranked by
/// distinct-matched-term count and merged within the operating budget's 80% gate. Pure over
/// the injected query/estimate seams (production: SearchIndexService.Query and
/// TokenBudget.EstimateTokens, bound in QaScopeFactory).</summary>
public static class ExcerptContextBuilder
{
    /// <summary>Spoken rows carried each side of a matching row.</summary>
    public const int NeighborRadius = 2;
    public const int MaxQueryTerms = 8;
    /// <summary>Excerpt mode stays at the operating budget (design section 7.2).</summary>
    public const int ExcerptCtxTokens = 32768;
    public const string GapMarker = "[...]";
    /// <summary>Design section 7.5: the disclosed-degradation answer header.</summary>
    public const string DisclosureText = "Answered from matching excerpts, not the full transcript.";

    public static ExcerptQaContext Build(string question, IReadOnlyList<DisplayRow> rows,
        string sessionId, Func<SearchQuery, IReadOnlyList<SearchResult>> query,
        Func<string, int> estimateTokens, Func<string, string>? stripLine = null)
    {
        stripLine ??= static s => s;
        var terms = TextDistance.Normalize(question)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 3).Distinct().Take(MaxQueryTerms).ToList();

        var spoken = new List<DisplayRow>();
        foreach (var row in rows) if (!row.IsMarker) spoken.Add(row);

        var hitTerms = new Dictionary<int, HashSet<string>>();
        foreach (string term in terms)
        {
            foreach (var result in query(new SearchQuery(term)))
            {
                if (!string.Equals(result.Session.SessionId, sessionId, StringComparison.Ordinal))
                    continue;
                foreach (var hit in result.Hits)
                {
                    if (hit.Seq < 0) continue;   // speaker-name hit: no spoken line to excerpt
                    int si = spoken.FindIndex(r => r.Segments.Any(
                        g => g.Seq == hit.Seq && g.PartIndex == hit.PartIndex));
                    if (si < 0) si = NearestByStart(spoken, hit.StartMs);
                    if (si < 0) continue;
                    if (!hitTerms.TryGetValue(si, out var set)) hitTerms[si] = set = [];
                    set.Add(term);
                }
            }
        }
        if (hitTerms.Count == 0)
            return new ExcerptQaContext("", DisclosureText, [], ExcerptCtxTokens, NoMatches: true);

        var ranked = hitTerms.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key)
            .Select(kv => kv.Key).ToList();
        int budgetTokens = (int)(ExcerptCtxTokens * QaContextLadder.FitGate)
            - QaContextLadder.OutputReserveTokens;
        var included = new SortedSet<int>();
        foreach (int center in ranked)
        {
            var candidate = new SortedSet<int>(included);
            for (int i = Math.Max(0, center - NeighborRadius);
                 i <= Math.Min(spoken.Count - 1, center + NeighborRadius); i++)
                candidate.Add(i);
            // The FIRST window is always kept - a degraded-but-disclosed answer beats none.
            if (included.Count > 0 && estimateTokens(Render(spoken, candidate, stripLine)) > budgetTokens)
                continue;
            included = candidate;
        }
        return new ExcerptQaContext(Render(spoken, included, stripLine), DisclosureText,
            included.Select(i => spoken[i]).ToList(), ExcerptCtxTokens, NoMatches: false);
    }

    private static int NearestByStart(List<DisplayRow> spoken, long startMs)
    {
        int best = -1;
        long bestDelta = long.MaxValue;
        for (int i = 0; i < spoken.Count; i++)
        {
            long d = Math.Abs(spoken[i].StartMs - startMs);
            if (d < bestDelta) { bestDelta = d; best = i; }
        }
        return bestDelta <= 2000 ? best : -1;
    }

    private static string Render(List<DisplayRow> spoken, SortedSet<int> included,
        Func<string, string> stripLine)
    {
        var sb = new StringBuilder();
        int prev = -2;
        foreach (int i in included)
        {
            if (prev >= 0 && i > prev + 1) sb.Append(GapMarker).Append('\n');
            var row = spoken[i];
            sb.Append('[').Append(AssistantCitationFormat.Format(row.StartMs)).Append("] ")
              .Append(row.DisplayName ?? "Unknown speaker").Append(": ")
              .Append(stripLine(row.Text)).Append('\n');
            prev = i;
        }
        return sb.ToString();
    }
}
```
- [x] **Run tests and see PASS.** Same filter — expected: 6 passed. ACTUAL: 6 passed.
- [x] **Commit.**
```
git add src/LocalScribe.Core/Assistant/ExcerptContextBuilder.cs tests/LocalScribe.Core.Tests/ExcerptContextBuilderTests.cs
git commit -m "feat(core): ExcerptContextBuilder - search-assisted excerpting with disclosed degradation

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: Core — `MatterQaContextBuilder` + `MatterCitationValidator` (summaries newest-first, explicit coverage)

**Files:**
- Create `src\LocalScribe.Core\Assistant\MatterQaContextBuilder.cs`
- Create `src\LocalScribe.Core\Assistant\MatterCitationValidator.cs`
- Create test `tests\LocalScribe.Core.Tests\MatterQaContextBuilderTests.cs`

**Interfaces:**
- Produces (namespace `LocalScribe.Core.Assistant`):
  - `public sealed record MatterSummarySource(string SessionId, string Title, DateTimeOffset StartedAtLocal, string? SummaryMarkdown, bool Stale)` — one tagged session's latest summary (null markdown = none generated yet). The App composition fills these from the foundation's `SummaryStore`.
  - `public sealed record MatterQaContext(string ContextBody, int CtxTokens, IReadOnlyList<string> IncludedSessionIds, IReadOnlyList<string> OmittedSessionIds, IReadOnlyList<string> MissingSummarySessionIds)`.
  - `public static class MatterQaContextBuilder` — `public const string StaleNote = "(This summary may be out of date - the transcript changed after it was generated.)"` and `public static MatterQaContext Build(IReadOnlyList<MatterSummarySource> sessions, Func<string, int> estimateTokens)`. Rules (design §7.5 matter scope): summaries only, NEVER transcripts; strict newest-first prefix within `(int)(32768 * FitGate) - OutputReserveTokens` — once a summary does not fit, it and every older one land in `Omitted` (no cherry-picking, so the disclosure reads as one cut line); the FIRST summary is always included; sessions without a summary land in `Missing` (offered for generation in the UI); stale summaries are included WITH the in-context stale note (the UI badges them too); `CtxTokens = QaContextLadder.Pick(body) ?? 32768`.
  - `public static class MatterCitationValidator` — `public static ValidatedAnswer Validate(string answerMarkdown, IReadOnlyList<MatterSummarySource> includedSummaries)`. Matter-scope resolution: a stamp resolves when the SAME time (parsed ms equality) appears as a stamp inside an included summary AND the claim fuzzy-matches that summary's text (`CitationValidator.ClaimScore`/`MatchThreshold` reused). Chips are clickable (`SessionId` set, `Seq = -1` → the click-through opens the Read view without scrolling, the existing `seq < 0` guard) ONLY when exactly one session's summary carries the cited time — a bare `HH:MM:SS` is ambiguous across sessions (recorded v1 constraint). Reasons: `"no citation"` / `"cited time not found in the included summaries"` / `"text does not match the cited summary"`.
- Consumes: Task 2's `CitationChip`/`AnswerLine`/`ValidatedAnswer`/`CitationValidator.ClaimScore`, Task 1's `StampsIn`/`SplitAnswer`, `QaContextLadder`.

Steps:
- [x] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\MatterQaContextBuilderTests.cs`. DEVIATION: added one test beyond the 6 embedded below — `Stamp_bearing_header_line_is_validated_in_matter_scope` — per the SAME cross-task seam Task 2's `CitationValidator` applied (a `#`-prefixed line can carry a valid stamp; `SplitAnswer` still sets `IsClaim=false` for it via the header rule, but `Stamps` is populated). See the Implement-the-validator step below for the corresponding `MatterCitationValidator.Validate` change.
```csharp
using LocalScribe.Core.Assistant;

public class MatterQaContextBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);

    private static MatterSummarySource Src(string id, int daysAgo, string? summary, bool stale = false)
        => new(id, "Session " + id, T0.AddDays(-daysAgo), summary, stale);

    [Fact]
    public void Builds_newest_first_with_missing_and_stale_disclosed()
    {
        var sources = new[]
        {
            Src("old", 10, "Older summary: the retainer was signed [00:10:00]"),
            Src("new", 1, "Newest summary: the parties agreed to settle [00:01:05]", stale: true),
            Src("none", 5, null),
        };
        var ctx = MatterQaContextBuilder.Build(sources, s => s.Length / 2);
        Assert.Equal(new[] { "new", "old" }, ctx.IncludedSessionIds);        // newest first
        Assert.Empty(ctx.OmittedSessionIds);
        Assert.Equal(new[] { "none" }, ctx.MissingSummarySessionIds);
        int newAt = ctx.ContextBody.IndexOf("Newest summary", StringComparison.Ordinal);
        int oldAt = ctx.ContextBody.IndexOf("Older summary", StringComparison.Ordinal);
        Assert.True(newAt >= 0 && oldAt > newAt);
        Assert.Contains("## Session new (2026-06-30)", ctx.ContextBody);     // per-session header
        Assert.Contains(MatterQaContextBuilder.StaleNote, ctx.ContextBody);  // stale disclosed in-context
        Assert.Equal(8192, ctx.CtxTokens);                                   // per-job ladder pick
    }

    [Fact]
    public void Budget_cut_is_a_strict_newest_first_prefix()
    {
        // Each summary ~30k estimated tokens (s => s.Length with 30k chars): the budget
        // (int)(32768*0.8)-1024 = 25190 holds only the first. The cut point and everything
        // older is OMITTED - explicit, never silent (design 7.5).
        string big = new string('x', 30_000);
        var sources = new[] { Src("a", 1, big), Src("b", 2, big), Src("c", 3, big) };
        var ctx = MatterQaContextBuilder.Build(sources, s => s.Length);
        Assert.Equal(new[] { "a" }, ctx.IncludedSessionIds);                 // first ALWAYS included
        Assert.Equal(new[] { "b", "c" }, ctx.OmittedSessionIds);
        Assert.Empty(ctx.MissingSummarySessionIds);
    }

    [Fact]
    public void Matter_claim_verifies_and_navigates_when_the_cited_time_is_unique()
    {
        var included = new[]
        {
            Src("a", 1, "The parties agreed to settle for ten thousand dollars [00:01:05]"),
            Src("b", 2, "The retainer was signed on Monday [00:10:00]"),
        };
        var v = MatterCitationValidator.Validate(
            "- The parties agreed to settle for ten thousand dollars [00:01:05]", included);
        var chip = Assert.Single(Assert.Single(v.Lines).Chips);
        Assert.True(chip.Verified);
        Assert.Equal("a", chip.SessionId);
        Assert.Equal(-1, chip.Seq);                        // matter scope: open, no scroll (v1)
        Assert.Equal(0, v.UnverifiableCount);
    }

    [Fact]
    public void Ambiguous_cited_time_verifies_but_does_not_navigate()
    {
        var included = new[]
        {
            Src("a", 1, "The parties agreed to settle for ten thousand dollars [00:01:05]"),
            Src("b", 2, "They agreed to settle for ten thousand dollars promptly [00:01:05]"),
        };
        var v = MatterCitationValidator.Validate(
            "The parties agreed to settle for ten thousand dollars [00:01:05]", included);
        var chip = Assert.Single(v.Lines[0].Chips);
        Assert.True(chip.Verified);
        Assert.Null(chip.SessionId);                       // ambiguous across sessions
    }

    [Fact]
    public void Unknown_cited_time_is_flagged()
    {
        var included = new[] { Src("a", 1, "The parties agreed to settle [00:01:05]") };
        var v = MatterCitationValidator.Validate("The parties agreed to settle [00:59:59]", included);
        Assert.True(v.Lines[0].Unverifiable);
        Assert.Equal("cited time not found in the included summaries", v.Lines[0].Reason);
    }

    [Fact]
    public void Mismatched_claim_text_is_flagged_not_dropped()
    {
        var included = new[] { Src("a", 1, "The parties agreed to settle for ten thousand dollars [00:01:05]") };
        var v = MatterCitationValidator.Validate("The weather was rainy on Tuesday [00:01:05]", included);
        Assert.True(v.Lines[0].Unverifiable);
        Assert.Equal("text does not match the cited summary", v.Lines[0].Reason);
        Assert.Equal("The weather was rainy on Tuesday", v.Lines[0].Text);
    }
}
```
- [x] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~MatterQaContextBuilderTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\` — expected: `error CS0246: The type or namespace name 'MatterSummarySource' could not be found`. ACTUAL: exactly that (`CS0246`, `MatterSummarySource`) — a type-position reference (local-var-less `Src(...) => new(...)` still names the type in the ctor's implicit target and the field types), unlike the CS0103 nuance noted in Tasks 1/2/3.
- [x] **Implement the builder.** Create `src\LocalScribe.Core\Assistant\MatterQaContextBuilder.cs`:
```csharp
using System.Globalization;
using System.Text;
namespace LocalScribe.Core.Assistant;

/// <summary>One tagged session's latest summary for the matter scope (design 2026-07-18
/// section 7.5): SUMMARIES only, never transcripts. Null SummaryMarkdown = no summary yet -
/// listed for generation, never silently absent. Filled by the App composition from the
/// foundation branch's SummaryStore.</summary>
public sealed record MatterSummarySource(string SessionId, string Title,
    DateTimeOffset StartedAtLocal, string? SummaryMarkdown, bool Stale);

/// <summary>Matter-scope context plus the EXPLICIT coverage lists the UI must disclose
/// (design 7.5: "the UI lists exactly which sessions are included/omitted - no silent
/// truncation").</summary>
public sealed record MatterQaContext(string ContextBody, int CtxTokens,
    IReadOnlyList<string> IncludedSessionIds, IReadOnlyList<string> OmittedSessionIds,
    IReadOnlyList<string> MissingSummarySessionIds);

/// <summary>Newest-first per-session summaries within the operating budget's 80% gate. The cut
/// is a strict prefix: once a summary does not fit, it and every older one are OMITTED (one
/// honest cut line beats cherry-picking). Stale summaries are included with an in-context
/// note. Pure; estimateTokens is the TokenBudget seam.</summary>
public static class MatterQaContextBuilder
{
    public const string StaleNote =
        "(This summary may be out of date - the transcript changed after it was generated.)";

    public static MatterQaContext Build(IReadOnlyList<MatterSummarySource> sessions,
        Func<string, int> estimateTokens)
    {
        var newestFirst = sessions
            .OrderByDescending(s => s.StartedAtLocal)
            .ThenByDescending(s => s.SessionId, StringComparer.Ordinal).ToList();
        var included = new List<string>();
        var omitted = new List<string>();
        var missing = new List<string>();
        int budget = (int)(32768 * QaContextLadder.FitGate) - QaContextLadder.OutputReserveTokens;
        var sb = new StringBuilder();
        bool full = false;
        foreach (var s in newestFirst)
        {
            if (string.IsNullOrWhiteSpace(s.SummaryMarkdown)) { missing.Add(s.SessionId); continue; }
            if (full) { omitted.Add(s.SessionId); continue; }
            string block = Block(s);
            if (included.Count > 0 && estimateTokens(sb.ToString() + block) > budget)
            {
                full = true;               // strict prefix: this one and everything older is out
                omitted.Add(s.SessionId);
                continue;
            }
            sb.Append(block);
            included.Add(s.SessionId);
        }
        string body = sb.ToString();
        return new MatterQaContext(body, QaContextLadder.Pick(estimateTokens(body)) ?? 32768,
            included, omitted, missing);
    }

    private static string Block(MatterSummarySource s)
        => "## " + s.Title + " ("
         + s.StartedAtLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")\n"
         + (s.Stale ? StaleNote + "\n" : "")
         + s.SummaryMarkdown + "\n\n";
}
```
- [x] **Implement the validator.** Create `src\LocalScribe.Core\Assistant\MatterCitationValidator.cs`. DEVIATION from the plan's embedded body: `Validate`'s skip condition is `bool shouldValidate = part.IsClaim || part.Stamps.Count > 0;` instead of `if (!part.IsClaim)` — the SAME cross-task seam Task 2's `CitationValidator` applies, kept consistent between the two validators so a claim hidden behind a `#`-prefixed header cannot bypass matter-scope citation checking. The emitted `AnswerLine.IsClaim` still reports `part.IsClaim` verbatim (not hardcoded `true`) — only the decision to run validation widened, not the reported classification:
```csharp
namespace LocalScribe.Core.Assistant;

/// <summary>Matter-scope citation post-validation (design 2026-07-18 section 7.5): the context
/// is summaries, not transcripts, so a claim verifies when its cited time appears as a stamp
/// inside an included summary AND the claim fuzzy-matches that summary's text (same TextDistance
/// thresholds as the session validator). Chips navigate (open the session's Read view, no
/// scroll - Seq stays -1) only when the cited time lives in exactly ONE session's summary; a
/// bare HH:MM:SS is ambiguous across sessions (recorded v1 constraint). Unverifiable claims
/// are FLAGGED, never dropped.</summary>
public static class MatterCitationValidator
{
    public static ValidatedAnswer Validate(string answerMarkdown,
        IReadOnlyList<MatterSummarySource> includedSummaries)
    {
        var summaryStamps = includedSummaries
            .Where(s => !string.IsNullOrWhiteSpace(s.SummaryMarkdown))
            .Select(s => (Source: s, Stamps: AssistantCitationFormat.StampsIn(s.SummaryMarkdown!)))
            .ToList();
        var lines = new List<AnswerLine>();
        int unverifiable = 0;
        foreach (var part in AssistantCitationFormat.SplitAnswer(answerMarkdown))
        {
            if (!part.IsClaim)
            {
                lines.Add(new AnswerLine(part.RawLine, [], false, false, null));
                continue;
            }
            var chips = new List<CitationChip>();
            bool anyStampFound = false, anyVerified = false;
            foreach (var stamp in part.Stamps)
            {
                var carriers = summaryStamps.Where(x => x.Stamps.Any(t => t.Ms == stamp.Ms)).ToList();
                if (carriers.Count == 0)
                {
                    chips.Add(new CitationChip(stamp.Token, false, null, -1, ""));
                    continue;
                }
                anyStampFound = true;
                var matching = carriers.Where(c => CitationValidator.ClaimScore(
                        part.ClaimText, c.Source.SummaryMarkdown!) >= CitationValidator.MatchThreshold)
                    .ToList();
                bool ok = matching.Count > 0;
                anyVerified |= ok;
                string? sessionId = ok && matching.Count == 1 ? matching[0].Source.SessionId : null;
                chips.Add(new CitationChip(stamp.Token, ok, sessionId, -1, ""));
            }
            string? reason = anyVerified ? null
                : part.Stamps.Count == 0 ? "no citation"
                : !anyStampFound ? "cited time not found in the included summaries"
                : "text does not match the cited summary";
            if (reason is not null) unverifiable++;
            lines.Add(new AnswerLine(part.ClaimText, chips, true, reason is not null, reason));
        }
        return new ValidatedAnswer(lines, unverifiable);
    }
}
```
- [x] **Run tests and see PASS.** Same filter — expected: 6 passed. ACTUAL: 7 passed (6 embedded + the 1 seam-discriminator test added above). Then re-ran Task 2's class too (`--filter "FullyQualifiedName~CitationValidator"`) to prove no regression — ACTUAL: 14 passed, no regression.
- [x] **Commit.**
```
git add src/LocalScribe.Core/Assistant/MatterQaContextBuilder.cs src/LocalScribe.Core/Assistant/MatterCitationValidator.cs tests/LocalScribe.Core.Tests/MatterQaContextBuilderTests.cs
git commit -m "feat(core): matter-scope QA context (newest-first, explicit coverage) + summary-based citation validation

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: Core — `AssistantChatStore` + `StoragePaths` assistant chat locations

**Files:**
- Create `src\LocalScribe.Core\Assistant\AssistantChatStore.cs`
- Modify `src\LocalScribe.Core\Storage\StoragePaths.cs` (append four members after the `MatterJson` member)
- Create test `tests\LocalScribe.Core.Tests\AssistantChatStoreTests.cs`

**Interfaces:**
- Produces (namespace `LocalScribe.Core.Assistant`):
  - `public sealed record AssistantChatTurn(string Id, DateTimeOffset AskedAtUtc, string Question, string AnswerMarkdown, IReadOnlyList<AnswerLine> Lines, string Model, string Backend, string PromptVersion, bool ExcerptMode, string? Disclosure, IReadOnlyList<string> IncludedSessionIds, IReadOnlyList<string> OmittedSessionIds, IReadOnlyList<string> MissingSummarySessionIds, int UnverifiableClaims)` — one Q&A exchange. `Lines` persists the VALIDATED presentation (chips + verdicts) so history renders exactly what was shown at answer time, self-contained; `Backend` is the backend `AssistantDone` reported (floor-fall provenance); `AnswerMarkdown` keeps the raw model output.
  - `public sealed record AssistantChatLog { public int SchemaVersion { get; init; } = AssistantChatStore.Version; public IReadOnlyList<AssistantChatTurn> Turns { get; init; } = []; }`
  - `public sealed class AssistantChatStore` — `public const int Version = 1;`, ctor `(string chatsJsonPath)`, `Task<AssistantChatLog> LoadAsync(CancellationToken ct)` (missing file → empty log; newer schema → `NotSupportedException` via `SchemaGuard`), `Task AppendAsync(AssistantChatTurn turn, CancellationToken ct)` (load-modify-write through `JsonFile.WriteAsync` → `AtomicFile`; creates the `assistant\` directory). Append-only by construction — no update/delete surface exists.
- Produces on `StoragePaths`: `SessionAssistantDir(id)` = `<session>\assistant`, `SessionChatsJson(id)`, `MatterAssistantDir(matterId)` = `<matters>\<matterId>\assistant`, `MatterChatsJson(matterId)` (design §7.3: chat history in the session folder / matter folder).
- Consumes: `SchemaGuard`, `JsonFile`, `AnswerLine`/`CitationChip` (Task 2 — records serialize cleanly through `LocalScribeJson.Options`).

Steps:
- [x] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\AssistantChatStoreTests.cs`:
```csharp
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Storage;

public class AssistantChatStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string ChatsPath => Path.Combine(_root, "assistant", "chats.json");

    private static AssistantChatTurn Turn(string id, string question) => new(
        id, new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.Zero), question,
        "The parties agreed [00:01:05]",
        [new AnswerLine("The parties agreed",
            [new CitationChip("00:01:05", true, "s1", 3, "agreed")], true, false, null)],
        "qwen3-4b-instruct-2507-q4_k_m.gguf", "cuda", "3", false, null, ["s1"], [], [], 0);

    [Fact]
    public async Task Missing_file_loads_as_an_empty_log()
    {
        var store = new AssistantChatStore(ChatsPath);
        var log = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(AssistantChatStore.Version, log.SchemaVersion);
        Assert.Empty(log.Turns);
    }

    [Fact]
    public async Task Append_creates_the_folder_and_roundtrips_validated_lines()
    {
        var store = new AssistantChatStore(ChatsPath);
        await store.AppendAsync(Turn("t1", "what was agreed"), CancellationToken.None);
        await store.AppendAsync(Turn("t2", "when is payment due"), CancellationToken.None);

        var log = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(2, log.Turns.Count);
        Assert.Equal("what was agreed", log.Turns[0].Question);
        Assert.Equal("cuda", log.Turns[0].Backend);                  // provenance survives
        var chip = Assert.Single(Assert.Single(log.Turns[0].Lines).Chips);
        Assert.Equal(("00:01:05", true, "s1", 3), (chip.Stamp, chip.Verified, chip.SessionId, chip.Seq));
    }

    [Fact]
    public async Task Newer_schema_is_rejected_loud()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ChatsPath)!);
        await File.WriteAllTextAsync(ChatsPath, "{\"schemaVersion\": 99, \"turns\": []}");
        var store = new AssistantChatStore(ChatsPath);
        await Assert.ThrowsAsync<NotSupportedException>(() => store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public void StoragePaths_place_chats_in_the_assistant_folders()
    {
        var paths = new StoragePaths(_root);
        Assert.Equal(Path.Combine(_root, "sessions", "s1", "assistant", "chats.json"),
            paths.SessionChatsJson("s1"));
        Assert.Equal(Path.Combine(_root, "matters", "m1", "assistant", "chats.json"),
            paths.MatterChatsJson("m1"));
    }
}
```
- [x] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~AssistantChatStoreTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\` — expected: `error CS0246: The type or namespace name 'AssistantChatStore' could not be found` (plus CS1061 on the `StoragePaths` members). ACTUAL: `error CS0246: The type or namespace name 'AssistantChatTurn' could not be found (are you missing a using directive or an assembly reference?)` — exact expected failure mode (`AssistantChatTurn` appears in type position in the test's `Turn(...)` helper signature, so CS0246 not CS0103, matching the plan verbatim).
- [x] **Add the StoragePaths members.** MERGE-RECONCILIATION APPLIED (per the plan's own NOTE at this step): `feat/llm-foundation-summaries` already merged and added `public string AssistantDir(string id) => Path.Combine(SessionDir(id), "assistant");` + `public string SummariesJson(string id) => Path.Combine(AssistantDir(id), "summaries.json");` to `StoragePaths.cs` (this is the plan's `SessionAssistantDir` under a different name). DEVIATION from the plan's embedded diff: did NOT add a duplicate `SessionAssistantDir` — reused the existing `AssistantDir(id)` and added only the three genuinely-missing members, inserted immediately after the existing assistant block (not after `MatterJson`, since that block already sits right above it):
```csharp
    public string AssistantDir(string id) => Path.Combine(SessionDir(id), "assistant");
    public string SummariesJson(string id) => Path.Combine(AssistantDir(id), "summaries.json");

    // Assistant chat sidecars (design 2026-07-18 section 7.3): derived work product, stored
    // SEPARATELY from transcript files - per-session and per-matter assistant\chats.json.
    public string SessionChatsJson(string id) => Path.Combine(AssistantDir(id), "chats.json");
    public string MatterAssistantDir(string matterId) => Path.Combine(MattersDir, matterId, "assistant");
    public string MatterChatsJson(string matterId) => Path.Combine(MatterAssistantDir(matterId), "chats.json");
```
- [x] **Implement the store.** Create `src\LocalScribe.Core\Assistant\AssistantChatStore.cs` (verbatim from the plan — the real `SchemaGuard.ReadObjectAsync/ReadVersion/RejectIfNewer`, `JsonFile.ReadAsync<T>/WriteAsync<T>`, and Task 2's `AnswerLine`/`CitationChip` signatures were verified against the branch and matched the plan's Repo-facts exactly; zero identifier drift):
```csharp
using LocalScribe.Core.Storage;
namespace LocalScribe.Core.Assistant;

/// <summary>One persisted Q&amp;A exchange (design 2026-07-18 sections 7.3 + 7.5). Lines is the
/// VALIDATED presentation (chips + verdicts) captured at answer time so history renders
/// self-contained; Backend is what AssistantDone actually reported (floor-fall provenance);
/// the coverage lists carry the matter scope's explicit included/omitted/missing disclosure.</summary>
public sealed record AssistantChatTurn(string Id, DateTimeOffset AskedAtUtc, string Question,
    string AnswerMarkdown, IReadOnlyList<AnswerLine> Lines, string Model, string Backend,
    string PromptVersion, bool ExcerptMode, string? Disclosure,
    IReadOnlyList<string> IncludedSessionIds, IReadOnlyList<string> OmittedSessionIds,
    IReadOnlyList<string> MissingSummarySessionIds, int UnverifiableClaims);

/// <summary>The chats.json shape: schema stamp + append-only turn list.</summary>
public sealed record AssistantChatLog
{
    public int SchemaVersion { get; init; } = AssistantChatStore.Version;
    public IReadOnlyList<AssistantChatTurn> Turns { get; init; } = [];
}

/// <summary>Per-scope chat history over AtomicFile (design 7.3): assistant\chats.json in the
/// session folder (session scope) or the matter folder (matter scope). Append-only by
/// construction - no update or delete surface exists. Missing file = empty log; a NEWER
/// schema fails loud (SchemaGuard pattern, same as edits.json).</summary>
public sealed class AssistantChatStore
{
    public const int Version = 1;
    private readonly string _path;

    public AssistantChatStore(string chatsJsonPath) => _path = chatsJsonPath;

    public async Task<AssistantChatLog> LoadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return new AssistantChatLog();
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "chats.json");
        return await JsonFile.ReadAsync<AssistantChatLog>(_path, ct) ?? new AssistantChatLog();
    }

    public async Task AppendAsync(AssistantChatTurn turn, CancellationToken ct)
    {
        var log = await LoadAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await JsonFile.WriteAsync(_path,
            log with { SchemaVersion = Version, Turns = [.. log.Turns, turn] }, ct);
    }
}
```
- [x] **Run tests and see PASS.** Same filter — expected: 4 passed. ACTUAL: 4 passed. Then re-ran `--filter "FullyQualifiedName~StoragePaths"` to prove no regression to the existing branch-6 assistant members — ACTUAL: 14 passed (13 pre-existing + this task's 1 new test), no regression.
- [x] **Commit.**
```
git add src/LocalScribe.Core/Assistant/AssistantChatStore.cs src/LocalScribe.Core/Storage/StoragePaths.cs tests/LocalScribe.Core.Tests/AssistantChatStoreTests.cs
git commit -m "feat(core): AssistantChatStore - append-only per-scope chats.json via AtomicFile + schema stamp

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: Core — `AssistantQaService` + `QaScopeFactory` (warm-helper lifecycle, gate, persistence) + scripted fakes

**Files:**
- Create `src\LocalScribe.Core\Assistant\QaScopeFactory.cs` (also holds `QaScope`, `AnswerWarmupPayload`, `AnswerQuestionPayload`)
- Create `src\LocalScribe.Core\Assistant\AssistantQaService.cs`
- Create `tests\LocalScribe.Core.Tests\AssistantChatFakes.cs` (the shared scripted doubles; Task 8 links them into App.Tests)
- Create tests `tests\LocalScribe.Core.Tests\AssistantQaServiceTests.cs` and `tests\LocalScribe.Core.Tests\QaScopeFactoryTests.cs`

**Interfaces:**
- Produces (namespace `LocalScribe.Core.Assistant`):
  - `public sealed record QaScope(AssistantRequest WarmupRequest, string Model, string PromptVersion, bool ExcerptMode, string? Disclosure, bool NoMatches, string? SessionId, IReadOnlyList<DisplayRow>? SessionRows, IReadOnlyList<MatterSummarySource>? MatterSummaries, IReadOnlyList<string> IncludedSessionIds, IReadOnlyList<string> OmittedSessionIds, IReadOnlyList<string> MissingSummarySessionIds)` — everything one ask needs: the warmup request (context prefill) plus the validation ground truth (`SessionRows` XOR `MatterSummaries`) and the coverage lists.
  - `public sealed record AnswerWarmupPayload(string Prompt);` / `public sealed record AnswerQuestionPayload(string Question);` — the payload envelopes (serialized camelCase via `LocalScribeJson.Options`). `// CONTRACT:` match the foundation helper's answer-op payload keys on the merged master.
  - `public sealed class QaScopeFactory` — ctor `(string modelPath, string modelFileName, string requestedBackend, Func<SearchQuery, IReadOnlyList<SearchResult>> search)`; `Task<QaScope> ForSessionAsync(string sessionId, Func<CancellationToken, Task<IReadOnlyList<DisplayRow>>> loadRows, string question, CancellationToken ct)` (full-transcript ladder first, excerpts on `NeedsExcerpts` — §7.5's raise-or-excerpt); `Task<QaScope> ForMatterAsync(IReadOnlyList<MatterSummarySource> sessions, CancellationToken ct)`. **This is the ONLY Core file that calls foundation prompt/budget/shaper members** (`TokenBudget.EstimateTokens`, `AssistantInputShaper.StripLeadingTimestamps`/`BuildSpeakerPreamble`, `AssistantPrompts.BuildAnswerPrompt`/`PromptVersion`) — every call site is `// CONTRACT:`-marked.
  - `public sealed class AssistantQaService : IAsyncDisposable` — ctor `(IAssistantChatSessionFactory factory, AssistantChatStore store, Func<CancellationToken, Task<IAsyncDisposable>> acquireEngineLease, Func<string, CancellationToken, Task<QaScope>> scopeFor, TimeProvider time)`; `Task<AssistantChatTurn> AskAsync(string question, IProgress<string>? chunks, CancellationToken ct)`. Behavior: scope per question (matter/excerpt contexts are question- or state-dependent); `NoMatches` → refuse WITHOUT touching the model; engine lease held around the model call only (production binds the foundation `AssistantGate` — queued-while-recording); warm session REUSED while `WarmupRequest.PayloadJson` is byte-identical (KV reuse — re-prefill skipped), otherwise disposed and re-warmed (context change); `AssistantChunk` streamed through `chunks`; `AssistantError`, a stream ending without `AssistantDone`, or an empty answer → throw, **nothing persisted** (§7.7), and the session is reset (a poisoned warm helper must not serve the next question); on success → validate (session rows / matter summaries), build the turn (Backend from `AssistantDone` — floor-fall provenance), `store.AppendAsync`, return. `DisposeAsync` tears the warm session down (chat close / scope change; the 5-min idle teardown is the foundation session's own duty per the contract note in Global Constraints).
- Produces (namespace `LocalScribe.Core.Tests`, in `AssistantChatFakes.cs`): `FakeAssistantChatSession` (records question payloads, replays scripted `AssistantEvent` lists, tracks `Disposed`) and `FakeAssistantChatSessionFactory` (records warmup requests, mints scripted sessions). These are THE stubs every automated test uses (locked rule: real-model runs are smoke-only).
- Consumes: Tasks 1–6 types; foundation `IAssistantChatSessionFactory`/`IAssistantChatSession`/`AssistantEvent` hierarchy/`AssistantRequest`.

**EXECUTED CONTRACT-ADAPTED per `branch7-task7-brief.md`** (the foundation branch had already merged by the
time this task ran, so the plan's `// CONTRACT:` assumed shapes below are superseded by the BINDING "Contract
resolutions" verified against the real merged sources): `QaScope` gained two trailing fields
`SpeakerPreamble`/`ContextText` so the service can rebuild the FULL prompt each ask (KV-prefix reuse) instead
of sending a bare question; `AnswerWarmupPayload`/`AnswerQuestionPayload` were DELETED (the foundation's single
v1 wire shape is `AssistantWire.PromptPayload(prompt, maxTokens)`); every `TokenBudget.EstimateTokens` call
site binds `s => TokenBudget.EstimateTokens(s.Length)` (the real member takes a char count); `Warmup(...)`
calls the real `AssistantPrompts.BuildAnswerPrompt(speakerPreamble, contextText, question)` with `question=""`
and the excerpt disclosure prepended into `contextText` (never a separate slot). See the per-step notes below
for what changed at each embedded code block; the fenced code below is left as the ORIGINAL (pre-adaptation)
plan text for history — the real committed files differ as annotated.

Steps:
- [x] **Write the fakes.** Verified `IAssistantChatSession`/`IAssistantChatSessionFactory` (`src/LocalScribe.Core/Assistant/AssistantJobRunner.cs`) match the embedded shape exactly (`StartAsync(AssistantRequest, ct) : Task<IAssistantChatSession>`; `AskAsync(string, ct) : IAsyncEnumerable<AssistantEvent>`) — the fakes below are implemented VERBATIM, no adaptation needed. Create `tests\LocalScribe.Core.Tests\AssistantChatFakes.cs`:
```csharp
using System.Runtime.CompilerServices;
using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

/// <summary>Scripted stand-in for the foundation warm-helper chat session. LOCKED rule
/// (design 2026-07-18 section 8): real-model runs are smoke-only - every automated test goes
/// through these fakes. Linked into App.Tests via Compile Include (leaf-project pattern).</summary>
public sealed class FakeAssistantChatSession : IAssistantChatSession
{
    public List<string> Questions { get; } = [];
    public Queue<IReadOnlyList<AssistantEvent>> Scripted { get; } = new();
    public bool Disposed { get; private set; }

    public async IAsyncEnumerable<AssistantEvent> AskAsync(string questionPayloadJson,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        Questions.Add(questionPayloadJson);
        IReadOnlyList<AssistantEvent> events = Scripted.Count > 0 ? Scripted.Dequeue() : [];
        foreach (AssistantEvent ev in events)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return ev;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Mints one FakeAssistantChatSession per StartAsync, recording every warmup request.
/// ScriptPerSession pre-loads the Nth new session's first ask; tests can also enqueue directly
/// on Sessions[i].Scripted for follow-up asks on a reused session.</summary>
public sealed class FakeAssistantChatSessionFactory : IAssistantChatSessionFactory
{
    public List<AssistantRequest> Warmups { get; } = [];
    public List<FakeAssistantChatSession> Sessions { get; } = [];
    public Queue<IReadOnlyList<AssistantEvent>> ScriptPerSession { get; } = new();

    public Task<IAssistantChatSession> StartAsync(AssistantRequest warmupRequest, CancellationToken ct)
    {
        Warmups.Add(warmupRequest);
        var session = new FakeAssistantChatSession();
        if (ScriptPerSession.Count > 0) session.Scripted.Enqueue(ScriptPerSession.Dequeue());
        Sessions.Add(session);
        return Task.FromResult<IAssistantChatSession>(session);
    }
}
```
(`// CONTRACT:` if `IAssistantChatSession`/`IAssistantChatSessionFactory` member shapes differ on the merged master — e.g. `StartAsync` returns `Task<IAssistantChatSession>` vs `ValueTask` — adapt the fakes to implement the REAL interface; every consumer in this plan goes through the interface, so nothing else changes.)
- [x] **Write the failing service tests.** Created `tests\LocalScribe.Core.Tests\AssistantQaServiceTests.cs`. DEVIATIONS from the embedded body below (brief OVERRIDEs 1/4/5): every `QaScope` construction gained trailing `""`, `""` for `SpeakerPreamble`/`ContextText`; the `Matter_scope_validates_against_the_included_summaries` test's combined `Assert.Equal((array,array,array), (array,array,array))` tuple assertion was split into three plain `Assert.Equal` calls — `ValueTuple<string[],...>.Equals` falls back to per-element reference equality for array elements (an xUnit/BCL mechanics trap independent of this task), so the original form never compares equal across distinct array instances even with identical contents; added one new test `Overlapping_asks_are_serialized_not_interleaved` (OVERRIDE 4, single-flight guard) using a bespoke non-shared `BlockingThenSession`/`SingleSessionFactory` test double local to this file (kept `AssistantChatFakes.cs` verbatim per the brief — none of its shared fakes can pause mid-stream).
```csharp
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Tests;

public class AssistantQaServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private AssistantChatStore Store => new(Path.Combine(_root, "assistant", "chats.json"));

    private static DisplayRow Row(int seq, long startMs, long endMs, string name, string text) => new()
    {
        StartMs = startMs, EndMs = endMs, DisplayName = name, Text = text,
        Segments = [new RowSegment(seq, TranscriptSource.Remote, startMs, endMs, text, text, false, false)]
    };

    private static QaScope SessionScope(IReadOnlyList<DisplayRow> rows, string payload = "P1") => new(
        new AssistantRequest(Op: "answer", ModelPath: @"C:\models\m.gguf", CtxTokens: 8192,
            Backend: "auto", KeepAlive: true, PayloadJson: payload),
        "m.gguf", "3", false, null, false, "s1", rows, null, ["s1"], [], []);

    private static IReadOnlyList<AssistantEvent> Script(params AssistantEvent[] events) => events;

    private sealed class CollectingProgress : IProgress<string>
    {
        public List<string> Items { get; } = [];
        public void Report(string value) => Items.Add(value);
    }

    private sealed class FakeLease(List<string> order) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { order.Add("release"); return ValueTask.CompletedTask; }
    }

    private (AssistantQaService Svc, FakeAssistantChatSessionFactory Factory, AssistantChatStore Store, List<string> Order)
        Make(Func<string, CancellationToken, Task<QaScope>> scopeFor)
    {
        var factory = new FakeAssistantChatSessionFactory();
        var store = Store;
        var order = new List<string>();
        var svc = new AssistantQaService(factory, store,
            ct => { order.Add("acquire"); return Task.FromResult<IAsyncDisposable>(new FakeLease(order)); },
            scopeFor, TimeProvider.System);
        return (svc, factory, store, order);
    }

    [Fact]
    public async Task Ask_streams_chunks_validates_citations_and_persists_the_turn()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, factory, store, order) = Make((q, ct) => Task.FromResult(SessionScope(rows)));
        factory.ScriptPerSession.Enqueue(Script(
            new AssistantChunk("The parties agreed to settle for ten thousand "),
            new AssistantChunk("dollars [00:01:05]"),
            new AssistantDone("cpu", 100, 42)));

        var progress = new CollectingProgress();
        var turn = await svc.AskAsync("what was the settlement", progress, CancellationToken.None);

        Assert.Equal(new[] { "The parties agreed to settle for ten thousand ", "dollars [00:01:05]" },
            progress.Items);
        Assert.Equal("cpu", turn.Backend);                       // AssistantDone provenance, not the request
        Assert.Equal("m.gguf", turn.Model);
        Assert.Equal(0, turn.UnverifiableClaims);
        var chip = Assert.Single(turn.Lines.Single(l => l.IsClaim).Chips);
        Assert.True(chip.Verified);
        Assert.Equal(3, chip.Seq);
        Assert.Equal(new[] { "acquire", "release" }, order);     // lease wrapped the model call
        var warmup = Assert.Single(factory.Warmups);
        Assert.True(warmup.KeepAlive);                           // warm helper (design 7.1)
        Assert.Contains("what was the settlement", Assert.Single(factory.Sessions).Questions.Single());
        Assert.Single((await store.LoadAsync(CancellationToken.None)).Turns);
    }

    [Fact]
    public async Task Warm_session_is_reused_while_the_context_payload_is_identical()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, factory, store, _) = Make((q, ct) => Task.FromResult(SessionScope(rows, "SAME")));
        factory.ScriptPerSession.Enqueue(Script(new AssistantChunk("A [00:01:05]"), new AssistantDone("cpu", 1, 1)));
        await svc.AskAsync("first", null, CancellationToken.None);
        factory.Sessions[0].Scripted.Enqueue(Script(new AssistantChunk("B [00:01:05]"), new AssistantDone("cpu", 1, 1)));
        await svc.AskAsync("second", null, CancellationToken.None);

        Assert.Single(factory.Warmups);                          // ONE prefill - KV reuse (design 7.1)
        Assert.Equal(2, factory.Sessions[0].Questions.Count);
        Assert.Equal(2, (await store.LoadAsync(CancellationToken.None)).Turns.Count);
    }

    [Fact]
    public async Task Context_change_rebuilds_the_warm_session()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        string payload = "P1";
        var (svc, factory, _, _) = Make((q, ct) => Task.FromResult(SessionScope(rows, payload)));
        factory.ScriptPerSession.Enqueue(Script(new AssistantChunk("A [00:01:05]"), new AssistantDone("cpu", 1, 1)));
        factory.ScriptPerSession.Enqueue(Script(new AssistantChunk("B [00:01:05]"), new AssistantDone("cpu", 1, 1)));

        await svc.AskAsync("first", null, CancellationToken.None);
        payload = "P2";                                          // transcript changed -> new context
        await svc.AskAsync("second", null, CancellationToken.None);

        Assert.Equal(2, factory.Warmups.Count);
        Assert.True(factory.Sessions[0].Disposed);               // stale prefill torn down
    }

    [Fact]
    public async Task Error_event_persists_nothing_and_resets_the_session()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, factory, store, _) = Make((q, ct) => Task.FromResult(SessionScope(rows)));
        factory.ScriptPerSession.Enqueue(Script(new AssistantChunk("half an ans"), new AssistantError("helper crashed")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AskAsync("q", null, CancellationToken.None));
        Assert.Contains("helper crashed", ex.Message);
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);   // section 7.7: nothing persisted
        Assert.True(factory.Sessions[0].Disposed);               // poisoned session never serves again

        factory.ScriptPerSession.Enqueue(Script(new AssistantChunk("ok [00:01:05]"), new AssistantDone("cpu", 1, 1)));
        await svc.AskAsync("retry", null, CancellationToken.None);
        Assert.Equal(2, factory.Warmups.Count);                  // re-warmed cleanly
    }

    [Fact]
    public async Task Stream_ending_without_done_is_an_error_and_persists_nothing()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var (svc, factory, store, _) = Make((q, ct) => Task.FromResult(SessionScope(rows)));
        factory.ScriptPerSession.Enqueue(Script(new AssistantChunk("half")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AskAsync("q", null, CancellationToken.None));
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);
    }

    [Fact]
    public async Task NoMatches_scope_refuses_without_touching_the_model()
    {
        var scope = new QaScope(
            new AssistantRequest(Op: "answer", ModelPath: @"C:\m.gguf", CtxTokens: 32768,
                Backend: "auto", KeepAlive: true, PayloadJson: ""),
            "m.gguf", "3", true, ExcerptContextBuilder.DisclosureText, NoMatches: true,
            "s1", [], null, ["s1"], [], []);
        var (svc, factory, store, _) = Make((q, ct) => Task.FromResult(scope));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AskAsync("q", null, CancellationToken.None));
        Assert.Empty(factory.Warmups);                           // the model was never engaged
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);
    }

    [Fact]
    public async Task Matter_scope_validates_against_the_included_summaries()
    {
        var summaries = new[]
        {
            new MatterSummarySource("a", "Session a", new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
                "The parties agreed to settle for ten thousand dollars [00:01:05]", false),
        };
        var scope = new QaScope(
            new AssistantRequest(Op: "answer", ModelPath: @"C:\m.gguf", CtxTokens: 8192,
                Backend: "auto", KeepAlive: true, PayloadJson: "M1"),
            "m.gguf", "3", false, null, false, null, null, summaries, ["a"], ["b"], ["c"]);
        var (svc, factory, store, _) = Make((q, ct) => Task.FromResult(scope));
        factory.ScriptPerSession.Enqueue(Script(
            new AssistantChunk("The parties agreed to settle for ten thousand dollars [00:01:05]"),
            new AssistantDone("cuda", 1, 1)));

        var turn = await svc.AskAsync("what was agreed", null, CancellationToken.None);
        var chip = Assert.Single(turn.Lines.Single(l => l.IsClaim).Chips);
        Assert.True(chip.Verified);
        Assert.Equal("a", chip.SessionId);
        Assert.Equal(-1, chip.Seq);
        Assert.Equal((new[] { "a" }, new[] { "b" }, new[] { "c" }),
            (turn.IncludedSessionIds.ToArray(), turn.OmittedSessionIds.ToArray(),
             turn.MissingSummarySessionIds.ToArray()));          // coverage disclosure survives into history
    }
}
```
- [x] **Run it and see it FAIL (build error).** Ran the filter — ACTUAL: exactly `error CS0246: The type or namespace name 'QaScope' could not be found` and `'AssistantQaService' could not be found`, matching the expectation.
- [x] **Implement the scope factory.** Created `src\LocalScribe.Core\Assistant\QaScopeFactory.cs`. Per brief OVERRIDE 2, NOT verbatim from the embedded body below: `AnswerWarmupPayload`/`AnswerQuestionPayload` records deleted; added `public const int WarmupMaxTokens = 16;` / `public const int MaxAnswerTokens = 1024;`; every `TokenBudget.EstimateTokens` call site binds `s => TokenBudget.EstimateTokens(s.Length)` (verified real signature: `EstimateTokens(int chars)`); `ForSessionAsync` now computes `string contextText = excerptMode && disclosure is not null ? disclosure + "\n\n" + body : body;` and threads it (plus `preamble`) into the returned `QaScope`'s two new trailing fields; `ForMatterAsync` passes `""` preamble and `mc.ContextBody` as `contextText` (the matter disclosure stays UI-only, never prepended into the model context); `Warmup(...)` rewritten to `AssistantRequest(... PayloadJson: AssistantWire.PromptPayload(AssistantPrompts.BuildAnswerPrompt(speakerPreamble, contextText, ""), WarmupMaxTokens))` — no `JsonSerializer`, no envelope record, verified against the real `AssistantWire.PromptPayload(string prompt, int maxTokens)` and `AssistantPrompts.BuildAnswerPrompt(string speakerPreamble, string contextText, string question)`. `using System.Text.Json;` and `using LocalScribe.Core.Storage;` dropped (unused once the envelope serialization was removed).
```csharp
using System.Text.Json;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;
namespace LocalScribe.Core.Assistant;

/// <summary>Everything one ask needs (design 2026-07-18 section 7.5): the warmup request (the
/// scope context, prefilled once into the warm helper) plus the validation ground truth
/// (SessionRows for session scope, MatterSummaries for matter scope) and the explicit coverage
/// lists. NoMatches=true means refuse honestly before the model is engaged.</summary>
public sealed record QaScope(AssistantRequest WarmupRequest, string Model, string PromptVersion,
    bool ExcerptMode, string? Disclosure, bool NoMatches,
    string? SessionId, IReadOnlyList<DisplayRow>? SessionRows,
    IReadOnlyList<MatterSummarySource>? MatterSummaries,
    IReadOnlyList<string> IncludedSessionIds, IReadOnlyList<string> OmittedSessionIds,
    IReadOnlyList<string> MissingSummarySessionIds);

/// <summary>Warmup payload envelope. CONTRACT: match the foundation helper's answer-op payload
/// keys on the merged master (serialized camelCase via LocalScribeJson.Options).</summary>
public sealed record AnswerWarmupPayload(string Prompt);

/// <summary>Per-question payload for the warm session (v1 single-turn to the model - only the
/// question travels; the context lives in the prefilled KV cache). CONTRACT: match the
/// foundation helper's schema.</summary>
public sealed record AnswerQuestionPayload(string Question);

/// <summary>Builds QaScopes. THE ONLY Core file of this branch that calls foundation
/// prompt/budget/shaper members - every call site is CONTRACT-marked; re-verify the real
/// signatures on the merged master and adapt identifiers only, never behavior.</summary>
public sealed class QaScopeFactory
{
    private readonly string _modelPath;
    private readonly string _modelFile;
    private readonly string _requestedBackend;
    private readonly Func<SearchQuery, IReadOnlyList<SearchResult>> _search;

    public QaScopeFactory(string modelPath, string modelFileName, string requestedBackend,
        Func<SearchQuery, IReadOnlyList<SearchResult>> search)
        => (_modelPath, _modelFile, _requestedBackend, _search)
            = (modelPath, modelFileName, requestedBackend, search);

    /// <summary>Session scope: full projected transcript with the raise ladder; excerpts only
    /// when even 64k cannot hold it (design 7.5 raise-or-excerpt, disclosed).</summary>
    public async Task<QaScope> ForSessionAsync(string sessionId,
        Func<CancellationToken, Task<IReadOnlyList<DisplayRow>>> loadRows,
        string question, CancellationToken ct)
    {
        IReadOnlyList<DisplayRow> rows = await loadRows(ct);
        // CONTRACT: TokenBudget.EstimateTokens(string) + AssistantInputShaper.StripLeadingTimestamps(string).
        var full = SessionQaContextBuilder.Build(rows, TokenBudget.EstimateTokens,
            AssistantInputShaper.StripLeadingTimestamps);
        string body;
        int ctx;
        bool excerptMode = false;
        string? disclosure = null;
        bool noMatches = false;
        if (!full.NeedsExcerpts)
        {
            body = full.ContextBody;
            ctx = full.CtxTokens!.Value;
        }
        else
        {
            var ex = ExcerptContextBuilder.Build(question, rows, sessionId, _search,
                TokenBudget.EstimateTokens, AssistantInputShaper.StripLeadingTimestamps);
            (body, ctx, excerptMode, disclosure, noMatches)
                = (ex.ContextBody, ex.CtxTokens, true, ex.Disclosure, ex.NoMatches);
        }
        // CONTRACT: BuildSpeakerPreamble over the display-name roster (assumed IEnumerable<string>).
        string preamble = AssistantInputShaper.BuildSpeakerPreamble(full.SpeakerNames);
        return new QaScope(Warmup(preamble, body, excerptMode, ctx), _modelFile,
            $"{AssistantPrompts.PromptVersion}", excerptMode, disclosure, noMatches,
            sessionId, rows, null, [sessionId], [], []);
    }

    /// <summary>Matter scope: newest-first summaries within budget, explicit coverage.</summary>
    public Task<QaScope> ForMatterAsync(IReadOnlyList<MatterSummarySource> sessions, CancellationToken ct)
    {
        // CONTRACT: TokenBudget.EstimateTokens(string).
        var mc = MatterQaContextBuilder.Build(sessions, TokenBudget.EstimateTokens);
        var includedSources = sessions.Where(s => mc.IncludedSessionIds.Contains(s.SessionId)).ToList();
        string? disclosure = mc.OmittedSessionIds.Count + mc.MissingSummarySessionIds.Count > 0
            ? "Answered from per-session summaries: " + mc.IncludedSessionIds.Count + " of "
              + sessions.Count + " tagged sessions included."
            : null;
        return Task.FromResult(new QaScope(Warmup("", mc.ContextBody, false, mc.CtxTokens),
            _modelFile, $"{AssistantPrompts.PromptVersion}", false, disclosure,
            mc.IncludedSessionIds.Count == 0, null, null, includedSources,
            mc.IncludedSessionIds, mc.OmittedSessionIds, mc.MissingSummarySessionIds));
    }

    private AssistantRequest Warmup(string speakerPreamble, string contextBody, bool excerptMode,
        int ctxTokens)
        // CONTRACT: AssistantPrompts.BuildAnswerPrompt assumed (speakerPreamble, contextBody,
        // excerptMode) -> string. The strict-extractive, per-claim-citation-required answer
        // prompt is FOUNDATION-owned wording; this branch only supplies the context. KeepAlive
        // is true - the warm-helper contract (design 7.1).
        => new(Op: "answer", ModelPath: _modelPath, CtxTokens: ctxTokens,
            Backend: _requestedBackend, KeepAlive: true,
            PayloadJson: JsonSerializer.Serialize(
                new AnswerWarmupPayload(
                    AssistantPrompts.BuildAnswerPrompt(speakerPreamble, contextBody, excerptMode)),
                LocalScribeJson.Options));
}
```
- [x] **Implement the service.** Created `src\LocalScribe.Core\Assistant\AssistantQaService.cs`. Per brief OVERRIDE 3, NOT verbatim from the embedded body below: the per-ask payload is `AssistantWire.PromptPayload(AssistantPrompts.BuildAnswerPrompt(scope.SpeakerPreamble, scope.ContextText, question), QaScopeFactory.MaxAnswerTokens)` — the FULL prompt every ask, byte-identical up to the question tail, so the helper's KV prefix (prefilled by the warmup) is actually reused; no `JsonSerializer`/`AnswerQuestionPayload` (both deleted), `using System.Text.Json;` and `using LocalScribe.Core.Storage;` dropped (unused). Per brief OVERRIDE 4 (defensive; Task-6 reviewer + branch-6 note N3 "serialize questions per session"): added `private readonly SemaphoreSlim _oneAtATime = new(1, 1);`; `AskAsync` now does `await _oneAtATime.WaitAsync(ct);` as its first line with the ENTIRE method body (scope resolution through `store.AppendAsync`/return) wrapped in `try { ... } finally { _oneAtATime.Release(); }`, so a second overlapping ask serializes behind the first rather than interleaving warm-session state or the store's read-modify-write; the semaphore is disposed in `DisposeAsync` after `ResetSessionAsync()`. Everything else (warm reuse keyed on `WarmupRequest.PayloadJson` byte-equality, reset-on-error/empty/no-done, session-rows-vs-matter-summaries validation dispatch, `Backend` from `AssistantDone`, persist-only-after-success) is VERBATIM from the embedded body.
```csharp
using System.Text;
using System.Text.Json;
using LocalScribe.Core.Storage;
namespace LocalScribe.Core.Assistant;

/// <summary>Q&amp;A orchestration over the foundation warm-helper contract (design 2026-07-18
/// sections 7.1 + 7.5 + 7.7). One instance per open chat scope. The warm session is REUSED
/// while the warmup payload is byte-identical (KV reuse - follow-up questions skip the
/// re-prefill) and rebuilt when the context changes; the engine lease (production: the
/// foundation AssistantGate - queued while a recording runs) wraps every model call; a turn is
/// persisted ONLY after a successful AssistantDone - errors, truncated streams and empty
/// answers persist NOTHING and reset the session. DisposeAsync = teardown on chat close /
/// scope change; the 5-minute idle teardown is the foundation session's own duty.</summary>
public sealed class AssistantQaService : IAsyncDisposable
{
    private readonly IAssistantChatSessionFactory _factory;
    private readonly AssistantChatStore _store;
    private readonly Func<CancellationToken, Task<IAsyncDisposable>> _acquireEngineLease;
    private readonly Func<string, CancellationToken, Task<QaScope>> _scopeFor;
    private readonly TimeProvider _time;
    private IAssistantChatSession? _session;
    private string? _warmPayload;

    public AssistantQaService(IAssistantChatSessionFactory factory, AssistantChatStore store,
        Func<CancellationToken, Task<IAsyncDisposable>> acquireEngineLease,
        Func<string, CancellationToken, Task<QaScope>> scopeFor, TimeProvider time)
        => (_factory, _store, _acquireEngineLease, _scopeFor, _time)
            = (factory, store, acquireEngineLease, scopeFor, time);

    public async Task<AssistantChatTurn> AskAsync(string question, IProgress<string>? chunks,
        CancellationToken ct)
    {
        QaScope scope = await _scopeFor(question, ct);
        if (scope.NoMatches)
            throw new InvalidOperationException(
                "There is nothing to answer from in this scope yet (no matching excerpts, or no session summaries generated).");
        string answer;
        string backend;
        try
        {
            await using IAsyncDisposable lease = await _acquireEngineLease(ct);
            if (_session is null
                || !string.Equals(_warmPayload, scope.WarmupRequest.PayloadJson, StringComparison.Ordinal))
            {
                await ResetSessionAsync();
                _session = await _factory.StartAsync(scope.WarmupRequest, ct);
                _warmPayload = scope.WarmupRequest.PayloadJson;
            }
            var sb = new StringBuilder();
            AssistantDone? done = null;
            // CONTRACT: the answer-op question payload envelope (v1 single-turn: only the
            // question travels; the context is already prefilled in the warm session).
            string payload = JsonSerializer.Serialize(new AnswerQuestionPayload(question),
                LocalScribeJson.Options);
            await foreach (AssistantEvent ev in _session.AskAsync(payload, ct))
            {
                switch (ev)
                {
                    case AssistantChunk c: sb.Append(c.Text); chunks?.Report(c.Text); break;
                    case AssistantError e: throw new InvalidOperationException(e.Message);
                    case AssistantDone d: done = d; break;
                }
            }
            if (done is null)
                throw new InvalidOperationException(
                    "The assistant ended unexpectedly - nothing was saved.");
            answer = sb.ToString();
            backend = done.Backend;
        }
        catch
        {
            await ResetSessionAsync();   // a poisoned warm session must not serve the next question
            throw;
        }
        if (answer.Trim().Length == 0)
            throw new InvalidOperationException(
                "The assistant returned an empty answer - nothing was saved.");
        ValidatedAnswer validated = scope.SessionRows is not null
            ? CitationValidator.Validate(answer, scope.SessionRows, scope.SessionId ?? "")
            : MatterCitationValidator.Validate(answer, scope.MatterSummaries ?? []);
        var turn = new AssistantChatTurn(Guid.NewGuid().ToString("N"), _time.GetUtcNow(), question,
            answer, validated.Lines, scope.Model, backend, scope.PromptVersion, scope.ExcerptMode,
            scope.Disclosure, scope.IncludedSessionIds, scope.OmittedSessionIds,
            scope.MissingSummarySessionIds, validated.UnverifiableCount);
        await _store.AppendAsync(turn, ct);
        return turn;
    }

    private async Task ResetSessionAsync()
    {
        if (_session is { } s)
        {
            _session = null;
            _warmPayload = null;
            await s.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync() => await ResetSessionAsync();
}
```
- [x] **Run the service tests and see PASS.** Same filter — ACTUAL: 8 passed (the 7 embedded tests + the new `Overlapping_asks_are_serialized_not_interleaved` from OVERRIDE 4), 0 failed.
- [x] **Write + run the scope-factory tests (real foundation types).** Created `tests\LocalScribe.Core.Tests\QaScopeFactoryTests.cs` VERBATIM from the embedded body below — both tests call the real `QaScopeFactory` unchanged; no `// CONTRACT:` call site needed identifier adaptation (every foundation signature verified matched the brief exactly). The anchored-context substring assertions (`Assert.Contains("[00:00:05] Alice: Hello there", scope.WarmupRequest.PayloadJson)`) held as predicted — the anchored body sits inside the JSON-escaped `"prompt"` value verbatim (no character in the asserted substring needs JSON escaping). ACTUAL: 2 passed, 0 failed.
```csharp
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

public class QaScopeFactoryTests
{
    private static DisplayRow Row(int seq, long startMs, string name, string text) => new()
    {
        StartMs = startMs, EndMs = startMs + 1000, DisplayName = name, Text = text,
        Segments = [new RowSegment(seq, TranscriptSource.Local, startMs, startMs + 1000, text, text, false, false)]
    };

    [Fact]
    public async Task Session_scope_builds_a_keepalive_answer_warmup_with_the_anchored_context()
    {
        var rows = new List<DisplayRow>
        {
            Row(0, 5_000, "Alice", "Hello there"),
            Row(1, 65_000, "Bob", "We agreed to settle"),
        };
        var factory = new QaScopeFactory(@"C:\models\m.gguf", "m.gguf", "auto", _ => []);
        var scope = await factory.ForSessionAsync("s1",
            ct => Task.FromResult<IReadOnlyList<DisplayRow>>(rows), "what was agreed",
            CancellationToken.None);

        Assert.False(scope.NoMatches);
        Assert.False(scope.ExcerptMode);
        Assert.Equal("answer", scope.WarmupRequest.Op);
        Assert.True(scope.WarmupRequest.KeepAlive);
        Assert.Equal(8192, scope.WarmupRequest.CtxTokens);       // tiny transcript -> smallest step
        Assert.Contains("[00:00:05] Alice: Hello there", scope.WarmupRequest.PayloadJson);
        Assert.Contains("[00:01:05] Bob: We agreed to settle", scope.WarmupRequest.PayloadJson);
        Assert.Same(rows, scope.SessionRows);
        Assert.Equal(new[] { "s1" }, scope.IncludedSessionIds);
    }

    [Fact]
    public async Task Session_scope_falls_to_disclosed_excerpts_when_the_ladder_is_exhausted()
    {
        // ~600k chars: over 64k tokens under ANY sane estimator -> the excerpt path, disclosed.
        var rows = new List<DisplayRow>();
        for (int i = 0; i < 60; i++)
            rows.Add(Row(i, i * 10_000, "Alice",
                (i == 30 ? "the settlement amount was discussed " : "") + new string('x', 10_000)));
        var searched = new List<string>();
        var factory = new QaScopeFactory(@"C:\models\m.gguf", "m.gguf", "auto", q =>
        {
            searched.Add(q.Text);
            var row = rows[30];
            var seg = row.Segments[0];
            return [new LocalScribe.Core.Search.SearchResult(
                new LocalScribe.Core.Search.SearchSessionEntry { SessionId = "s1" },
                [new LocalScribe.Core.Search.SearchHit(seg.Seq, seg.PartIndex, row.StartMs,
                    "Alice", row.Text, q.Text, false, false)], 1)];
        });
        var scope = await factory.ForSessionAsync("s1",
            ct => Task.FromResult<IReadOnlyList<DisplayRow>>(rows), "settlement amount",
            CancellationToken.None);

        Assert.True(scope.ExcerptMode);
        Assert.False(scope.NoMatches);
        Assert.Equal(ExcerptContextBuilder.DisclosureText, scope.Disclosure);
        Assert.Equal(32768, scope.WarmupRequest.CtxTokens);
        Assert.NotEmpty(searched);                                // the index was actually consulted
    }
}
```
Run: `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~QaScopeFactoryTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\` — expected: 2 passed.
- [x] **Commit.** Also ran the full assistant Core regression filter (`FullyQualifiedName~Assistant|~Citation|~QaContext|~ExcerptContext|~TokenBudget`) before committing — ACTUAL: 91 passed, 0 failed, no regression to Tasks 1–6. Build: `dotnet build LocalScribe.slnx` — 0 Warning(s), 0 Error(s).
```
git add src/LocalScribe.Core/Assistant/QaScopeFactory.cs src/LocalScribe.Core/Assistant/AssistantQaService.cs tests/LocalScribe.Core.Tests/AssistantChatFakes.cs tests/LocalScribe.Core.Tests/AssistantQaServiceTests.cs tests/LocalScribe.Core.Tests/QaScopeFactoryTests.cs
git commit -m "feat(core): AssistantQaService + QaScopeFactory - warm-helper lifecycle, engine lease, persist-only-on-success

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: App — `AssistantChatViewModel` + `ChatTurnViewModel` (streaming, chips, busy/queued, AI-draft label)

**Files:**
- Create `src\LocalScribe.App\ViewModels\AssistantChatViewModel.cs` (both classes)
- Modify `tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj` (link the Task 7 fakes)
- Create test `tests\LocalScribe.App.Tests\AssistantChatViewModelTests.cs`

**Interfaces:**
- Produces (namespace `LocalScribe.App.ViewModels`):
  - `public sealed partial class AssistantChatViewModel : ObservableObject` — scope-agnostic (both surfaces reuse it):
    - `public const string AiDraftLabel = "AI-generated draft — not a transcript; verify against the record."` (the locked label, em dash as the `\u2014` escape so the source stays ASCII) and `public const string UnavailableText = "No assistant model is installed. See Settings > Assistant to set one up."` (§7.6 disabled-with-explainer).
    - ctor `(Func<AssistantQaService?> serviceFactory, AssistantChatStore store, IUiErrorReporter reporter, Action<Action> dispatch, Func<string?>? busyReason = null)` — `serviceFactory` returns null while no model is installed (the VM flips to the explainer, §7.6); `busyReason` is the queued-state probe (production: recording-active check — the service's gate does the actual queueing, this only labels it).
    - `ObservableCollection<ChatTurnViewModel> Turns`; `[ObservableProperty]` `QuestionText` (`""`), `IsAsking`, `IsAvailable` (default true), `StatusText` (`""`), `StreamingText` (`""`); `IAsyncRelayCommand AskCommand` (gated on `!IsAsking` + non-blank question); `IRelayCommand<CitationChip> NavigateChipCommand` (raises navigation for chips with a `SessionId`; unverified/ambiguous chips no-op); `event Action<string, int, string>? CitationNavigationRequested` (sessionId, seq, navTerm — the exact triple the search-page click-through uses); `event Action<AssistantChatTurn>? TurnCompleted` (the matter surface's coverage disclosure hook); `Task LoadHistoryAsync(CancellationToken)`; `void InvalidateContext()` (disposes the current service so the next ask rebuilds context + re-warms — wired to `SessionContentChanged`); `void Shutdown()` (chat close/scope change teardown).
    - Ask flow: `service = _service ??= serviceFactory()`; null → `IsAvailable=false`, `StatusText=UnavailableText`, return (no throw). Else `IsAsking=true`, `StatusText = busyReason() ?? "Answering..."`, chunks stream into `StreamingText` via `dispatch`; success → `Turns.Add`, `QuestionText=""`, `TurnCompleted`; failure → `reporter.Report("Assistant answer", ex)` and NOTHING is added (§7.7); finally state resets.
  - `public sealed class ChatTurnViewModel` — display projection of a persisted turn: `Question`, `Lines` (`IReadOnlyList<AnswerLine>`), `Disclosure`, `UnverifiableClaims`, `AiLabel` (= `AiDraftLabel`), `ProvenanceLine` (`"{Model} · {BACKEND} · prompt {PromptVersion}"`, middle dot as `·`), `Turn` (the raw record).
- Consumes: Tasks 6–7 Core types; the Task 7 fakes (linked below) drive every test — no model, no helper.

Steps:
- [x] **Link the fakes into App.Tests.** Anchor re-verified by quoted context (no drift; the file grew two earlier `<Compile Include>` lines since the plan's `@ 7605606` grounding, so this block now sits at lines 38-39 instead of the plan's assumed position, but the quoted `FakeCaptureDeviceEnumerator.cs` line + `</ItemGroup>` matched byte-for-byte). In `tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj` the linked-doubles ItemGroup currently ends (@ 7605606):
```xml
    <Compile Include="..\LocalScribe.Core.Tests\FakeCaptureDeviceEnumerator.cs" Link="FakeCaptureDeviceEnumerator.cs" />
  </ItemGroup>
```
Replace with:
```xml
    <Compile Include="..\LocalScribe.Core.Tests\FakeCaptureDeviceEnumerator.cs" Link="FakeCaptureDeviceEnumerator.cs" />
    <!-- Matter-QA round: the scripted assistant chat doubles (locked rule: real-model runs are
         smoke-only) live in Core.Tests; linked here the same leaf-project way as the doubles
         above so AssistantChatViewModelTests / MatterAssistantViewModelTests can drive the REAL
         AssistantQaService over fakes. -->
    <Compile Include="..\LocalScribe.Core.Tests\AssistantChatFakes.cs" Link="AssistantChatFakes.cs" />
  </ItemGroup>
```
- [x] **Write the failing tests.** DEVIATION (plan-snippet compile gaps, not a contract disagreement): the embedded snippet has no `using Xunit;` or `using System.IO;` — every other file in this leaf test project needs both explicitly (no global usings cover them here), so both were added. Also CONTRACT-DRIFT: `QaScope` gained two trailing required fields (`string SpeakerPreamble, string ContextText`) since this snippet was drafted — `AssistantQaService.AskAsync` now feeds them straight into `AssistantPrompts.BuildAnswerPrompt` for the per-question payload (verified in the merged `AssistantQaService.cs`). Adapted `SessionScope`'s `new QaScope(...)` call by appending `"", ""` (behavior-preserving: no test asserts on the payload text sent to the fake session). Created `tests\LocalScribe.App.Tests\AssistantChatViewModelTests.cs`:
```csharp
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Tests;

public class AssistantChatViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) => Infos.Add(message);
    }

    private static DisplayRow Row(int seq, long startMs, long endMs, string name, string text) => new()
    {
        StartMs = startMs, EndMs = endMs, DisplayName = name, Text = text,
        Segments = [new RowSegment(seq, TranscriptSource.Remote, startMs, endMs, text, text, false, false)]
    };

    private static QaScope SessionScope(IReadOnlyList<DisplayRow> rows) => new(
        new AssistantRequest(Op: "answer", ModelPath: @"C:\m.gguf", CtxTokens: 8192,
            Backend: "auto", KeepAlive: true, PayloadJson: "P1"),
        "m.gguf", "3", false, null, false, "s1", rows, null, ["s1"], [], []);

    private (AssistantChatViewModel Vm, FakeAssistantChatSessionFactory Factory,
        AssistantChatStore Store, FakeReporter Reporter)
        MakeChat(Func<string?>? busyReason = null, bool modelInstalled = true)
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var factory = new FakeAssistantChatSessionFactory();
        var store = new AssistantChatStore(Path.Combine(_root, "assistant", "chats.json"));
        var reporter = new FakeReporter();
        Func<AssistantQaService?> serviceFactory = modelInstalled
            ? () => new AssistantQaService(factory, store,
                ct => Task.FromResult<IAsyncDisposable>(new NoopLease()),
                (q, ct) => Task.FromResult(SessionScope(rows)), TimeProvider.System)
            : () => null;
        var vm = new AssistantChatViewModel(serviceFactory, store, reporter, a => a(), busyReason);
        return (vm, factory, store, reporter);
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Ask_streams_then_lands_a_validated_turn_and_clears_the_question()
    {
        var (vm, factory, _, reporter) = MakeChat();
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("The parties agreed to settle for ten thousand "),
            new AssistantChunk("dollars [00:01:05]"),
            new AssistantDone("cuda", 10, 5),
        });
        var streamed = new List<string>();
        vm.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(vm.StreamingText) && vm.StreamingText.Length > 0) streamed.Add(vm.StreamingText); };

        vm.QuestionText = "what was the settlement";
        await vm.AskCommand.ExecuteAsync(null);

        Assert.Empty(reporter.Errors);
        var turn = Assert.Single(vm.Turns);
        Assert.Equal("what was the settlement", turn.Question);
        Assert.Equal(0, turn.UnverifiableClaims);
        var chip = Assert.Single(turn.Lines.Single(l => l.IsClaim).Chips);
        Assert.True(chip.Verified);
        Assert.Equal("", vm.QuestionText);                       // cleared on success
        Assert.False(vm.IsAsking);
        Assert.Equal("", vm.StreamingText);                      // preview cleared once the turn lands
        Assert.Contains(streamed, s => s.EndsWith("ten thousand "));   // streamed incrementally
    }

    [Fact]
    public async Task Turn_view_carries_the_ai_draft_label_and_provenance()
    {
        var (vm, factory, _, _) = MakeChat();
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("The parties agreed to settle for ten thousand dollars [00:01:05]"),
            new AssistantDone("cpu", 10, 5),
        });
        vm.QuestionText = "q";
        await vm.AskCommand.ExecuteAsync(null);

        var turn = Assert.Single(vm.Turns);
        Assert.Equal(AssistantChatViewModel.AiDraftLabel, turn.AiLabel);   // LOCKED: label on ALL chat output
        Assert.Equal("m.gguf \u00B7 CPU \u00B7 prompt 3", turn.ProvenanceLine);   // middle-dot escapes: ASCII test source
    }

    [Fact]
    public async Task No_model_flips_to_the_unavailable_explainer_without_throwing()
    {
        var (vm, _, _, reporter) = MakeChat(modelInstalled: false);
        vm.QuestionText = "q";
        await vm.AskCommand.ExecuteAsync(null);

        Assert.False(vm.IsAvailable);
        Assert.Equal(AssistantChatViewModel.UnavailableText, vm.StatusText);
        Assert.Empty(vm.Turns);
        Assert.Empty(reporter.Errors);                           // an explainer, not an error
    }

    [Fact]
    public async Task Helper_error_reports_and_adds_nothing()
    {
        var (vm, factory, store, reporter) = MakeChat();
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("half"), new AssistantError("helper crashed"),
        });
        vm.QuestionText = "q";
        await vm.AskCommand.ExecuteAsync(null);

        Assert.Single(reporter.Errors);                          // surfaced (design 7.7)
        Assert.Empty(vm.Turns);                                  // nothing persisted, nothing rendered
        Assert.Empty((await store.LoadAsync(CancellationToken.None)).Turns);
        Assert.False(vm.IsAsking);
        Assert.Equal("q", vm.QuestionText);                      // the question is NOT lost on failure
    }

    [Fact]
    public void Citation_chip_click_raises_navigation_only_for_clickable_chips()
    {
        var (vm, _, _, _) = MakeChat();
        (string Sid, int Seq, string Term)? raised = null;
        vm.CitationNavigationRequested += (sid, seq, term) => raised = (sid, seq, term);

        vm.NavigateChipCommand.Execute(new CitationChip("00:01:05", true, "s1", 3, "settle"));
        Assert.Equal(("s1", 3, "settle"), raised);

        raised = null;
        vm.NavigateChipCommand.Execute(new CitationChip("00:59:59", false, null, -1, ""));
        Assert.Null(raised);                                     // unverified chips never navigate
    }

    [Fact]
    public async Task History_loads_from_the_store()
    {
        var (vm, _, store, _) = MakeChat();
        await store.AppendAsync(new AssistantChatTurn("t1",
            new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.Zero), "old question", "old answer",
            [new AnswerLine("old answer", [], true, true, "no citation")],
            "m.gguf", "cpu", "3", false, null, ["s1"], [], [], 1), CancellationToken.None);

        await vm.LoadHistoryAsync(CancellationToken.None);
        var turn = Assert.Single(vm.Turns);
        Assert.Equal("old question", turn.Question);
        Assert.Equal(1, turn.UnverifiableClaims);                // verdicts render exactly as persisted
    }

    [Fact]
    public async Task Busy_reason_surfaces_as_the_queued_status_while_asking()
    {
        var (vm, factory, _, _) = MakeChat(
            busyReason: () => "Waiting for the recording to finish - the assistant runs one heavy engine at a time.");
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("ok [00:01:05]"), new AssistantDone("cpu", 1, 1),
        });
        var statuses = new List<string>();
        vm.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(vm.StatusText)) statuses.Add(vm.StatusText); };

        vm.QuestionText = "q";
        await vm.AskCommand.ExecuteAsync(null);
        Assert.Contains("Waiting for the recording to finish - the assistant runs one heavy engine at a time.", statuses);
        Assert.Equal("", vm.StatusText);                         // cleared after the turn
    }
}
```
- [x] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~AssistantChatViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\` — expected: `error CS0246: The type or namespace name 'AssistantChatViewModel' could not be found`. ACTUAL: exactly that (plus the plan-snippet compile gaps above: `FactAttribute`/`Fact` before `using Xunit;` was added), matching the expected pre-implementation failure.
- [x] **Implement.** Create `src\LocalScribe.App\ViewModels\AssistantChatViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Assistant;
namespace LocalScribe.App.ViewModels;

/// <summary>Scope-agnostic assistant chat (design 2026-07-18 sections 7.5-7.7): the Session
/// Details Assistant tab and the Matters Assistant tab both bind this VM over their own
/// AssistantQaService. Multi-turn UI, single-turn to the model (v1 recorded constraint - the
/// service's warm session skips the re-prefill). Failures surface via the reporter and add
/// NOTHING; the AI-draft label rides every rendered turn (locked rule).</summary>
public sealed partial class AssistantChatViewModel : ObservableObject
{
    /// <summary>LOCKED (design section 1): every rendered assistant artifact carries this.
    /// The em dash is the \u2014 escape so this source file stays ASCII (project rule).</summary>
    public const string AiDraftLabel =
        "AI-generated draft \u2014 not a transcript; verify against the record.";
    /// <summary>Section 7.6: assistant UI is disabled-with-explainer until a model exists.</summary>
    public const string UnavailableText =
        "No assistant model is installed. See Settings > Assistant to set one up.";

    private readonly Func<AssistantQaService?> _serviceFactory;
    private readonly AssistantChatStore _store;
    private readonly IUiErrorReporter _reporter;
    private readonly Action<Action> _dispatch;
    private readonly Func<string?>? _busyReason;
    private AssistantQaService? _service;

    public ObservableCollection<ChatTurnViewModel> Turns { get; } = [];
    [ObservableProperty] private string _questionText = "";
    [ObservableProperty] private bool _isAsking;
    [ObservableProperty] private bool _isAvailable = true;
    /// <summary>"" idle; "Answering..." / the queued busy reason while a question runs; the
    /// unavailable explainer when no model is installed.</summary>
    [ObservableProperty] private string _statusText = "";
    /// <summary>Live streamed answer preview; cleared once the validated turn lands.</summary>
    [ObservableProperty] private string _streamingText = "";
    public IAsyncRelayCommand AskCommand { get; }
    public IRelayCommand<CitationChip> NavigateChipCommand { get; }
    /// <summary>(sessionId, seq, navTerm) - the exact triple the search-page snippet
    /// click-through uses; seq &lt; 0 opens the read view without scrolling.</summary>
    public event Action<string, int, string>? CitationNavigationRequested;
    /// <summary>Raised after a successful turn (the matter surface refreshes its coverage
    /// disclosure from the turn's included/omitted/missing lists).</summary>
    public event Action<AssistantChatTurn>? TurnCompleted;

    public AssistantChatViewModel(Func<AssistantQaService?> serviceFactory, AssistantChatStore store,
        IUiErrorReporter reporter, Action<Action> dispatch, Func<string?>? busyReason = null)
    {
        (_serviceFactory, _store, _reporter, _dispatch, _busyReason)
            = (serviceFactory, store, reporter, dispatch, busyReason);
        AskCommand = new AsyncRelayCommand(AskAsync, () => !IsAsking && QuestionText.Trim().Length > 0);
        NavigateChipCommand = new RelayCommand<CitationChip>(chip =>
        {
            if (chip?.SessionId is { } sid)
                CitationNavigationRequested?.Invoke(sid, chip.Seq, chip.NavTerm);
        });
    }

    partial void OnQuestionTextChanged(string value) => AskCommand.NotifyCanExecuteChanged();
    partial void OnIsAskingChanged(bool value) => AskCommand.NotifyCanExecuteChanged();

    /// <summary>Persisted history renders exactly as validated at answer time (the turns carry
    /// their AnswerLines) - self-contained, no re-validation churn on load.</summary>
    public async Task LoadHistoryAsync(CancellationToken ct)
    {
        try
        {
            var log = await Task.Run(() => _store.LoadAsync(ct), ct);
            _dispatch(() =>
            {
                Turns.Clear();
                foreach (var t in log.Turns) Turns.Add(new ChatTurnViewModel(t));
            });
        }
        catch (Exception ex) { _reporter.Report("Load assistant chat history", ex); }
    }

    /// <summary>Context changed (correction save, split, re-transcription, tag change): tear the
    /// warm helper down so the next question re-prefills against the CURRENT record (the
    /// section 7.1 staleness rule). The service also self-detects payload drift - this just
    /// releases the helper promptly.</summary>
    public void InvalidateContext()
    {
        var s = Interlocked.Exchange(ref _service, null);
        if (s is not null) _ = s.DisposeAsync();
    }

    /// <summary>Chat close / scope change teardown (design 7.1).</summary>
    public void Shutdown() => InvalidateContext();

    private async Task AskAsync()
    {
        string question = QuestionText.Trim();
        if (question.Length == 0) return;
        _service ??= _serviceFactory();
        if (_service is null)
        {
            IsAvailable = false;
            StatusText = UnavailableText;
            return;
        }
        IsAvailable = true;
        IsAsking = true;
        StatusText = _busyReason?.Invoke() ?? "Answering...";
        StreamingText = "";
        try
        {
            AssistantChatTurn turn = await _service.AskAsync(question,
                new StreamProgress(this), CancellationToken.None);
            Turns.Add(new ChatTurnViewModel(turn));
            QuestionText = "";
            TurnCompleted?.Invoke(turn);
        }
        catch (Exception ex)
        {
            // Design 7.7: visible error, nothing persisted, nothing rendered; the question text
            // is deliberately kept so the user can retry.
            _reporter.Report("Assistant answer", ex);
        }
        finally
        {
            IsAsking = false;
            StatusText = "";
            StreamingText = "";
        }
    }

    private sealed class StreamProgress(AssistantChatViewModel vm) : IProgress<string>
    {
        public void Report(string value) => vm._dispatch(() => vm.StreamingText += value);
    }
}

/// <summary>Display projection of one persisted turn: question, validated lines (chips +
/// verdicts exactly as at answer time), the coverage disclosure, the AI-draft label and the
/// model-backend-prompt provenance line (middle dot escape - read-view footer precedent).</summary>
public sealed class ChatTurnViewModel
{
    public ChatTurnViewModel(AssistantChatTurn turn) => Turn = turn;

    public AssistantChatTurn Turn { get; }
    public string Question => Turn.Question;
    public IReadOnlyList<AnswerLine> Lines => Turn.Lines;
    public string? Disclosure => Turn.Disclosure;
    public int UnverifiableClaims => Turn.UnverifiableClaims;
    public string AiLabel => AssistantChatViewModel.AiDraftLabel;
    /// <summary>Middle dots as the \u00B7 escape (read-view footer precedent, ASCII source).</summary>
    public string ProvenanceLine =>
        $"{Turn.Model} \u00B7 {Turn.Backend.ToUpperInvariant()} \u00B7 prompt {Turn.PromptVersion}";
}
```
- [x] **Run tests and see PASS.** Same filter — expected: 7 passed. ACTUAL: 7 passed. Full-solution `dotnet build LocalScribe.slnx` also confirmed 0 Warning(s)/0 Error(s).
- [x] **Commit.**
```
git add src/LocalScribe.App/ViewModels/AssistantChatViewModel.cs tests/LocalScribe.App.Tests/AssistantChatViewModelTests.cs tests/LocalScribe.App.Tests/LocalScribe.App.Tests.csproj
git commit -m "feat(app): AssistantChatViewModel - streamed asks, citation chips, queued/unavailable states, AI-draft label

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 9: App — `AssistantChatPanel` control, Session Details chat pane, citation click-through wiring

**Files:**
- Create `src\LocalScribe.App\Controls\AssistantChatPanel.xaml` + `src\LocalScribe.App\Controls\AssistantChatPanel.xaml.cs`
- Modify `src\LocalScribe.App\ViewModels\MetadataEditorViewModel.cs` (one property)
- Modify `src\LocalScribe.App\SessionDetailsWindow.xaml` (chat pane in the Assistant tab)
- Modify `src\LocalScribe.App\App.xaml.cs` (four edits: composition seams, session-chat construction, close teardown, citation navigation)

**Interfaces:**
- Produces: `AssistantChatPanel` (UserControl, DataContext = `AssistantChatViewModel`; chip buttons reach `NavigateChipCommand` through a `BindingProxy` — the read-view context-menu precedent, since a row-level template cannot walk to the VM's command); `MetadataEditorViewModel.Chat : AssistantChatViewModel?` (property-injected by the composition BEFORE the window binds; null in tests — the editor exposes it and nothing else).
- Consumes: Task 8 VM; the foundation branch's composed assistant instances (chat-session factory, gate, manifest — `// CONTRACT:` block below); `openReadView` + `readViews` + `ShowFindAt` (existing); `comp.Maintenance.SessionContentChanged` (existing); `searchIndex` (existing local); `session` (the shared `SessionViewModel` local — its `State` labels the queued reason).
- No new unit test: this task is XAML + composition wiring over already-tested pieces; the gate is a 0-warning build + both suites green (incl. `XamlHygieneTests` — theme resources only, no hardcoded ARGB) + the Task 11 smoke.

Steps:
- [x] **Create the panel XAML.** CONTRACT-ADAPTED (brief OVERRIDE E): the streaming-preview `Border` also
  wraps the `TextBlock Text="{Binding StreamingText}"` in a `StackPanel` carrying
  `{x:Static vm:AssistantChatViewModel.AiDraftLabel}` above it (the branch-6 Task-13 "label on every AI
  surface, incl. in-progress" lesson), with a matching `xmlns:vm="clr-namespace:LocalScribe.App.ViewModels"`
  added. Everything else verbatim from the plan. Created `src\LocalScribe.App\Controls\AssistantChatPanel.xaml`:
```xml
<UserControl x:Class="LocalScribe.App.Controls.AssistantChatPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
             xmlns:app="clr-namespace:LocalScribe.App">
    <UserControl.Resources>
        <app:BindingProxy x:Key="ChatProxy" />
        <BooleanToVisibilityConverter x:Key="BoolToVis" />
    </UserControl.Resources>
    <DockPanel>
        <!-- Input row (bottom): gated by the VM's CanExecute, never disabled-invisible. -->
        <DockPanel DockPanel.Dock="Bottom" Margin="0,8,0,0">
            <ui:Button DockPanel.Dock="Right" Content="Ask" Appearance="Primary" Margin="8,0,0,0"
                       Command="{Binding AskCommand}" />
            <ui:TextBox PlaceholderText="Ask about this record..."
                        Text="{Binding QuestionText, UpdateSourceTrigger=PropertyChanged}" />
        </DockPanel>
        <!-- Status: "Answering..." / the queued waiting-for-recording reason / the no-model
             explainer (design 7.6/7.7 - busy and queued states are VISIBLE). -->
        <TextBlock DockPanel.Dock="Bottom" Text="{Binding StatusText}" TextWrapping="Wrap"
                   Margin="0,6,0,0" FontSize="12"
                   Foreground="{DynamicResource SystemFillColorCautionBrush}">
            <TextBlock.Style>
                <Style TargetType="TextBlock">
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding StatusText}" Value="">
                            <Setter Property="Visibility" Value="Collapsed" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </TextBlock.Style>
        </TextBlock>
        <!-- Streaming preview while a question runs. -->
        <Border DockPanel.Dock="Bottom" Background="{DynamicResource ControlFillColorSecondaryBrush}"
                CornerRadius="4" Padding="8,4" Margin="0,6,0,0">
            <Border.Style>
                <Style TargetType="Border">
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding StreamingText}" Value="">
                            <Setter Property="Visibility" Value="Collapsed" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </Border.Style>
            <TextBlock Text="{Binding StreamingText}" TextWrapping="Wrap" FontSize="12" />
        </Border>
        <!-- Turn history. -->
        <ScrollViewer VerticalScrollBarVisibility="Auto">
            <ItemsControl ItemsSource="{Binding Turns}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <StackPanel Margin="0,0,0,14">
                            <TextBlock Text="{Binding Question}" FontWeight="SemiBold" TextWrapping="Wrap" />
                            <!-- LOCKED: the AI-draft label on every rendered turn. -->
                            <TextBlock Text="{Binding AiLabel}" FontSize="11" Opacity="0.7"
                                       TextWrapping="Wrap" Margin="0,2,0,2" />
                            <!-- Disclosed degradation: excerpt-mode / coverage note. -->
                            <TextBlock Text="{Binding Disclosure}" FontSize="11" TextWrapping="Wrap"
                                       Foreground="{DynamicResource SystemFillColorCautionBrush}"
                                       Margin="0,0,0,4">
                                <TextBlock.Style>
                                    <Style TargetType="TextBlock">
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding Disclosure}" Value="{x:Null}">
                                                <Setter Property="Visibility" Value="Collapsed" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </TextBlock.Style>
                            </TextBlock>
                            <ItemsControl ItemsSource="{Binding Lines}">
                                <ItemsControl.ItemTemplate>
                                    <DataTemplate>
                                        <StackPanel Margin="0,1">
                                            <TextBlock Text="{Binding Text}" TextWrapping="Wrap" />
                                            <WrapPanel Orientation="Horizontal">
                                                <ItemsControl ItemsSource="{Binding Chips}">
                                                    <ItemsControl.ItemsPanel>
                                                        <ItemsPanelTemplate>
                                                            <WrapPanel Orientation="Horizontal" />
                                                        </ItemsPanelTemplate>
                                                    </ItemsControl.ItemsPanel>
                                                    <ItemsControl.ItemTemplate>
                                                        <DataTemplate>
                                                            <!-- Citation chip: verified -> clickable pill; unverified
                                                                 -> visibly critical, click no-ops (VM guard). -->
                                                            <Button Content="{Binding Stamp}" FontSize="11"
                                                                    Margin="0,2,4,0" Padding="6,1"
                                                                    Command="{Binding Data.NavigateChipCommand, Source={StaticResource ChatProxy}}"
                                                                    CommandParameter="{Binding}">
                                                                <Button.Style>
                                                                    <Style TargetType="Button">
                                                                        <Style.Triggers>
                                                                            <DataTrigger Binding="{Binding Verified}" Value="False">
                                                                                <Setter Property="Foreground"
                                                                                        Value="{DynamicResource SystemFillColorCriticalBrush}" />
                                                                                <Setter Property="ToolTip"
                                                                                        Value="This citation could not be verified against the record." />
                                                                            </DataTrigger>
                                                                        </Style.Triggers>
                                                                    </Style>
                                                                </Button.Style>
                                                            </Button>
                                                        </DataTemplate>
                                                    </ItemsControl.ItemTemplate>
                                                </ItemsControl>
                                                <!-- LOCKED: unverifiable claims are FLAGGED, never dropped. -->
                                                <Border CornerRadius="8" Padding="6,1" Margin="0,2,0,0"
                                                        Background="{DynamicResource SystemFillColorCriticalBackgroundBrush}"
                                                        ToolTip="{Binding Reason}"
                                                        Visibility="{Binding Unverifiable, Converter={StaticResource BoolToVis}}">
                                                    <TextBlock Text="uncited" FontSize="11"
                                                               Foreground="{DynamicResource SystemFillColorCriticalBrush}" />
                                                </Border>
                                            </WrapPanel>
                                        </StackPanel>
                                    </DataTemplate>
                                </ItemsControl.ItemTemplate>
                            </ItemsControl>
                            <TextBlock Text="{Binding ProvenanceLine}" FontSize="11" Opacity="0.6"
                                       Margin="0,4,0,0" />
                        </StackPanel>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </ScrollViewer>
    </DockPanel>
</UserControl>
```
(If `BindingProxy`'s namespace is not `LocalScribe.App`, fix the `xmlns:app` clr-namespace to match `src\LocalScribe.App\BindingProxy.cs` — re-verify by opening that file.)
- [x] **Create the code-behind.** Verbatim from the plan. Created `src\LocalScribe.App\Controls\AssistantChatPanel.xaml.cs`:
```csharp
using System.Windows.Controls;
namespace LocalScribe.App.Controls;

/// <summary>Shared chat surface (design 2026-07-18 section 7.6): Session Details' Assistant tab
/// and the Matters Assistant tab both host this over an AssistantChatViewModel DataContext.
/// The BindingProxy carries the VM's NavigateChipCommand into the chip-row templates (the
/// read-view context-menu precedent - a row template cannot walk to the VM's command).</summary>
public partial class AssistantChatPanel : UserControl
{
    public AssistantChatPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ((BindingProxy)Resources["ChatProxy"]).Data = DataContext;
    }
}
```
- [x] **Expose the chat on the editor VM.** ANCHOR-DRIFT (foundation branch, not a contract disagreement):
  the real ctor already carries a trailing `AssistantTabViewModel? assistant = null` param (added by
  `feat/llm-foundation-summaries`, merged) that this stale `@ 7605606` snippet does not show. Inserted the
  `Chat` property immediately before the REAL ctor (found by signature, not by the plan's literal text) -
  same "immediately before the ctor" placement the plan intends, unambiguous since there is exactly one
  ctor. In `src\LocalScribe.App\ViewModels\MetadataEditorViewModel.cs` the ctor currently opens (@ 7605606):
```csharp
    public MetadataEditorViewModel(MaintenanceService maintenance, SessionViewModel session,
        IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time,
        Func<string, bool> confirm)
```
Immediately BEFORE that ctor insert:
```csharp
    /// <summary>Session-scope assistant chat (design 2026-07-18 section 7.6), property-injected
    /// by the App composition (openSessionDetails) BEFORE the window binds - so a plain get/set
    /// suffices (no INPC needed). Null in tests and when the assistant stack is not composed;
    /// the editor exposes it for the Assistant tab and touches nothing else on it.</summary>
    public AssistantChatViewModel? Chat { get; set; }

```
- [x] **Add the chat pane to the Assistant tab.** Verified: the Assistant tab exists on the merged master
  (its content is `ScrollViewer > StackPanel` per the plan's assumption, ending with the summary `ui:Card`);
  appended the two elements as the StackPanel's last children, after `</ui:Card>` and before the closing
  `</StackPanel>` (FALLBACK not needed). `xmlns:controls` was not yet on the root and was added. In
  `src\LocalScribe.App\SessionDetailsWindow.xaml`, locate the `<TabItem Header="Assistant">` that `feat/llm-foundation-summaries` added (it hosts the summary UI; @ 7605606 only "Details" and "Speakers" tabs exist — this tab WILL be there on the merged master). Append as the LAST children of that tab's content `StackPanel` (the window's tabs each wrap a `ScrollViewer` > `StackPanel`; if the foundation tab's skeleton differs, insert at the equivalent end-of-content position). Also add `xmlns:controls="clr-namespace:LocalScribe.App.Controls"` to the `<ui:FluentWindow ...>` root attributes if the file does not have it yet:
```xml
                        <!-- Matter-QA round (design 2026-07-18 sections 7.5-7.6): session chat.
                             Deliberately NOT gated by IsEditable - asking about the record is
                             read-only derived work; recording-time asks queue visibly on the
                             engine gate instead. -->
                        <TextBlock Text="Ask about this session" FontWeight="SemiBold" Margin="0,12,0,4" />
                        <controls:AssistantChatPanel DataContext="{Binding Chat}" MinHeight="260" />
```
FALLBACK (only if no Assistant tab exists on the merged master — i.e. the foundation branch shipped without its Session Details summary portion): insert a whole tab before the closing `</TabControl>` instead:
```xml
            <TabItem Header="Assistant">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel Margin="4,8,4,4">
                        <TextBlock Text="Ask about this session" FontWeight="SemiBold" Margin="0,0,0,4" />
                        <controls:AssistantChatPanel DataContext="{Binding Chat}" MinHeight="260" />
                    </StackPanel>
                </ScrollViewer>
            </TabItem>
```
- [x] **App.xaml.cs Edit 1 — composition seams.** CONTRACT-ADAPTED per brief OVERRIDEs A+B (the plan's
  `assistantChatFactory`/`assistantGate`/`assistantManifest` bare-local assumptions don't exist on the
  merged master): added `AssistantGate AssistantGate` to the `AppComposition` record + passed the existing
  `assistantGate` local through `Build()`'s return (CompositionRoot.cs) so chat REUSES the summarizer's one
  gate; resolved the manifest once off-thread via `comp.AssistantModels.GetAsync`; built `qaScopeFactoryFor`
  off `AssistantModelInfo.FilePath` (no `.Backend` on that record) requesting backend `"auto"`; wrapped
  `AssistantGate.EnterAsync(null, ct)`'s sync `IDisposable` lease as `IAsyncDisposable` via a new
  `SyncLeaseAsAsync` adapter (own file, `src\LocalScribe.App\SyncLeaseAsAsync.cs`, to keep App.xaml.cs
  growth down per the brief's own suggestion). Anchor (`sessionDetailsWindows`/`sessionDetailsEditors`)
  re-verified byte-for-byte at its real (shifted) location. The Session Details maps block currently reads (@ 7605606):
```csharp
        var sessionDetailsWindows = new Dictionary<string, SessionDetailsWindow>(StringComparer.Ordinal);
        var sessionDetailsEditors = new Dictionary<string, ViewModels.MetadataEditorViewModel>(StringComparer.Ordinal);
```
Immediately AFTER those two lines insert:
```csharp

        // ----- Matter-QA round (design 2026-07-18 sections 7.5-7.7): assistant chat seams -----
        // navigateToCitation is ASSIGNED after openReadView exists (a lambda cannot reference a
        // local declared later in this method - the file's documented hoisting rule); chat VMs
        // close over this mutable slot, never over openReadView directly.
        Action<string, int, string>? navigateToCitation = null;
        // CONTRACT (foundation branch): REUSE the instances the foundation composed for the
        // Session Details summary tab - the chat-session factory (IAssistantChatSessionFactory),
        // the AssistantGate, and the installed-model resolution (AssistantModelManifest
        // .DefaultModel -> AssistantModelRef(File, Sha256, Backend), plus however the summary
        // wiring turns File into AssistantRequest.ModelPath). Locate them in THIS file on the
        // merged master (grep IAssistantChatSessionFactory / AssistantGate /
        // AssistantModelManifest) and adapt the three identifiers below to the real locals;
        // never compose a second helper stack. Assumed locals:
        //   assistantChatFactory : IAssistantChatSessionFactory
        //   assistantGate        : AssistantGate (acquire assumed AcquireAsync(ct) ->
        //                          Task<IAsyncDisposable>; wrap an IDisposable lease if needed)
        //   assistantManifest    : AssistantModelManifest? (null / no DefaultModel = no model
        //                          installed -> the chat VMs show the explainer)
        Func<LocalScribe.Core.Assistant.QaScopeFactory?> qaScopeFactoryFor = () =>
            assistantManifest?.DefaultModel is LocalScribe.Core.Assistant.AssistantModelRef m
                ? new LocalScribe.Core.Assistant.QaScopeFactory(
                    m.File, System.IO.Path.GetFileName(m.File), m.Backend,
                    q => searchIndex.Query(q))
                : null;
        Func<CancellationToken, Task<IAsyncDisposable>> acquireAssistantLease =
            async ct => await assistantGate.AcquireAsync(ct);
        Func<string?> assistantBusyReason = () =>
            session.State != LocalScribe.Core.Live.SessionState.Idle
                ? "Waiting for the recording to finish - the assistant runs one heavy engine at a time."
                : null;
```
- [x] **App.xaml.cs Edit 2 — session chat construction.** CONTRACT-ADAPTED per brief OVERRIDE C: used
  `comp.AssistantChat` (the real `IAssistantChatSessionFactory` on `AppComposition`) wherever the plan named
  the bare local `assistantChatFactory`; everything else (the `AssistantQaService`/`QaScopeFactory`/
  `RunForSessionAsync`/`SessionProjectionLoader.LoadAsync` call chain, `AssistantChatViewModel` ctor order)
  implemented verbatim and verified signature-for-signature against the real merged Core/App types. Anchor
  (`detailEditor.Saved += comp.Windows.NotifyRosterChanged;` immediately followed by `var window = new
  SessionDetailsWindow(...)`) re-verified byte-for-byte. Inside the `openSessionDetails` factory, the wiring currently ends with (@ 7605606, quoted context):
```csharp
            detailEditor.Saved += comp.Windows.NotifyRosterChanged;
            var window = new SessionDetailsWindow(detailEditor, sessionId, comp.Windows, windowState,
                comp.Settings);
```
Between `detailEditor.Saved += comp.Windows.NotifyRosterChanged;` and `var window = ...` insert:
```csharp
            // Matter-QA round: session-scope chat (design 7.5/7.6). The scope reloads the
            // projection PER QUESTION through the same per-session gate every reader uses, so
            // answers always run against the current record; the warm session is reused while
            // that context is byte-identical (KV reuse) and torn down on change.
            var chatStore = new LocalScribe.Core.Assistant.AssistantChatStore(
                comp.Paths.SessionChatsJson(sessionId));
            Func<LocalScribe.Core.Assistant.AssistantQaService?> chatServiceFactory = () =>
                qaScopeFactoryFor() is { } scopes
                    ? new LocalScribe.Core.Assistant.AssistantQaService(assistantChatFactory,
                        chatStore, acquireAssistantLease,
                        (question, ct) => scopes.ForSessionAsync(sessionId,
                            inner => comp.Maintenance.RunForSessionAsync(sessionId, async gated =>
                                (IReadOnlyList<LocalScribe.Core.Projection.DisplayRow>)
                                (await LocalScribe.Core.Storage.SessionProjectionLoader.LoadAsync(
                                    comp.Paths, comp.Settings.Current, TimeProvider.System,
                                    sessionId, gated)).Rows,
                                inner),
                            question, ct),
                        TimeProvider.System)
                    : null;
            var chatVm = new ViewModels.AssistantChatViewModel(chatServiceFactory, chatStore,
                errors, dispatch, assistantBusyReason);
            chatVm.CitationNavigationRequested += (sid, seq, term)
                => navigateToCitation?.Invoke(sid, seq, term);
            Action<string> chatInvalidate = id => { if (id == sessionId) chatVm.InvalidateContext(); };
            comp.Maintenance.SessionContentChanged += chatInvalidate;
            detailEditor.Chat = chatVm;
            _ = chatVm.LoadHistoryAsync(CancellationToken.None);
```
- [x] **App.xaml.cs Edit 3 — teardown on close.** Implemented verbatim (OVERRIDE D); anchor re-verified
  byte-for-byte at its real location. The factory's close handler currently reads:
```csharp
            window.Closed += (_, _) =>
            {
                sessionDetailsWindows.Remove(sessionId);
                sessionDetailsEditors.Remove(sessionId);
                detailEditor.Dispose();
                _ = sessionsVm.RefreshRowAsync(sessionId);   // Stage 5.4 4.4: backstop if a save landed late / X was used
            };
```
Replace with:
```csharp
            window.Closed += (_, _) =>
            {
                sessionDetailsWindows.Remove(sessionId);
                sessionDetailsEditors.Remove(sessionId);
                comp.Maintenance.SessionContentChanged -= chatInvalidate;
                chatVm.Shutdown();                           // warm-helper teardown on chat close (design 7.1)
                detailEditor.Dispose();
                _ = sessionsVm.RefreshRowAsync(sessionId);   // Stage 5.4 4.4: backstop if a save landed late / X was used
            };
```
- [x] **App.xaml.cs Edit 4 — citation navigation.** Implemented verbatim (OVERRIDE D); anchor re-verified
  byte-for-byte at its real location. The search click-through block currently ends:
```csharp
        searchVm.OpenSnippetRequested += (sessionId, seq, term) =>
        {
            openReadView(sessionId);
            if (seq >= 0 && readViews.TryGetValue(sessionId, out var window))
                window.ShowFindAt(seq, term);
        };
```
Immediately AFTER that block insert:
```csharp

        // Citation click-through (design 2026-07-18 section 7.5): the exact same open+target
        // path as the search-page snippet click above; seq < 0 (matter-scope chips, or a row
        // without payloads) just opens the read view. Assigned here because openReadView /
        // readViews only exist from this point (hoisting rule at the top of this method).
        navigateToCitation = (sessionId, seq, term) =>
        {
            openReadView(sessionId);
            if (seq >= 0 && readViews.TryGetValue(sessionId, out var window))
                window.ShowFindAt(seq, term);
        };
```
- [x] **[Brief OVERRIDE E addition, not in the original plan] Add a discriminating XamlHygiene test.**
  Added `AssistantChatPanel_labels_both_the_streaming_and_the_turn_AI_text` to
  `tests\LocalScribe.App.Tests\XamlHygieneTests.cs`, asserting the panel XAML contains BOTH
  `{x:Static vm:AssistantChatViewModel.AiDraftLabel}` (streaming surface) and `{Binding AiLabel}` (turn-history
  surface) — fails if either label is removed.
- [x] **Build + suites (0-warning gate).** DEVIATION (brief's gate, narrower + stricter than the plan's isobin
  commands): ran the plan's isobin build+test first for fast iteration (0 Warning(s)/0 Error(s); App suite
  609/616 passed, the other 7 were the EXPECTED isobin-path XamlHygieneTests false-fails per the brief -
  `RepoPaths.SolutionRoot()` walks up to `.git`, which does not exist under the Temp isobin path), THEN ran
  the brief's REQUIRED default-path gate: `dotnet build LocalScribe.slnx` (0 Warning(s)/0 Error(s)) and
  `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo` from the default
  repo-internal output path — ACTUAL: 616/616 passed, including all 7 XamlHygieneTests (incl. the new one)
  green. Run:
```
dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\
```
Expected: build 0 warnings (the new XAML uses theme resources only — `XamlHygieneTests` stays green); App suite fully green (the `MetadataEditorViewModel` change is an additive property — every pinned ctor call keeps compiling).
- [x] **Commit.** Also added `src/LocalScribe.App/CompositionRoot.cs` (OVERRIDE A), the new
  `src/LocalScribe.App/SyncLeaseAsAsync.cs` adapter, and `tests/LocalScribe.App.Tests/XamlHygieneTests.cs`
  (OVERRIDE E test) to the plan's git-add list, since the brief's contract adaptations touch those files too.
  Commit `2fec17c620755c9dfdb523c06c62761140091ce0`.
```
git add src/LocalScribe.App/Controls/AssistantChatPanel.xaml src/LocalScribe.App/Controls/AssistantChatPanel.xaml.cs src/LocalScribe.App/ViewModels/MetadataEditorViewModel.cs src/LocalScribe.App/SessionDetailsWindow.xaml src/LocalScribe.App/App.xaml.cs
git commit -m "feat(app): AssistantChatPanel + Session Details chat pane + citation click-through wiring

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 10: App — `MatterAssistantViewModel` (matter chat + per-session summary status + coverage disclosure)

**Files:**
- Create `src\LocalScribe.App\ViewModels\MatterAssistantViewModel.cs`
- Create test `tests\LocalScribe.App.Tests\MatterAssistantViewModelTests.cs`

**Interfaces:**
- Produces (namespace `LocalScribe.App.ViewModels`):
  - `public sealed record MatterSummaryStatusRow(string SessionId, string Title, string DateDisplay, string StatusText, bool HasSummary, bool IsStale)` — status text is exactly `"Summary ready"` / `"Summary out of date"` / `"No summary yet"` (§7.6 generated/stale/missing).
  - `public sealed partial class MatterAssistantViewModel : ObservableObject` — ctor `(string matterId, Func<CancellationToken, Task<IReadOnlyList<MatterSummarySource>>> loadSummarySources, Func<AssistantQaService?> chatServiceFactory, AssistantChatStore store, IUiErrorReporter reporter, Action<Action> dispatch, Func<string?>? busyReason = null)`. Members: `MatterId`, `SummaryRows` (newest-first), `[ObservableProperty] CoverageText` (`""` until the first answer; then the EXPLICIT included/omitted/no-summary disclosure built from the turn's lists — §7.5's no-silent-truncation rule made visible), `Chat` (an owned `AssistantChatViewModel`), `GenerateSummaryCommand` (`IRelayCommand<MatterSummaryStatusRow>` → `event Action<string>? SummaryGenerationRequested`), `RefreshAsync(ct)`, `Shutdown()` (delegates to `Chat` — matter switch = scope change teardown).
- Consumes: Tasks 5–8 types. The summary loading itself is a seam (`loadSummarySources`) — the App composition (Task 11) binds it to the foundation `SummaryStore`; tests pass canned lists.

Steps:
- [x] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\MatterAssistantViewModelTests.cs`:
```csharp
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Tests;

public class MatterAssistantViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) { }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<MatterSummarySource> Sources() =>
    [
        new("a", "Session a", T0.AddDays(-1),
            "The parties agreed to settle for ten thousand dollars [00:01:05]", false),
        new("b", "Session b", T0.AddDays(-2), "Retainer summary [00:10:00]", true),
        new("c", "Session c", T0.AddDays(-3), null, false),
    ];

    private (MatterAssistantViewModel Vm, FakeAssistantChatSessionFactory Factory, FakeReporter Reporter)
        Make()
    {
        var factory = new FakeAssistantChatSessionFactory();
        var store = new AssistantChatStore(Path.Combine(_root, "assistant", "chats.json"));
        var scope = new QaScope(
            new AssistantRequest(Op: "answer", ModelPath: @"C:\m.gguf", CtxTokens: 8192,
                Backend: "auto", KeepAlive: true, PayloadJson: "M1"),
            "m.gguf", "3", false, null, false, null, null,
            [Sources()[0]], ["a"], ["b"], ["c"]);
        var reporter = new FakeReporter();
        var vm = new MatterAssistantViewModel("m1",
            ct => Task.FromResult(Sources()),
            () => new AssistantQaService(factory, store,
                ct => Task.FromResult<IAsyncDisposable>(new NoopLease()),
                (q, ct) => Task.FromResult(scope), TimeProvider.System),
            store, reporter, a => a());
        return (vm, factory, reporter);
    }

    [Fact]
    public async Task Refresh_maps_summary_status_rows_newest_first()
    {
        var (vm, _, reporter) = Make();
        await vm.RefreshAsync(CancellationToken.None);

        Assert.Empty(reporter.Errors);
        Assert.Equal(new[] { "a", "b", "c" }, vm.SummaryRows.Select(r => r.SessionId));
        Assert.Equal("Summary ready", vm.SummaryRows[0].StatusText);
        Assert.Equal("Summary out of date", vm.SummaryRows[1].StatusText);   // stale badged
        Assert.True(vm.SummaryRows[1].IsStale);
        Assert.Equal("No summary yet", vm.SummaryRows[2].StatusText);        // missing -> generate offer
        Assert.False(vm.SummaryRows[2].HasSummary);
        Assert.Equal("2026-06-30", vm.SummaryRows[0].DateDisplay);
    }

    [Fact]
    public async Task Generate_cta_raises_the_generation_request()
    {
        var (vm, _, _) = Make();
        await vm.RefreshAsync(CancellationToken.None);
        string? requested = null;
        vm.SummaryGenerationRequested += id => requested = id;

        vm.GenerateSummaryCommand.Execute(vm.SummaryRows[2]);
        Assert.Equal("c", requested);
    }

    [Fact]
    public async Task Coverage_text_discloses_included_omitted_and_missing_after_an_ask()
    {
        var (vm, factory, reporter) = Make();
        await vm.RefreshAsync(CancellationToken.None);
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("The parties agreed to settle for ten thousand dollars [00:01:05]"),
            new AssistantDone("cpu", 1, 1),
        });

        vm.Chat.QuestionText = "what was agreed";
        await vm.Chat.AskCommand.ExecuteAsync(null);

        Assert.Empty(reporter.Errors);
        Assert.Contains("summaries from 1 of 3 tagged sessions", vm.CoverageText);
        Assert.Contains("Omitted (context budget): Session b.", vm.CoverageText);
        Assert.Contains("No summary yet: Session c.", vm.CoverageText);      // never silent (design 7.5)
    }

    [Fact]
    public async Task Shutdown_tears_the_warm_helper_down()
    {
        var (vm, factory, _) = Make();
        factory.ScriptPerSession.Enqueue(new AssistantEvent[]
        {
            new AssistantChunk("ok [00:01:05]"), new AssistantDone("cpu", 1, 1),
        });
        vm.Chat.QuestionText = "q";
        await vm.Chat.AskCommand.ExecuteAsync(null);            // a warm session now exists

        vm.Shutdown();
        Assert.True(factory.Sessions[0].Disposed);              // scope-change/close teardown (design 7.1)
    }
}
```
- [x] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~MatterAssistantViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\` — expected: `error CS0246: The type or namespace name 'MatterAssistantViewModel' could not be found`.
- [x] **Implement.** Create `src\LocalScribe.App\ViewModels\MatterAssistantViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Assistant;
namespace LocalScribe.App.ViewModels;

/// <summary>One tagged session's summary status on the Matters Assistant tab (design
/// 2026-07-18 section 7.6): generated / stale / missing-with-generate-offer.</summary>
public sealed record MatterSummaryStatusRow(string SessionId, string Title, string DateDisplay,
    string StatusText, bool HasSummary, bool IsStale);

/// <summary>The Matters Assistant tab state (design 2026-07-18 sections 7.5-7.6): matter chat
/// over per-session SUMMARIES (never transcripts, hard-scoped to one matter by construction)
/// plus the per-session summary status list and the EXPLICIT coverage disclosure after each
/// answer - included/omitted/no-summary are always listed, never silently truncated. One
/// instance per selected matter; Shutdown on matter switch tears the warm helper down.</summary>
public sealed partial class MatterAssistantViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<MatterSummarySource>>> _loadSummarySources;
    private readonly IUiErrorReporter _reporter;
    private readonly Action<Action> _dispatch;

    public string MatterId { get; }
    public ObservableCollection<MatterSummaryStatusRow> SummaryRows { get; } = [];
    /// <summary>"" until the first answer; then "Last answer used summaries from N of M tagged
    /// sessions." plus the omitted and no-summary-yet lists BY TITLE (design 7.5).</summary>
    [ObservableProperty] private string _coverageText = "";
    public AssistantChatViewModel Chat { get; }
    public IRelayCommand<MatterSummaryStatusRow> GenerateSummaryCommand { get; }
    /// <summary>Raised with the session id whose summary should be (re)generated. The App
    /// composition routes it to the foundation's summary-generation surface.</summary>
    public event Action<string>? SummaryGenerationRequested;

    public MatterAssistantViewModel(string matterId,
        Func<CancellationToken, Task<IReadOnlyList<MatterSummarySource>>> loadSummarySources,
        Func<AssistantQaService?> chatServiceFactory, AssistantChatStore store,
        IUiErrorReporter reporter, Action<Action> dispatch, Func<string?>? busyReason = null)
    {
        MatterId = matterId;
        (_loadSummarySources, _reporter, _dispatch) = (loadSummarySources, reporter, dispatch);
        Chat = new AssistantChatViewModel(chatServiceFactory, store, reporter, dispatch, busyReason);
        Chat.TurnCompleted += UpdateCoverage;
        GenerateSummaryCommand = new RelayCommand<MatterSummaryStatusRow>(row =>
        {
            if (row is not null) SummaryGenerationRequested?.Invoke(row.SessionId);
        });
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var sources = await _loadSummarySources(ct);
            var rows = sources
                .OrderByDescending(s => s.StartedAtLocal)
                .ThenByDescending(s => s.SessionId, StringComparer.Ordinal)
                .Select(RowFor).ToList();
            _dispatch(() =>
            {
                SummaryRows.Clear();
                foreach (var r in rows) SummaryRows.Add(r);
            });
        }
        catch (Exception ex) { _reporter.Report("Load matter summary status", ex); }
    }

    private static MatterSummaryStatusRow RowFor(MatterSummarySource s) => new(
        s.SessionId, s.Title,
        s.StartedAtLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        string.IsNullOrWhiteSpace(s.SummaryMarkdown) ? "No summary yet"
            : s.Stale ? "Summary out of date" : "Summary ready",
        !string.IsNullOrWhiteSpace(s.SummaryMarkdown), s.Stale);

    private void UpdateCoverage(AssistantChatTurn turn)
    {
        string Names(IReadOnlyList<string> ids) => string.Join(", ",
            ids.Select(id => SummaryRows.FirstOrDefault(r => r.SessionId == id)?.Title ?? id));
        var parts = new List<string>
        {
            "Last answer used summaries from " + turn.IncludedSessionIds.Count + " of "
                + SummaryRows.Count + " tagged sessions.",
        };
        if (turn.OmittedSessionIds.Count > 0)
            parts.Add("Omitted (context budget): " + Names(turn.OmittedSessionIds) + ".");
        if (turn.MissingSummarySessionIds.Count > 0)
            parts.Add("No summary yet: " + Names(turn.MissingSummarySessionIds) + ".");
        _dispatch(() => CoverageText = string.Join(" ", parts));
    }

    /// <summary>Matter switch / page teardown: the scope change tears the warm helper down.</summary>
    public void Shutdown() => Chat.Shutdown();
}
```
- [x] **Run tests and see PASS.** Same filter — expected: 4 passed.
- [x] **Commit.**
```
git add src/LocalScribe.App/ViewModels/MatterAssistantViewModel.cs tests/LocalScribe.App.Tests/MatterAssistantViewModelTests.cs
git commit -m "feat(app): MatterAssistantViewModel - matter chat + summary status rows + explicit coverage disclosure

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

**Deviation note:** `QaScope` grew two trailing positional fields (`SpeakerPreamble`, `ContextText`)
between when this plan snippet was written and the merged Task 7 implementation (the same drift
`AssistantChatViewModelTests.cs` already documents for its own `SessionScope` helper) — the test's
`new QaScope(...)` call appends `"", ""` for those two fields; behavior-preserving since no
assertion here touches the payload text sent to the fake session. The embedded test snippet was
also missing `using System.IO;` / `using Xunit;` (needed against this project's actual global
usings / xunit package reference) — added per repo convention (see `AssistantChatViewModelTests.cs`).
No production-code deviation: `MatterAssistantViewModel.cs` was implemented verbatim from the plan
and all 4 tests passed on the first run against the existing Task 5-8 types.

---

### Task 11: App — Matters Assistant tab (XAML + VM hook + composition) + final gate + smoke

**Files:**
- Modify `src\LocalScribe.App\ViewModels\MattersPageViewModel.cs` (`Assistant` property, `AssistantFactory` seam, `RebuildAssistant`, one call in `SelectAsync`)
- Modify `src\LocalScribe.App\Pages\MattersPage.xaml` (the Assistant `TabItem`)
- Modify `src\LocalScribe.App\App.xaml.cs` (matter-scope composition)
- Test: add one `[Fact]` to `tests\LocalScribe.App.Tests\MattersPageViewModelTests.cs`

**Interfaces:**
- Produces on `MattersPageViewModel`: `[ObservableProperty] MatterAssistantViewModel? Assistant` (null = assistant not composed → the tab shows an explainer), `public Func<string, MatterAssistantViewModel>? AssistantFactory { get; set; }` (property-injected post-construction, the `ExternalEngineBusy` settable-seam precedent — the pinned 7-arg ctor is untouched, every existing test keeps compiling), `public void RebuildAssistant(string? matterId)` (shuts the old scope down, builds the new one, kicks its refresh + history load; public so tests drive it directly).
- Consumes: Task 10 VM; `SelectAsync`'s dispatch block (anchor quoted); the foundation `SummaryStore` (`// CONTRACT:` in the composition); `openSessionDetails` (the guaranteed generation route), `navigateToCitation` (Task 9).

Steps:
- [x] **Write the failing test.** Append inside `MattersPageViewModelTests` (before the closing brace) in `tests\LocalScribe.App.Tests\MattersPageViewModelTests.cs` — it reuses the file's existing `MakeVm` helper and `_reporter` field:
```csharp
    [Fact]
    public void RebuildAssistant_builds_per_matter_and_shuts_down_the_previous_scope()
    {
        // Design 2026-07-18 section 7.6/7.1: one Assistant state per selected matter; switching
        // matters is a SCOPE CHANGE - the old warm helper is torn down. Driven directly (the
        // SelectAsync insert is a one-line call to this method).
        var vm = MakeVm();
        var built = new List<string>();
        vm.AssistantFactory = id =>
        {
            built.Add(id);
            var store = new LocalScribe.Core.Assistant.AssistantChatStore(
                Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "chats.json"));
            return new MatterAssistantViewModel(id,
                ct => Task.FromResult<IReadOnlyList<LocalScribe.Core.Assistant.MatterSummarySource>>([]),
                () => null, store, _reporter, a => a());
        };

        vm.RebuildAssistant("m1");
        Assert.Equal("m1", vm.Assistant!.MatterId);
        vm.RebuildAssistant("m2");
        Assert.Equal(new[] { "m1", "m2" }, built);
        Assert.Equal("m2", vm.Assistant!.MatterId);
        vm.RebuildAssistant(null);
        Assert.Null(vm.Assistant);                               // deselection clears the tab
    }
```
- [x] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~RebuildAssistant" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\` — expected: `error CS1061: 'MattersPageViewModel' does not contain a definition for 'AssistantFactory'` (plus `RebuildAssistant`/`Assistant`).
- [x] **Add the VM hook.** In `src\LocalScribe.App\ViewModels\MattersPageViewModel.cs`:
  1. The observable-property block currently contains (@ 7605606):
```csharp
    [ObservableProperty] private string? _selectedMatterId;
    [ObservableProperty] private bool _hasSelection;
```
Immediately after `_hasSelection` insert:
```csharp
    /// <summary>The selected matter's Assistant-tab state (design 2026-07-18 section 7.6);
    /// null when no matter is selected or the assistant stack is not composed (the tab then
    /// shows an explainer). Rebuilt per selection via RebuildAssistant.</summary>
    [ObservableProperty] private MatterAssistantViewModel? _assistant;
```
  2. Immediately after the ctor's closing brace (the line after `Vocabulary = new VocabularyEditorViewModel(SaveMatterVocabularyAsync, _reporter);` and its `}`) insert:
```csharp

    /// <summary>Composition seam (settable-property precedent: SessionController
    /// .ExternalEngineBusy): App.xaml.cs assigns the per-matter Assistant factory after
    /// construction; null in tests that do not exercise the tab.</summary>
    public Func<string, MatterAssistantViewModel>? AssistantFactory { get; set; }

    /// <summary>Swaps the Assistant-tab state for the newly selected matter. The previous
    /// matter's warm helper is torn down (scope change, design 7.1); the new VM loads its
    /// summary-status rows and chat history in the background. Null = deselection.</summary>
    public void RebuildAssistant(string? matterId)
    {
        Assistant?.Shutdown();
        Assistant = matterId is null ? null : AssistantFactory?.Invoke(matterId);
        if (Assistant is { } assistant)
        {
            _ = assistant.RefreshAsync(CancellationToken.None);
            _ = assistant.Chat.LoadHistoryAsync(CancellationToken.None);
        }
    }
```
  3. In `SelectAsync`, the dispatch block currently ends (@ 7605606):
```csharp
                HeaderCreatedDisplay = "created "
                    + loaded.DateCreatedUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                HasSelection = true;
            });
```
Insert one line before `HasSelection = true;`:
```csharp
                RebuildAssistant(matterId);   // Matter-QA round: fresh Assistant tab per matter
```
- [x] **Run the test and see PASS.** Same filter — expected: 1 passed. Then the whole class: `--filter "FullyQualifiedName~MattersPageViewModelTests"` (the ctor is unchanged — all pinned call sites keep compiling).
- [x] **Add the Assistant tab.** In `src\LocalScribe.App\Pages\MattersPage.xaml` the tab strip currently ends (@ 7605606):
```xml
                    </StackPanel>
                </TabItem>
            </TabControl>
```
(the `</TabItem>` closing the "Advanced" tab — locate by the `Delete matter` button just above). Replace with:
```xml
                    </StackPanel>
                </TabItem>
                <TabItem Header="Assistant">
                    <!-- Matter-QA round (design 2026-07-18 sections 7.5-7.6): matter chat over
                         per-session SUMMARIES (never transcripts, one matter by construction) +
                         per-session summary status + the explicit coverage disclosure. -->
                    <Grid Margin="4,8,4,4">
                        <TextBlock Text="The assistant is not set up." Opacity="0.7"
                                   HorizontalAlignment="Center" VerticalAlignment="Center">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock">
                                    <Setter Property="Visibility" Value="Collapsed" />
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Assistant}" Value="{x:Null}">
                                            <Setter Property="Visibility" Value="Visible" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>
                        <DockPanel DataContext="{Binding Assistant}">
                            <DockPanel.Style>
                                <Style TargetType="DockPanel">
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding}" Value="{x:Null}">
                                            <Setter Property="Visibility" Value="Collapsed" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </DockPanel.Style>
                            <!-- Per-session summary status: generated / stale / missing -> generate. -->
                            <Border DockPanel.Dock="Top" MaxHeight="180" Margin="0,0,0,8">
                                <ScrollViewer VerticalScrollBarVisibility="Auto">
                                    <ItemsControl ItemsSource="{Binding SummaryRows}">
                                        <ItemsControl.ItemTemplate>
                                            <DataTemplate>
                                                <DockPanel Margin="0,2">
                                                    <ui:Button DockPanel.Dock="Right" Content="Generate"
                                                               FontSize="11" Padding="8,2" Margin="8,0,0,0"
                                                               Command="{Binding DataContext.GenerateSummaryCommand,
                                                                   RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                                               CommandParameter="{Binding}" />
                                                    <TextBlock VerticalAlignment="Center" TextWrapping="Wrap">
                                                        <Run Text="{Binding Title, Mode=OneWay}" FontWeight="SemiBold" />
                                                        <Run Text="{Binding DateDisplay, Mode=OneWay}" />
                                                        <Run Text="{Binding StatusText, Mode=OneWay}" />
                                                    </TextBlock>
                                                </DockPanel>
                                            </DataTemplate>
                                        </ItemsControl.ItemTemplate>
                                    </ItemsControl>
                                </ScrollViewer>
                            </Border>
                            <!-- Coverage disclosure (design 7.5: included/omitted listed, never silent). -->
                            <TextBlock DockPanel.Dock="Top" Text="{Binding CoverageText}" TextWrapping="Wrap"
                                       FontSize="12" Margin="0,0,0,8">
                                <TextBlock.Style>
                                    <Style TargetType="TextBlock">
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding CoverageText}" Value="">
                                                <Setter Property="Visibility" Value="Collapsed" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </TextBlock.Style>
                            </TextBlock>
                            <controls:AssistantChatPanel DataContext="{Binding Chat}" />
                        </DockPanel>
                    </Grid>
                </TabItem>
            </TabControl>
```
(`xmlns:controls` and `xmlns:ui` already exist on this page's root — verified @ 7605606.)
- [x] **Compose the matter scope.** In `src\LocalScribe.App\App.xaml.cs` the matters wiring currently reads (@ 7605606):
```csharp
        mattersVm.OpenReadViewRequested += openReadView;
```
Immediately AFTER that line insert:
```csharp

        // Matter-QA round (design 2026-07-18 sections 7.5-7.6): the Matters Assistant tab.
        // Summary sources reload PER QUESTION and per refresh, so regenerated summaries are
        // picked up without reopening; the chat store lives in the matter folder.
        mattersVm.AssistantFactory = matterId =>
        {
            Func<CancellationToken, Task<IReadOnlyList<LocalScribe.Core.Assistant.MatterSummarySource>>>
                loadSources = async ct =>
            {
                var catalog = await comp.Maintenance.ListSessionsAsync(ct);
                var sources = new List<LocalScribe.Core.Assistant.MatterSummarySource>();
                foreach (var s in catalog.Sessions.Where(s => s.Meta.MatterIds.Contains(matterId)))
                {
                    // CONTRACT (foundation): read the LATEST summary version for the session via
                    // the foundation SummaryStore (assumed: ctor over (comp.Paths, sessionId),
                    // LoadAsync(ct) -> the version list, newest last; SummaryVersion carries
                    // ContentMarkdown + Stale). Locate the store's real construction in the
                    // Session Details summary wiring on the merged master and mirror it; the
                    // fixed behavior: null markdown when no version exists.
                    var versions = await new LocalScribe.Core.Assistant.SummaryStore(comp.Paths, s.Id)
                        .LoadAsync(ct);
                    var latest = versions.Count > 0 ? versions[^1] : null;
                    sources.Add(new LocalScribe.Core.Assistant.MatterSummarySource(
                        s.Id, s.Meta.Title, s.Session.StartedAtUtc.ToLocalTime(),
                        latest?.ContentMarkdown, latest?.Stale ?? false));
                }
                return sources;
            };
            var matterChatStore = new LocalScribe.Core.Assistant.AssistantChatStore(
                comp.Paths.MatterChatsJson(matterId));
            Func<LocalScribe.Core.Assistant.AssistantQaService?> serviceFactory = () =>
                qaScopeFactoryFor() is { } scopes
                    ? new LocalScribe.Core.Assistant.AssistantQaService(assistantChatFactory,
                        matterChatStore, acquireAssistantLease,
                        async (question, ct) => await scopes.ForMatterAsync(await loadSources(ct), ct),
                        TimeProvider.System)
                    : null;
            var vm = new ViewModels.MatterAssistantViewModel(matterId, loadSources, serviceFactory,
                matterChatStore, errors, dispatch, assistantBusyReason);
            vm.Chat.CitationNavigationRequested += (sid, seq, term)
                => navigateToCitation?.Invoke(sid, seq, term);
            // Generation route: opening Session Details lands on its Assistant tab's Generate
            // CTA (the foundation surface) - the guaranteed path. CONTRACT: if the merged master
            // exposes a direct summary-job entry point in this file, bind to it instead.
            vm.SummaryGenerationRequested += openSessionDetails;
            return vm;
        };
```
- [x] **Final gate: 0-warning build + BOTH full suites.** Run:
```
dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\matter-qa\
```
Expected: build 0 warnings; App suite fully green (incl. `XamlHygieneTests` — the new markup uses theme resources only); Core suite green except the 2 known pre-existing fixture failures.
- [ ] **Manual smoke (WPF + real model — the ONLY place a real model runs, per the locked rule).**
  1. **No model:** with no assistant model installed, the Session Details Assistant tab's chat and the Matters Assistant tab show the explainer ("No assistant model is installed..." / "The assistant is not set up."); everything else in the app is unaffected.
  2. **Session ask + citations:** install the default model (foundation flow), open a finalized real session → Session Details → Assistant → ask a question answerable from the record. The answer streams; each claim carries a `[HH:MM:SS]` chip; clicking a verified chip opens/activates the Read view scrolled to the cited segment with the find bar highlighting the term. Every turn shows the AI-draft label and the model · backend · prompt provenance line.
  3. **Unverifiable flagged:** ask something NOT in the record ("what is the capital of France") — the strict-extractive prompt should refuse or any fabricated claim renders with the red "uncited" chip / unverified chips. Nothing is silently dropped.
  4. **Queued while recording:** start a recording, ask in an open session chat → "Waiting for the recording to finish..." shows; stop recording → the answer proceeds. Never blocks Stop/Start.
  5. **Excerpt disclosure:** on a very long session (2 h class), ask → the answer carries "Answered from matching excerpts, not the full transcript."
  6. **Context staleness:** with a session chat open, save a correction in the Read view → ask again → the answer reflects the corrected text (the warm helper re-prefilled; the first ask after the change takes the prefill hit again).
  7. **Matter tab:** tag 2+ sessions to a matter, generate a summary for one → the status list shows "Summary ready" / "No summary yet"; Generate opens Session Details on the missing one; ask a matter question → the coverage line lists included/omitted/no-summary by title; a citation chip click opens the right session's Read view.
  8. **History + files:** close and reopen the windows — chat history re-renders with its chips; `sessions\<id>\assistant\chats.json` and `matters\<id>\assistant\chats.json` exist; a matter/session zip export includes the `assistant\` folder (foundation §7.3 behavior — verify presence only).
  9. **CPU floor:** on CPU-only (or forced CPU), the first ask shows the busy status through a minutes-long prefill without freezing the UI; a follow-up on the same scope answers in seconds (warm reuse); the provenance line records the CPU backend.
- [x] **Commit.**
```
git add src/LocalScribe.App/ViewModels/MattersPageViewModel.cs src/LocalScribe.App/Pages/MattersPage.xaml src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/MattersPageViewModelTests.cs
git commit -m "feat(app): Matters Assistant tab - matter chat, summary status, coverage disclosure, composition

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

**Deviation note (contract-adapted, per the branch-7 Task 11 brief):** the plan's embedded
`// CONTRACT:` code for the summary read and the chat factory did not match the merged
foundation shape and were corrected per the brief (binding, brief wins over plan text):
(1) the summary read uses the single composed `comp.Summaries.LoadAsync(s.Id, ct)`
(`SummaryStore` is `SummaryStore(StoragePaths paths)`, ONE instance already exposed on
`AppComposition` and shared with the Session Details Assistant tab / the finalize-time
`MarkAllStaleAsync` call) instead of constructing a fresh `new SummaryStore(comp.Paths, s.Id)`
per session; (2) the chat factory passed to `AssistantQaService` is `comp.AssistantChat`
(the `IAssistantChatSessionFactory` Task 9 already wired for the session-scope chat), not a
bare `assistantChatFactory` local (no such local exists in the merged file). Both corrections
are identifier-only - no behavior differs from the plan's intent. A required addition beyond
the plan's verbatim text: the matter chat needed its own recording-preemption wiring, mirroring
the session chat's `StateChanged -> CancelForRecording` fix (commit 6591731) - added as ONE
app-lifetime `comp.Controller.StateChanged` subscription (no per-matter subscribe/unsubscribe
needed) that reads the live, swappable `mattersVm.Assistant` at fire time, since
`MattersPageViewModel` owns exactly one Assistant instance for the app's lifetime. No other
production-code deviation: the VM hook (Part A) and the XAML tab (Part B) were implemented
verbatim from the plan and compiled/passed against the existing Task 1-10 types unchanged.

---

## Self-review

**(a) Spec coverage — every §7.5 / chat-§7.6 / chat-§7.7 clause maps to tasks:**
- §7.5 strict-extractive + per-claim `[HH:MM:SS]` → the prompt wording is foundation-owned (`BuildAnswerPrompt`, consumed in Task 7's `QaScopeFactory`); the anchors the model can cite are injected per line by Tasks 3–4 (`AssistantCitationFormat.Format` — Task 1), and validation is mandatory on every answer path (Task 7 service always validates before persisting).
- §7.5 citation post-validation (±2 s to a real segment + fuzzy match via `TextDistance`, unverifiable FLAGGED never dropped) → Task 2 (`ToleranceMs = 2000`, `MatchThreshold = 0.60` with the containment/whole-string split rationale, reasons per failure mode, `AnswerLine.Text` always intact) + Task 5's matter analog. Click-through to the Read view scrolled to the segment → chips carry `(SessionId, Seq, NavTerm)` → Task 8 `NavigateChipCommand` → Task 9's `navigateToCitation` = the SAME `openReadView` + `ShowFindAt` path the search page uses (the "reuse the Matters open-transcript navigation" family — the search click-through is the richer variant of it, and Matters' own `OpenReadViewRequested` stays untouched).
- §7.5 session scope (full projected transcript, active version + corrections — `SessionProjectionLoader.LoadAsync` follows the active version by construction; raise `num_ctx` within the RAM policy, then search-assisted excerpting with a disclosure header) → Task 3 ladder (8k→64k, 80% gate, §7.2 numbers restated) + Task 4 excerpts (`SearchIndexService.Query` per term, ±2 neighbors, `[...]` gaps, `DisclosureText`, honest `NoMatches`) + Task 7 `ForSessionAsync` sequencing them.
- §7.5 matter scope (summaries never transcripts, newest-first within budget, UI lists included/omitted, no-summary sessions offered for generation, hard-scoped to one matter) → Task 5 builder (strict newest-first prefix cut, stale note, missing list) + Task 10 (`CoverageText` by title, status rows, Generate CTA) + Task 11 composition (sources filtered to `Meta.MatterIds.Contains(matterId)` — one matter by construction).
- §7.5 multi-turn UI / single-turn to the model (recorded v1) + §7.1 warm helper (keepAlive, re-prefill skipped, teardown on close/scope change/context change) → Task 7 service (payload-identity reuse, per-question `AnswerQuestionPayload`, `ResetSessionAsync` on error, `DisposeAsync`) + Task 8 `InvalidateContext`/`Shutdown` + Task 9 `SessionContentChanged` unsubscribe-on-close wiring + Task 11 `RebuildAssistant` on matter switch. The 5-min idle teardown is explicitly assigned to the foundation session (Global Constraints note).
- §7.3 chat history per scope (`assistant\chats.json` in session/matter folders, AtomicFile, derived work product separate from transcripts) → Task 6 (paths + append-only store + schema stamp; nothing under this plan touches a transcript file).
- Chat portions of §7.6 (Session Details Assistant tab gains session chat; Matters gains an Assistant tab; disabled-with-explainer until a model exists) → Tasks 9/11 XAML + `UnavailableText`/null-`Assistant` explainers; the summary portion of the Session Details tab remains foundation-owned (this plan only appends below it, with a full-tab fallback if the tab is absent).
- §7.7 as it applies to chat (helper crash → visible error nothing persisted; mid-recording → visibly queued; model missing → off with explainer; CUDA→CPU fall → recorded and surfaced) → Task 7 (persist-only-on-`AssistantDone`, `Backend` from the done event into the turn) + Task 8 (reporter surface, `busyReason` status, explainer) + the provenance line rendering the recorded backend.
- Locked evidentiary rules re-checked: no code path rewrites/hides/drops answer or transcript content; markers are never cited targets but are also never modified; nothing writes markers; nothing gates or delays recording (the gate queues the ASSISTANT, never the recorder); the AI-draft label is a constant rendered on every turn (Task 9 XAML) and asserted in tests (Task 8).

**(b) Placeholder scan:** no TBD / "add error handling" / "similar to Task N" — every step carries full test code, full implementation code, exact run commands with expected failure/pass, and full commit messages with the trailer. The open foundation-signature points are not placeholders but `// CONTRACT:`-marked ASSUMED-shape call sites concentrated in exactly three places (`QaScopeFactory.cs`, the App.xaml.cs Edit-1 seam block, the Task 11 `SummaryStore` read), each with a written adaptation rule (identifiers only, behavior fixed) — necessary because this branch starts only after `feat/llm-foundation-summaries` merges and that branch's plan does not exist yet. Anchors into 7605606 code are quoted (`StoragePaths` matters block, App.Tests csproj tail, `MetadataEditorViewModel` ctor, `MattersPageViewModel` property block/ctor tail/`SelectAsync` tail, MattersPage tab tail, App.xaml.cs maps/factory/Closed/search-wiring/matters-wiring blocks) with the re-verify-by-quote rule stated in Global Constraints.

**(c) Type consistency across tasks:** `CitationStamp`/`AnswerLineParts` (T1) → `CitationValidator.Validate(string, IReadOnlyList<DisplayRow>, string) : ValidatedAnswer` and `MatterCitationValidator.Validate(string, IReadOnlyList<MatterSummarySource>) : ValidatedAnswer` (T2/T5) share `CitationChip(string, bool, string?, int, string)`/`AnswerLine` → persisted verbatim inside `AssistantChatTurn.Lines` (T6, camelCase JSON round-trip asserted) → rendered by `ChatTurnViewModel.Lines` (T8) → chip `CommandParameter` is a `CitationChip`, `NavigateChipCommand : IRelayCommand<CitationChip>` raises `(string, int, string)` — the exact triple `navigateToCitation`/`ShowFindAt(int seq, string term)` consumes (T9). `SessionQaContext.CtxTokens : int?` (T3) vs `ExcerptQaContext.CtxTokens : int` (T4) both flow into `AssistantRequest.CtxTokens` via `QaScopeFactory` (T7); `QaScope.SessionRows : IReadOnlyList<DisplayRow>?` XOR `MatterSummaries : IReadOnlyList<MatterSummarySource>?` selects the validator in `AskAsync`; the service's `Func<string, CancellationToken, Task<QaScope>>` matches both `ForSessionAsync` partial application (session id + loadRows closed over) and `ForMatterAsync` (question ignored — matter context is question-independent). `estimateTokens : Func<string,int>` and `stripLine : Func<string,string>?` are identical seams across T3/T4/T5 with the single production binding in T7. `AssistantChatStore` is shared by service (append) and VM (history) — both constructed from the same path in each composition block. The fakes implement the FOUNDATION interfaces, so `AssistantQaService` in App tests (T8/T10) is the REAL Core service over fakes — no parallel test-only service. All members tests touch are `public` (no InternalsVisibleTo); `MetadataEditorViewModel`/`MattersPageViewModel` ctors are unchanged (property injection), so every pinned construction site in the existing suites keeps compiling. New UI strings are ASCII except `—`/`·` escapes; no Unicode emojis anywhere in tests.




