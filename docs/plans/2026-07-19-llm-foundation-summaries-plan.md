# LLM Foundation + Per-Session Summaries Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement branch 6 of the Steno round (`docs/plans/2026-07-18-steno-round-design.md`): §7.1 runtime (out-of-process `LocalScribe.Assistant.exe` hosting LLamaSharp over stdio, Diarizer precedent), §7.2 models (SHA-pinned GGUF fetch + manifest + verify-on-load, default locked to Qwen3-4B-Instruct-2507 q4_K_M), §7.3 storage (append-only versioned `assistant\summaries.json` via AtomicFile + schema stamp, staleness on `SessionContentChanged`), §7.4 summarization (fits-check gate, single-call / map-reduce, named-speaker roster, grounding line, fixed section headers), the Session-Details **Assistant tab** portion of §7.6 (summary render, version switcher, stale badge, Regenerate CTA, streaming, queued/error states, AI-draft label), §7.7 failure posture, and the Settings **Assistant** section. Q&A/chat UI (§7.5, the chat surfaces, the Matters Assistant tab) is the NEXT branch (`feat/matter-qa`) — NOT built here — but every contract it consumes (wire protocol, chat-session factory, `BuildAnswerPrompt`, `TokenBudget`, model refs) IS produced here, locked.
**Architecture:** A new console helper project `src\LocalScribe.Assistant\` (Exe, references Core, owns the LLamaSharp CPU+CUDA native backends in ITS process only — the exact ORT-isolation rationale documented in `LocalScribe.App.csproj:24-52` for Diarizer) speaks a JSON-lines stdio protocol whose wire types + both-direction codecs live in Core (`LocalScribe.Core.Assistant.AssistantWire`, mirroring `DiarisationWire`/`DiarisationJson`). Unlike Diarizer's read-stdin-to-end one-shot, the helper runs a **line-oriented request loop** so `keepAlive:true` can hold the loaded model + KV prefix across `answer` requests (the §7.1 warm-chat contract — this deviation from the Diarizer shape is contract-forced and recorded below). The App-side spawner `ProcessAssistantHelper` mirrors `ProcessDiarisationHelper` (redirect stdin/stdout, no shell, no window, kill-entire-process-tree on cancel) behind Core seams `IAssistantProcess`/`IAssistantProcessFactory`; `AssistantJobRunner` (spawn-per-job) and `AssistantChatSessionFactory` (warm keep-alive) turn raw lines into typed `AssistantEvent`s with an inactivity watchdog. `SummarizationService` orchestrates: `AssistantGate` (blocked-while-recording, visibly queued) → `SessionProjectionLoader` triple → `AssistantInputShaper` (timestamps stripped, roster preamble) → `TokenBudget` fits-check → `AssistantPrompts` → runner → `SummaryStore` (AtomicFile + SchemaGuard, append-only). App wiring: `CompositionRoot` constructs the services; `App.xaml.cs` marks summaries stale on `SessionContentChanged`/finalize/retranscription (beside the existing search-index reindex hooks at `App.xaml.cs:180-181`); the Session Details `TabControl` gains a third `TabItem`; the Settings root `UserControl` gains an Assistant `ui:Card`.
**Tech Stack:** C#/.NET 10, WPF, CommunityToolkit.Mvvm, Wpf.Ui, System.Text.Json (JsonNode for the wire; `LocalScribeJson` for sidecars), LLamaSharp (+Backend.Cpu, +Backend.Cuda12 — helper project ONLY), xUnit, PowerShell (fetch script + test stub helper).

## Global Constraints

- **Target branch:** `feat/llm-foundation-summaries`, created off master AFTER `feat/ux-round-2026-07-18` merges. All line anchors below are grounded @ `7605606` (the ux-round HEAD that becomes master's content for the files touched here); **re-verify every anchor by its quoted context before editing** — if drifted, locate by the quoted code, not the number.
- **Merge order for the round:** 6th of 7 — after `fix/dedup-short-utterance-guard`, `feat/markdown-export`, `feat/deep-link`, `feat/call-detect-advisory`, `feat/console-compact-mode`; before `feat/matter-qa`.
- **LOCKED rules (design §1/§7, restated — violating any is a blocking defect):**
  - Strictly local-only. **No sockets anywhere; stdio only.** Nothing here opens a network connection at runtime (the fetch script is dev tooling, run manually).
  - **The helper never writes files.** All persistence is App/Core-side via `AtomicFile` (`src\LocalScribe.Core\Storage\AtomicFile.cs` — write-tmp-then-move-with-retry; reused, not reimplemented).
  - **Assistant jobs are blocked while a recording session is active** — queued with a visible "waiting" state (the `ExternalEngineBusy`-style surface; here `AssistantGate` probing the same `controller.State != SessionState.Idle || !controller.PendingFinalize.IsCompleted` condition `CompositionRoot.cs:92-95` gives `RetranscriptionRunner`). Recording is NEVER gated by the assistant (recording always wins; the reverse `ExternalEngineBusy` chain is deliberately NOT extended).
  - Every rendered/exported assistant artifact carries the label **"AI-generated draft — not a transcript; verify against the record."** (single constant `AssistantPrompts.DraftLabel`, em dash written as `—` so source stays ASCII).
  - **Transcript files are never touched.** This branch writes only `assistant\summaries.json` (new sidecar), `settings.json` (additive field), and new source files. No task modifies `transcript.jsonl`, `edits.json`, `speakers.json`, `session.json`, or any renderer of them.
  - **Real-model runs are SMOKE only.** Unit/integration tests use in-process fakes and a scripted stub helper process (Task 7). No test downloads or loads a GGUF.
  - **Model default LOCKED:** `Qwen3-4B-Instruct-2507` q4_K_M. Optional manifest entries: `Gemma 4 E2B QAT`, `Qwen3-1.7B-Instruct` q4_K_M. No other models; no bake-off.
- **CONTRACTS LOCKED for `feat/matter-qa`** (the next branch consumes these EXACTLY — do not rename, retype, or "improve" any of them): the wire protocol (Task 1), `AssistantModelInfo`/`AssistantModelManifest`/`AssistantModelRef` (Task 4), `AssistantRequest`/`AssistantEvent` family (Task 1), `IAssistantJobRunner`/`AssistantJobRunner`/`IAssistantChatSession`/`IAssistantChatSessionFactory` (Task 6), `SummaryVersion`/`SummaryStore` (Task 8), `AssistantPrompts` incl. `BuildAnswerPrompt` + `AssistantInputShaper.StripLeadingTimestamps`/`BuildSpeakerPreamble` (Tasks 2-3), `TokenBudget` (Task 2), `AssistantGate` (Task 9).
- **Repo facts (verified 2026-07-18, encode-don't-rediscover):**
  - The model-fetch script is `tools\fetch-models.ps1` (there is NO `scripts\` directory). Models land in the git-ignored repo-root `models\` folder; runtime discovery is `ModelPaths.ModelsRoot` (`src\LocalScribe.Core\Transcription\ModelPaths.cs:8-21`: `LOCALSCRIBE_MODELS` env override → walk up to `LocalScribe.slnx` → beside the binary). No model manifest exists today; the GGUF manifest (Task 5) is net-new.
  - No central package management (no `Directory.Packages.props`/`Directory.Build.props`) — pin package versions inline in each csproj.
  - No `InternalsVisibleTo` anywhere — every member a test calls must be `public`.
  - Sidecar JSON pattern: `JsonFile.ReadAsync/WriteAsync` + `LocalScribeJson.Options` (camelCase, indented, ISO-8601 UTC) + `SchemaGuard.ReadObjectAsync/ReadVersion/RejectIfNewer` + a store `const int Version` stamped on save (`SpeakersStore` is the smallest complete example).
  - The active transcript version identifier is the **string** `SessionRecord.ActiveVersion` (`"v1"` or a `TranscriptVersion.Id` like `"v2-base.en-2026-07-13"`); `SessionProjectionLoader.LoadAsync(paths, settings, time, sessionId, ct)` resolves it and returns it as `LoadedProjection.VersionId` — that value is what `SummaryVersion.SourceTranscriptVersion` stores.
  - `MaintenanceService.SessionContentChanged` is `public event Action<string>?` (sessionId), raised only on real writes (`MaintenanceService.cs:30-43`); the existing consumer pattern is `App.xaml.cs:180-181` (fire-and-forget into the search index). `SessionController.SessionFinalizeCompleted` and `RetranscriptionRunner.RetranscriptionCompleted` are wired the same way at `App.xaml.cs:258`.
  - `SessionArchiver.AddSessionFolderAsync` enumerates `SearchOption.AllDirectories` (`SessionArchiver.cs:19`) — the new `assistant\` folder rides into zips automatically; docx export consumes only the `Header/TextView/Rows` triple, which cannot carry assistant content (exclusion by construction). Task 14 pins the zip inclusion with a test.
  - The Diarizer helper precedent (mirrored throughout): csproj shape `src\LocalScribe.Diarizer\LocalScribe.Diarizer.csproj` (Exe, net10.0-windows, ProjectReference Core, isolated native package, NOT referenced by App, NO auto-copy); spawn shape `src\LocalScribe.App\Services\ProcessDiarisationHelper.cs` (redirect stdin/stdout, `UseShellExecute=false`, `CreateNoWindow=true`, `Kill(entireProcessTree: true)` on cancel); exe resolution `Path.Combine(AppContext.BaseDirectory, "...exe")` (`CompositionRoot.cs:124`); publish = CLI only: `dotnet publish src/LocalScribe.Assistant -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <scratch>` then copy ONLY the single `.exe` beside the App output (never the whole publish folder — native-DLL collision rationale, `LocalScribe.App.csproj:24-52`).
- **Recorded deviations (deliberate, with reasons):**
  1. The helper's stdin handling is a line-oriented loop (one JSON request per line), not Diarizer's `ReadToEndAsync` one-shot — forced by the locked `keepAlive:true` warm-chat contract (§7.1): further `answer` requests must arrive on the SAME stdin after the first `done`.
  2. GGUF sha256 pins are acquired at fetch time from the Hugging Face **LFS pointer file** (`.../raw/main/<file>` contains `oid sha256:<64hex>`, fetched over TLS BEFORE the multi-GB blob) rather than hard-coded in the script — real hashes cannot be known at plan-authoring time and inventing them is worse. The pin still gates the download fail-closed (`Assert-Sha256` deletes on mismatch) and is recorded into `models\assistant-manifest.json`, which Core re-verifies on load. The two optional model URLs must be confirmed against Hugging Face at execution time (the pointer fetch fails loudly if a path is wrong).
  3. LLamaSharp pinned at `0.25.0` (all three packages at the SAME version). If `dotnet restore` reports that version unavailable, run `dotnet package search LLamaSharp --take 1` and pin all three to the newest stable instead — the App never references them either way, so the pin is helper-local.
- **App/Core never reference LLamaSharp or the Assistant project.** `LocalScribe.App.csproj` and `LocalScribe.Core.csproj` gain NO new PackageReference and NO ProjectReference to `LocalScribe.Assistant` (Diarizer precedent, enforced in Task 11's checklist).
- 0-warning build gate must hold. Core suite has 2 known pre-existing fixture failures (unrelated); App suite must be fully green.
- Tests: xUnit. App = `tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj`; Core = `tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj`. Filtered run pattern: `dotnet test "<testproj>" --filter "FullyQualifiedName~<Name>" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\`
- IMPORTANT: the LocalScribe app may be running and LOCK bin DLLs (MSB3027 copy error — NOT a compile error). Always use the isolated BaseOutputPath above (every command below already appends it); NEVER kill the user's running app or any other process not started by the tests.
- Never use Unicode emojis in test code or scripts (project rule). Non-ASCII glyphs the design mandates (the label's em dash, the chip middle dot) are written as C# escapes (`—`, `·`).
- Commit style: `feat(core)`/`feat(app)`/`feat(assistant)`/`test(...)`/`build(...)`/`docs(...)`; every commit message ends with exactly this trailer line:
```
Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
```

---

### Task 1: Core — Assistant wire protocol (`AssistantRequest`, `AssistantEvent` family, both-direction codecs)
**Files:**
- Create `src\LocalScribe.Core\Assistant\AssistantWire.cs`
- Create `tests\LocalScribe.Core.Tests\AssistantWireTests.cs`

**Interfaces:**
- Produces (LOCKED contract — `feat/matter-qa` consumes these verbatim):
  - `public sealed record AssistantRequest(string Op, string ModelPath, int CtxTokens, string Backend, bool KeepAlive, string PayloadJson);`
  - `public abstract record AssistantEvent; public sealed record AssistantChunk(string Text) : AssistantEvent; public sealed record AssistantProgress(string Phase, int Current, int Total) : AssistantEvent; public sealed record AssistantDone(string Backend, int PromptTokens, int OutputTokens) : AssistantEvent; public sealed record AssistantError(string Message) : AssistantEvent;`
  - Wire shape (LOCKED): request = ONE stdin JSON line `{"op":"summarize"|"answer","modelPath":string,"ctxTokens":int,"kvQuant":"q8_0","backend":"auto"|"cuda"|"cpu","keepAlive":bool,"payload":{...}}`; stdout JSON-lines events `{"type":"chunk","text":string}` | `{"type":"progress","phase":string,"current":int,"total":int}` | `{"type":"done","stats":{"backend":string,"promptTokens":int,"outputTokens":int}}` | `{"type":"error","message":string}`. With `keepAlive:true` the helper stays resident after `done` and accepts further `{"op":"answer",...}` requests on stdin reusing the loaded model+KV prefix; `keepAlive:false` exits after done.
  - `public static class AssistantWire` with `const string KvQuant = "q8_0"`, `SerializeRequest(AssistantRequest) : string` (single line, `kvQuant` stamped constant, `payload` embedded as raw JSON), `ParseRequestLine(string) : AssistantRequest?` (helper side; null on malformed), `SerializeEvent(AssistantEvent) : string`, `ParseEventLine(string) : AssistantEvent?` (App side; null on malformed/unknown — the `SherpaHelperDiariser` swallow-noise precedent), `PromptPayload(string prompt, int maxTokens) : string` (the v1 payload shape both ops use: `{"prompt":...,"maxTokens":...}`).
- Consumes: `System.Text.Json.Nodes` only. `JsonNode.ToJsonString()` is non-indented by default — exactly the JSON-lines requirement; `LocalScribeJson.Options` (indented) is deliberately NOT used on the wire.

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\AssistantWireTests.cs`:
```csharp
using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

public class AssistantWireTests
{
    [Fact]
    public void Request_serializes_to_the_locked_single_line_wire_shape_and_round_trips()
    {
        // Design 2026-07-18 section 7.1: the wire is a LOCKED contract feat/matter-qa consumes.
        var req = new AssistantRequest("summarize", @"C:\models\q.gguf", 16384, "auto", false,
            "{\"prompt\":\"hi\",\"maxTokens\":600}");
        string line = AssistantWire.SerializeRequest(req);

        Assert.DoesNotContain('\n', line);                       // JSON-LINES: one request, one line
        Assert.Contains("\"op\":\"summarize\"", line);
        Assert.Contains("\"ctxTokens\":16384", line);
        Assert.Contains("\"kvQuant\":\"q8_0\"", line);           // constant on the wire (design 7.2)
        Assert.Contains("\"backend\":\"auto\"", line);
        Assert.Contains("\"keepAlive\":false", line);
        Assert.Contains("\"payload\":{\"prompt\":\"hi\",\"maxTokens\":600}", line);

        var back = AssistantWire.ParseRequestLine(line);
        Assert.NotNull(back);
        Assert.Equal(req.Op, back!.Op);
        Assert.Equal(req.ModelPath, back.ModelPath);
        Assert.Equal(req.CtxTokens, back.CtxTokens);
        Assert.Equal(req.Backend, back.Backend);
        Assert.Equal(req.KeepAlive, back.KeepAlive);
        Assert.Equal(req.PayloadJson, back.PayloadJson);
    }

    [Fact]
    public void Events_round_trip_through_both_codecs()
    {
        AssistantEvent[] events =
        [
            new AssistantChunk("Hello "),
            new AssistantProgress("map", 2, 5),
            new AssistantDone("cuda", 1234, 210),
            new AssistantError("JOB_FAILED: boom"),
        ];
        foreach (var evt in events)
        {
            string line = AssistantWire.SerializeEvent(evt);
            Assert.DoesNotContain('\n', line);
            Assert.Equal(evt, AssistantWire.ParseEventLine(line));   // record value equality
        }
        // The done line carries the LOCKED nested stats shape.
        Assert.Contains("\"stats\":{\"backend\":\"cuda\",\"promptTokens\":1234,\"outputTokens\":210}",
            AssistantWire.SerializeEvent(new AssistantDone("cuda", 1234, 210)));
    }

    [Fact]
    public void Malformed_or_unknown_lines_parse_to_null_never_throw()
    {
        // SherpaHelperDiariser precedent: non-protocol stdout noise is skipped, never fatal.
        Assert.Null(AssistantWire.ParseEventLine("not json at all"));
        Assert.Null(AssistantWire.ParseEventLine("42"));
        Assert.Null(AssistantWire.ParseEventLine("{\"type\":\"mystery\"}"));
        Assert.Null(AssistantWire.ParseEventLine("{}"));
        Assert.Null(AssistantWire.ParseRequestLine("not json at all"));
        Assert.Null(AssistantWire.ParseRequestLine("{\"modelPath\":\"x\"}"));   // no op -> reject
        Assert.Null(AssistantWire.ParseRequestLine("{\"op\":\"summarize\"}"));  // no modelPath -> reject
    }

    [Fact]
    public void PromptPayload_builds_the_v1_payload_shape()
    {
        Assert.Equal("{\"prompt\":\"do it\",\"maxTokens\":600}", AssistantWire.PromptPayload("do it", 600));
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~AssistantWireTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` — expected: `error CS0246: The type or namespace name 'LocalScribe.Core.Assistant' could not be found` (the namespace does not exist yet).
- [ ] **Write the implementation.** Create `src\LocalScribe.Core\Assistant\AssistantWire.cs`:
```csharp
// LOCKED wire contract (design 2026-07-18 section 7.1) between the App and
// LocalScribe.Assistant.exe, and the typed event surface feat/matter-qa consumes.
// Request: ONE JSON line on the helper's stdin. Events: JSON lines on its stdout.
// With keepAlive:true the helper stays resident after "done" and accepts further
// {"op":"answer",...} lines reusing the loaded model + KV prefix (warm chat);
// keepAlive:false exits after done. No sockets anywhere - stdio only.
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalScribe.Core.Assistant;

/// <summary>One helper job request. PayloadJson is the raw JSON object embedded as
/// "payload" on the wire (v1 shape: AssistantWire.PromptPayload). Backend is the
/// REQUEST ("auto"|"cuda"|"cpu"); the backend actually used comes back on AssistantDone
/// (floor-fall provenance discipline).</summary>
public sealed record AssistantRequest(string Op, string ModelPath, int CtxTokens, string Backend, bool KeepAlive, string PayloadJson);

/// <summary>Typed stdout event stream. LOCKED contract - feat/matter-qa consumes these.</summary>
public abstract record AssistantEvent;
public sealed record AssistantChunk(string Text) : AssistantEvent;
public sealed record AssistantProgress(string Phase, int Current, int Total) : AssistantEvent;
public sealed record AssistantDone(string Backend, int PromptTokens, int OutputTokens) : AssistantEvent;
public sealed record AssistantError(string Message) : AssistantEvent;

public static class AssistantWire
{
    /// <summary>KV-cache quantization, constant on the wire (design 7.2: KV q8_0).</summary>
    public const string KvQuant = "q8_0";

    /// <summary>One request, one line. JsonObject.ToJsonString() is non-indented by design -
    /// LocalScribeJson (indented, for sidecar files) must never be used on the wire.</summary>
    public static string SerializeRequest(AssistantRequest request) => new JsonObject
    {
        ["op"] = request.Op,
        ["modelPath"] = request.ModelPath,
        ["ctxTokens"] = request.CtxTokens,
        ["kvQuant"] = KvQuant,
        ["backend"] = request.Backend,
        ["keepAlive"] = request.KeepAlive,
        ["payload"] = ParseOrEmpty(request.PayloadJson),
    }.ToJsonString();

    /// <summary>Helper-side request parse. Null on malformed/incomplete input - the helper
    /// answers with a protocol error event, it never crashes on bad stdin.</summary>
    public static AssistantRequest? ParseRequestLine(string line)
    {
        JsonObject? o = TryParseObject(line);
        if (o is null) return null;
        string? op = o["op"]?.GetValue<string>();
        string? modelPath = o["modelPath"]?.GetValue<string>();
        if (op is null || modelPath is null) return null;
        return new AssistantRequest(op, modelPath,
            o["ctxTokens"]?.GetValue<int>() ?? 0,
            o["backend"]?.GetValue<string>() ?? "auto",
            o["keepAlive"]?.GetValue<bool>() ?? false,
            o["payload"]?.ToJsonString() ?? "{}");
    }

    public static string SerializeEvent(AssistantEvent evt) => evt switch
    {
        AssistantChunk c => new JsonObject { ["type"] = "chunk", ["text"] = c.Text }.ToJsonString(),
        AssistantProgress p => new JsonObject
        { ["type"] = "progress", ["phase"] = p.Phase, ["current"] = p.Current, ["total"] = p.Total }.ToJsonString(),
        AssistantDone d => new JsonObject
        {
            ["type"] = "done",
            ["stats"] = new JsonObject
            { ["backend"] = d.Backend, ["promptTokens"] = d.PromptTokens, ["outputTokens"] = d.OutputTokens },
        }.ToJsonString(),
        AssistantError e => new JsonObject { ["type"] = "error", ["message"] = e.Message }.ToJsonString(),
        _ => throw new ArgumentOutOfRangeException(nameof(evt), evt.GetType().Name, "unknown assistant event"),
    };

    /// <summary>App-side event parse. Null on malformed/unknown lines - callers SKIP those
    /// (SherpaHelperDiariser precedent: stdout noise from native libs must never be fatal).</summary>
    public static AssistantEvent? ParseEventLine(string line)
    {
        JsonObject? o = TryParseObject(line);
        if (o is null) return null;
        return o["type"]?.GetValue<string>() switch
        {
            "chunk" => new AssistantChunk(o["text"]?.GetValue<string>() ?? ""),
            "progress" => new AssistantProgress(o["phase"]?.GetValue<string>() ?? "",
                o["current"]?.GetValue<int>() ?? 0, o["total"]?.GetValue<int>() ?? 0),
            "done" => o["stats"] is JsonObject s
                ? new AssistantDone(s["backend"]?.GetValue<string>() ?? "",
                    s["promptTokens"]?.GetValue<int>() ?? 0, s["outputTokens"]?.GetValue<int>() ?? 0)
                : new AssistantDone("", 0, 0),
            "error" => new AssistantError(o["message"]?.GetValue<string>() ?? ""),
            _ => null,
        };
    }

    /// <summary>The v1 payload both ops use: the fully-built prompt plus an output cap.</summary>
    public static string PromptPayload(string prompt, int maxTokens)
        => new JsonObject { ["prompt"] = prompt, ["maxTokens"] = maxTokens }.ToJsonString();

    private static JsonObject? TryParseObject(string line)
    {
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }
}
```
- [ ] **Run tests and see PASS.** Same filter as above — expected: 4 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Assistant/AssistantWire.cs tests/LocalScribe.Core.Tests/AssistantWireTests.cs
git commit -m "feat(core): assistant stdio wire contract - request/event records + both-direction codecs

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Core — `TokenBudget` + `AssistantInputShaper` (pure budget math + input shaping)
**Files:**
- Create `src\LocalScribe.Core\Assistant\TokenBudget.cs`
- Create `src\LocalScribe.Core\Assistant\AssistantInputShaper.cs`
- Create `tests\LocalScribe.Core.Tests\TokenBudgetTests.cs`

**Interfaces:**
- Produces (LOCKED contract members plus additive constants):
  - `public static class TokenBudget { public static int EstimateTokens(int chars); public static bool NeedsChunking(int estimatedTokens, int ctxTokens); public static int ChunkBudgetChars(int ctxTokens); }` using the spec's worst-case **2 chars/token** + **80% gate** + **map output cap 600 tokens** + **hierarchical reduce max depth 2** (design §7.4). Additive members (allowed beside the locked three): `WorstCaseCharsPerToken = 2`, `FitsGatePercent = 80`, `MapOutputCapTokens = 600`, `MaxReduceDepth = 2`, `OutputReserveTokens = 1200`, `MinCtxTokens = 4096`, `MaxCtxTokens = 32768`, `JobCtxTokens(int estimatedInputTokens)` (per-job `num_ctx` sizing, design §7.2: input estimate + output reserve, clamped to the 32k operating budget).
  - `AssistantInputShaper.StripLeadingTimestamps(string)` — line-anchored regex helper (LOCKED name); `AssistantInputShaper.BuildSpeakerPreamble(IReadOnlyList<string> speakerNames)` (LOCKED name); plus `BuildTranscriptText(IReadOnlyList<DisplayRow> rows)` — the summarization input builder: skips marker rows, emits `"{DisplayName}: {text}"` lines (named speaker labels KEPT — design §7.4, our advantage over Steno's two-bucket labels), timestamps never emitted and defensively stripped from text.
- Consumes: `LocalScribe.Core.Projection.DisplayRow` (`DisplayRow.cs:9-14`: `IsMarker`, `DisplayName` (null for markers), `Text`), `System.Text.RegularExpressions` (GeneratedRegex).

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\TokenBudgetTests.cs`:
```csharp
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Projection;

namespace LocalScribe.Core.Tests;

public class TokenBudgetTests
{
    [Fact]
    public void Estimate_uses_worst_case_two_chars_per_token()
    {
        // Design 2026-07-18 section 7.4: Steno's arithmetic - the gate must trip BEFORE overflow.
        Assert.Equal(0, TokenBudget.EstimateTokens(0));
        Assert.Equal(1, TokenBudget.EstimateTokens(1));    // ceil(1/2)
        Assert.Equal(1, TokenBudget.EstimateTokens(2));
        Assert.Equal(5000, TokenBudget.EstimateTokens(10000));
    }

    [Fact]
    public void Chunking_gate_trips_at_eighty_percent_of_ctx()
    {
        Assert.False(TokenBudget.NeedsChunking(12800, 16000));  // exactly 80% -> still fits
        Assert.True(TokenBudget.NeedsChunking(12801, 16000));   // one past the gate -> chunk
    }

    [Fact]
    public void Chunk_budget_reserves_the_map_output_cap()
    {
        // 80% of ctx minus the 600-token map output cap, back to chars at 2 chars/token.
        Assert.Equal((16000 * 80 / 100 - 600) * 2, TokenBudget.ChunkBudgetChars(16000));
    }

    [Fact]
    public void Job_ctx_sizes_to_the_job_within_the_operating_budget()
    {
        // Small job: floor. Mid job: input + reserve grossed up past the 80% gate. Huge: 32k cap.
        Assert.Equal(TokenBudget.MinCtxTokens, TokenBudget.JobCtxTokens(100));
        int mid = TokenBudget.JobCtxTokens(10000);
        Assert.False(TokenBudget.NeedsChunking(10000 + TokenBudget.OutputReserveTokens, mid));
        Assert.Equal(TokenBudget.MaxCtxTokens, TokenBudget.JobCtxTokens(1_000_000));
    }

    [Fact]
    public void StripLeadingTimestamps_is_line_anchored()
    {
        // Design 7.4: leading per-line timestamps stripped (a UI concern only); timestamps
        // INSIDE the utterance are content and must survive.
        Assert.Equal("hello there\nyes\n",
            AssistantInputShaper.StripLeadingTimestamps("[00:01:02] hello there\n12:34 yes\n"));
        Assert.Equal("meet at 10:30 tomorrow",
            AssistantInputShaper.StripLeadingTimestamps("meet at 10:30 tomorrow"));
        Assert.Equal("a\nb", AssistantInputShaper.StripLeadingTimestamps("[0:01:02.500] a\n01:02, b"));
    }

    [Fact]
    public void Speaker_preamble_lists_the_roster()
    {
        Assert.Equal("", AssistantInputShaper.BuildSpeakerPreamble([]));
        Assert.Equal("Speakers in this call: Sam, Client A.",
            AssistantInputShaper.BuildSpeakerPreamble(["Sam", "Client A"]));
    }

    [Fact]
    public void Transcript_text_keeps_named_speakers_and_skips_markers()
    {
        var rows = new List<DisplayRow>
        {
            new() { DisplayName = "Sam", Text = "Hello there.", StartMs = 0, EndMs = 900 },
            new() { IsMarker = true, Text = "recording paused", StartMs = 1000, EndMs = 1000 },
            new() { DisplayName = null, Text = "Hi.", StartMs = 2000, EndMs = 2500 },
        };
        Assert.Equal("Sam: Hello there.\nUnknown speaker: Hi.",
            AssistantInputShaper.BuildTranscriptText(rows));
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~TokenBudgetTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` — expected: `error CS0246: The type or namespace name 'TokenBudget' could not be found` (plus `AssistantInputShaper`).
- [ ] **Write `TokenBudget`.** Create `src\LocalScribe.Core\Assistant\TokenBudget.cs`:
```csharp
namespace LocalScribe.Core.Assistant;

/// <summary>Fits-check arithmetic (design 2026-07-18 section 7.4, LOCKED contract - feat/matter-qa
/// consumes EstimateTokens/NeedsChunking/ChunkBudgetChars for its raise-or-excerpt policy).
/// Worst-case 2 chars/token so the gate always trips BEFORE overflow, never after.</summary>
public static class TokenBudget
{
    public const int WorstCaseCharsPerToken = 2;
    public const int FitsGatePercent = 80;
    public const int MapOutputCapTokens = 600;
    public const int MaxReduceDepth = 2;
    /// <summary>Output tokens reserved when sizing a job's ctx (summary body budget).</summary>
    public const int OutputReserveTokens = 1200;
    public const int MinCtxTokens = 4096;
    /// <summary>The 32k operating budget (design decisions log: context budget).</summary>
    public const int MaxCtxTokens = 32768;

    public static int EstimateTokens(int chars)
        => (chars + WorstCaseCharsPerToken - 1) / WorstCaseCharsPerToken;

    public static bool NeedsChunking(int estimatedTokens, int ctxTokens)
        => estimatedTokens > ctxTokens * FitsGatePercent / 100;

    /// <summary>Input chars a map chunk may carry inside ctxTokens, after reserving the
    /// map output cap - so chunk input + chunk output both fit under the 80% gate.</summary>
    public static int ChunkBudgetChars(int ctxTokens)
        => (ctxTokens * FitsGatePercent / 100 - MapOutputCapTokens) * WorstCaseCharsPerToken;

    /// <summary>Per-job num_ctx (design 7.2: sized to the job, not a fixed max): input estimate
    /// plus the output reserve, grossed up so the 80% gate passes, clamped to the operating
    /// budget. Beyond MaxCtxTokens the caller goes to map-reduce instead.</summary>
    public static int JobCtxTokens(int estimatedInputTokens)
        => Math.Clamp((estimatedInputTokens + OutputReserveTokens) * 100 / FitsGatePercent + 1,
            MinCtxTokens, MaxCtxTokens);
}
```
- [ ] **Write `AssistantInputShaper`.** Create `src\LocalScribe.Core\Assistant\AssistantInputShaper.cs`:
```csharp
using System.Text;
using System.Text.RegularExpressions;
using LocalScribe.Core.Projection;

namespace LocalScribe.Core.Assistant;

/// <summary>Summarization/Q&A input shaping (design 2026-07-18 section 7.4): leading per-line
/// timestamps stripped (line-anchored regex - a UI concern only, never content mid-line);
/// NAMED speaker labels kept with a roster preamble. LOCKED helper names - feat/matter-qa
/// consumes StripLeadingTimestamps and BuildSpeakerPreamble.</summary>
public static partial class AssistantInputShaper
{
    // Line-anchored: optional [, H:MM / HH:MM / H:MM:SS with optional .ms, optional ], then
    // trailing separators. Multiline so ^ anchors EVERY line; a timestamp mid-sentence never matches.
    [GeneratedRegex(@"^\s*\[?\d{1,2}:\d{2}(:\d{2})?([.,]\d{1,3})?\]?[\s,\-]*",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPrefix();

    public static string StripLeadingTimestamps(string text) => TimestampPrefix().Replace(text, "");

    /// <summary>Roster preamble ("Speakers in this call: A, B.") - empty roster yields "".</summary>
    public static string BuildSpeakerPreamble(IReadOnlyList<string> speakerNames)
        => speakerNames.Count == 0 ? "" : $"Speakers in this call: {string.Join(", ", speakerNames)}.";

    /// <summary>The model-facing transcript: one "Name: text" line per speaker turn, marker rows
    /// skipped (they are workflow metadata, not speech), timestamps never emitted and defensively
    /// stripped from the text itself. Verbatim otherwise - no cleanup (locked evidentiary rule).</summary>
    public static string BuildTranscriptText(IReadOnlyList<DisplayRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            if (row.IsMarker) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(row.DisplayName ?? "Unknown speaker").Append(": ")
              .Append(StripLeadingTimestamps(row.Text).Trim());
        }
        return sb.ToString();
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 7 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Assistant/TokenBudget.cs src/LocalScribe.Core/Assistant/AssistantInputShaper.cs tests/LocalScribe.Core.Tests/TokenBudgetTests.cs
git commit -m "feat(core): TokenBudget fits-check math + assistant input shaper (roster kept, timestamps stripped)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Core — `AssistantPrompts` (PromptVersion, grounding line, section headers, all builders incl. `BuildAnswerPrompt`)
**Files:**
- Create `src\LocalScribe.Core\Assistant\AssistantPrompts.cs`
- Create `tests\LocalScribe.Core.Tests\AssistantPromptsTests.cs`

**Interfaces:**
- Produces (LOCKED): `public static class AssistantPrompts { public const int PromptVersion = 1; ... }` — builders for map/reduce/single-call summary prompts and (produced here, used by matter-qa) `BuildAnswerPrompt(...)`; the grounding line and the four fixed English section headers (**Summary / Key topics / Key statements / Follow-ups & commitments**) are constants here. Concrete surface:
  - `public const int PromptVersion = 1;` (bumped on ANY prompt change — artifacts reproducible-in-principle, design §7.3)
  - `public const string DraftLabel = "AI-generated draft — not a transcript; verify against the record.";` (the locked label, single source for every rendered/exported artifact)
  - `public const string GroundingLine = ...` (user-invisible, always appended app-side: extract only what is explicitly stated, do not infer)
  - `public static readonly IReadOnlyList<string> SectionHeaders`
  - `public static string BuildSummaryPrompt(string speakerPreamble, string transcriptText)`
  - `public static string BuildMapPrompt(string speakerPreamble, string chunkText, int chunkIndex, int chunkCount)`
  - `public static string BuildReducePrompt(string speakerPreamble, IReadOnlyList<string> mapOutputs)`
  - `public static string BuildAnswerPrompt(string speakerPreamble, string contextText, string question)` — strict-extractive, `[HH:MM:SS]` citation per claim (design §7.5; consumed by the NEXT branch, snapshot-pinned here so matter-qa cannot drift it silently)
- Consumes: nothing beyond BCL. Tests are SNAPSHOT tests keyed to `PromptVersion` (design §8).

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\AssistantPromptsTests.cs`:
```csharp
using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

public class AssistantPromptsTests
{
    // SNAPSHOT TESTS keyed to PromptVersion (design 2026-07-18 section 8). If any assertion
    // here needs changing, PromptVersion MUST be bumped in the same commit - that is the point.

    [Fact]
    public void Prompt_version_is_one_and_the_locked_constants_hold()
    {
        Assert.Equal(1, AssistantPrompts.PromptVersion);
        Assert.Equal("AI-generated draft — not a transcript; verify against the record.",
            AssistantPrompts.DraftLabel);   // em dash as escape: test source stays ASCII (project rule)
        Assert.Equal(new[] { "Summary", "Key topics", "Key statements", "Follow-ups & commitments" },
            AssistantPrompts.SectionHeaders);
        Assert.Contains("only what is explicitly stated", AssistantPrompts.GroundingLine);
        Assert.Contains("Do not infer", AssistantPrompts.GroundingLine);
    }

    [Fact]
    public void Summary_prompt_snapshot()
    {
        string p = AssistantPrompts.BuildSummaryPrompt("Speakers in this call: Sam.", "Sam: Hi.");
        Assert.Equal(
            "You are producing a private recall aid from a call transcript.\n" +
            "Speakers in this call: Sam.\n" +
            "Write exactly these Markdown sections, in this order, using these exact headers:\n" +
            "## Summary\n## Key topics\n## Key statements\n## Follow-ups & commitments\n" +
            AssistantPrompts.GroundingLine + "\n" +
            "If a section has nothing explicitly stated, write: None stated.\n" +
            "Transcript:\n" +
            "Sam: Hi.", p);
    }

    [Fact]
    public void Map_prompt_caps_output_and_names_the_part()
    {
        string p = AssistantPrompts.BuildMapPrompt("", "Sam: Hi.", 2, 5);
        Assert.Contains("part 2 of 5", p);
        Assert.Contains($"at most {TokenBudget.MapOutputCapTokens} tokens", p);
        Assert.Contains(AssistantPrompts.GroundingLine, p);
        Assert.EndsWith("Sam: Hi.", p);
    }

    [Fact]
    public void Reduce_prompt_merges_numbered_part_notes_into_the_sections()
    {
        string p = AssistantPrompts.BuildReducePrompt("Speakers in this call: Sam.", ["notes A", "notes B"]);
        Assert.Contains("## Summary", p);
        Assert.Contains("## Follow-ups & commitments", p);
        Assert.Contains("Part 1 notes:\nnotes A", p);
        Assert.Contains("Part 2 notes:\nnotes B", p);
        Assert.Contains(AssistantPrompts.GroundingLine, p);
    }

    [Fact]
    public void Answer_prompt_is_strict_extractive_with_timestamp_citations()
    {
        // Produced HERE, consumed by feat/matter-qa (design 7.5) - pinned so it cannot drift.
        string p = AssistantPrompts.BuildAnswerPrompt("", "ctx", "What was agreed?");
        Assert.Contains("ONLY the context below", p);
        Assert.Contains("[HH:MM:SS]", p);
        Assert.Contains("does not explicitly answer", p);
        Assert.Contains(AssistantPrompts.GroundingLine, p);
        Assert.Contains("Question:\nWhat was agreed?", p);
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~AssistantPromptsTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` — expected: `error CS0246: The type or namespace name 'AssistantPrompts' could not be found`.
- [ ] **Write the implementation.** Create `src\LocalScribe.Core\Assistant\AssistantPrompts.cs`:
```csharp
using System.Text;

namespace LocalScribe.Core.Assistant;

/// <summary>Every prompt the assistant sends (design 2026-07-18 sections 7.4/7.5). LOCKED
/// contract: PromptVersion is a bumped constant covering EVERY prompt change (artifacts are
/// reproducible-in-principle); BuildAnswerPrompt is produced here and consumed by
/// feat/matter-qa. Snapshot tests pin all output - changing any text here without bumping
/// PromptVersion is a blocking defect.</summary>
public static class AssistantPrompts
{
    public const int PromptVersion = 1;

    /// <summary>The locked artifact label (design section 1, evidentiary rules). Em dash
    /// escaped so this source file stays ASCII.</summary>
    public const string DraftLabel = "AI-generated draft — not a transcript; verify against the record.";

    /// <summary>User-invisible grounding line, always appended app-side (design 7.4).</summary>
    public const string GroundingLine =
        "Extract only what is explicitly stated in the transcript. Do not infer, speculate, or add outside knowledge.";

    /// <summary>The four fixed English section headers (body language follows the session).</summary>
    public static readonly IReadOnlyList<string> SectionHeaders =
        ["Summary", "Key topics", "Key statements", "Follow-ups & commitments"];

    private static string SectionHeaderBlock()
        => string.Join('\n', SectionHeaders.Select(h => "## " + h));

    public static string BuildSummaryPrompt(string speakerPreamble, string transcriptText)
        => "You are producing a private recall aid from a call transcript.\n"
         + (speakerPreamble.Length > 0 ? speakerPreamble + "\n" : "")
         + "Write exactly these Markdown sections, in this order, using these exact headers:\n"
         + SectionHeaderBlock() + "\n"
         + GroundingLine + "\n"
         + "If a section has nothing explicitly stated, write: None stated.\n"
         + "Transcript:\n"
         + transcriptText;

    public static string BuildMapPrompt(string speakerPreamble, string chunkText, int chunkIndex, int chunkCount)
        => $"You are reading part {chunkIndex} of {chunkCount} of a call transcript.\n"
         + (speakerPreamble.Length > 0 ? speakerPreamble + "\n" : "")
         + "List this part's topics, key statements (with who said them), and any follow-ups or "
         + $"commitments, as terse bullet notes of at most {TokenBudget.MapOutputCapTokens} tokens.\n"
         + GroundingLine + "\n"
         + "Transcript part:\n"
         + chunkText;

    public static string BuildReducePrompt(string speakerPreamble, IReadOnlyList<string> mapOutputs)
    {
        var sb = new StringBuilder()
            .Append("You are merging per-part notes from one call into a single recall aid.\n");
        if (speakerPreamble.Length > 0) sb.Append(speakerPreamble).Append('\n');
        sb.Append("Write exactly these Markdown sections, in this order, using these exact headers:\n")
          .Append(SectionHeaderBlock()).Append('\n')
          .Append(GroundingLine).Append('\n')
          .Append("If a section has nothing explicitly stated, write: None stated.\n");
        for (int i = 0; i < mapOutputs.Count; i++)
            sb.Append($"Part {i + 1} notes:\n").Append(mapOutputs[i]).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Strict-extractive Q&A (design 7.5): inference FORBIDDEN, one [HH:MM:SS]
    /// citation per claim. Consumed by feat/matter-qa; pinned here so it cannot drift.</summary>
    public static string BuildAnswerPrompt(string speakerPreamble, string contextText, string question)
        => "Answer the question using ONLY the context below.\n"
         + GroundingLine + "\n"
         + "Every claim in your answer must cite the timestamp of the segment it comes from, "
         + "in the form [HH:MM:SS], immediately after the claim.\n"
         + "If the context does not explicitly answer the question, say exactly that.\n"
         + (speakerPreamble.Length > 0 ? speakerPreamble + "\n" : "")
         + "Context:\n" + contextText + "\n"
         + "Question:\n" + question;
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 5 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Assistant/AssistantPrompts.cs tests/LocalScribe.Core.Tests/AssistantPromptsTests.cs
git commit -m "feat(core): AssistantPrompts - versioned summary/map/reduce/answer builders + locked draft label

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Core — assistant model manifest (`AssistantModelInfo`, `AssistantModelRef`, `AssistantModelManifest` + verify-on-load, `AssistantManifestCache`)
**Files:**
- Create `src\LocalScribe.Core\Assistant\AssistantModels.cs`
- Create `tests\LocalScribe.Core.Tests\AssistantModelManifestTests.cs`

**Interfaces:**
- Produces (LOCKED):
  - `public sealed record AssistantModelInfo(string CanonicalName, string FilePath, string Sha256, int NativeCtx, string License);` (`FilePath` = absolute resolved path under the models root)
  - `public sealed class AssistantModelManifest { public IReadOnlyList<AssistantModelInfo> Installed { get; } public AssistantModelInfo? DefaultModel { get; } }` (+ load/verify factory `public static Task<AssistantModelManifest> LoadAsync(string modelsRoot, CancellationToken ct)`). Additive members: `public const string DefaultCanonicalName = "Qwen3-4B-Instruct-2507";`, `public const int Version = 1;`, `public IReadOnlyList<string> Notes { get; }` (surfaced degradation: missing/corrupt entries listed, never silent), and a public ctor `AssistantModelManifest(IReadOnlyList<AssistantModelInfo> installed, AssistantModelInfo? defaultModel, IReadOnlyList<string> notes)` so tests and fakes can build one.
  - `public sealed record AssistantModelRef(string File, string Sha256, string Backend);` (artifact provenance — file NAME + hash + the backend ACTUALLY used, from `AssistantDone`)
  - Manifest file records `AssistantManifestEntry { CanonicalName, File, Sha256, NativeCtx, License }` / `AssistantManifestFile { SchemaVersion = 1, Models }` — the shape `tools\fetch-models.ps1` writes to `models\assistant-manifest.json` (Task 5), design §7.2 `{canonicalName, file, sha256, nativeCtx, license}`.
  - `public sealed class AssistantManifestCache(Func<CancellationToken, Task<AssistantModelManifest>> load)` — `Task<AssistantModelManifest> GetAsync(CancellationToken ct)` (loads once, caches; hashing a 2.5 GB file is seconds, done off the UI thread by callers), `void Invalidate()`.
- Consumes: `SchemaGuard` + `JsonFile`/`LocalScribeJson` (the sidecar pattern, `SpeakersStore` shape), `System.Security.Cryptography.SHA256.HashDataAsync` for **verify-on-load** (LOCKED: manifest with sha256; verify-on-load — note: whisper weights have no on-load hash check today, this is deliberately stricter for assistant models per the design). Missing manifest → empty manifest (features off with explainer, §7.7), never a throw.

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\AssistantModelManifestTests.cs`:
```csharp
using System.Security.Cryptography;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

public sealed class AssistantModelManifestTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-manifest-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private async Task WriteManifestAsync(params AssistantManifestEntry[] entries)
        => await JsonFile.WriteAsync(Path.Combine(_root, "assistant-manifest.json"),
            new AssistantManifestFile { Models = entries }, CancellationToken.None);

    private string WriteModel(string file, string content)
    {
        string path = Path.Combine(_root, file);
        File.WriteAllText(path, content);
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task Missing_manifest_yields_an_empty_manifest_not_a_throw()
    {
        // Design 7.7: model missing -> features off with explainer; loading must never fault the app.
        var m = await AssistantModelManifest.LoadAsync(_root, CancellationToken.None);
        Assert.Empty(m.Installed);
        Assert.Null(m.DefaultModel);
    }

    [Fact]
    public async Task Verified_entries_install_and_the_locked_default_is_preferred()
    {
        string shaQ = WriteModel("q4b.gguf", "fake qwen weights");
        string shaS = WriteModel("q17b.gguf", "fake small weights");
        await WriteManifestAsync(
            new AssistantManifestEntry { CanonicalName = "Qwen3-1.7B-Instruct", File = "q17b.gguf", Sha256 = shaS, NativeCtx = 32768, License = "Apache-2.0" },
            new AssistantManifestEntry { CanonicalName = "Qwen3-4B-Instruct-2507", File = "q4b.gguf", Sha256 = shaQ, NativeCtx = 262144, License = "Apache-2.0" });

        var m = await AssistantModelManifest.LoadAsync(_root, CancellationToken.None);
        Assert.Equal(2, m.Installed.Count);
        Assert.Equal("Qwen3-4B-Instruct-2507", m.DefaultModel!.CanonicalName);   // LOCKED default
        Assert.Equal(Path.Combine(_root, "q4b.gguf"), m.DefaultModel.FilePath);
        Assert.Equal(262144, m.DefaultModel.NativeCtx);
        Assert.Empty(m.Notes);
    }

    [Fact]
    public async Task Missing_or_tampered_files_are_excluded_with_a_note_never_silently()
    {
        string sha = WriteModel("ok.gguf", "good");
        WriteModel("bad.gguf", "tampered");   // hash will NOT match the manifest pin below
        await WriteManifestAsync(
            new AssistantManifestEntry { CanonicalName = "Qwen3-4B-Instruct-2507", File = "ok.gguf", Sha256 = sha, NativeCtx = 262144, License = "Apache-2.0" },
            new AssistantManifestEntry { CanonicalName = "Qwen3-1.7B-Instruct", File = "bad.gguf", Sha256 = new string('0', 64), NativeCtx = 32768, License = "Apache-2.0" },
            new AssistantManifestEntry { CanonicalName = "Gemma-4-E2B-QAT", File = "absent.gguf", Sha256 = sha, NativeCtx = 32768, License = "Gemma Terms of Use" });

        var m = await AssistantModelManifest.LoadAsync(_root, CancellationToken.None);
        Assert.Single(m.Installed);                                   // fail-closed: corrupt never offered
        Assert.Equal("Qwen3-4B-Instruct-2507", m.Installed[0].CanonicalName);
        Assert.Equal(2, m.Notes.Count);                               // surfaced, never silent
        Assert.Contains(m.Notes, n => n.Contains("bad.gguf") && n.Contains("sha256"));
        Assert.Contains(m.Notes, n => n.Contains("absent.gguf") && n.Contains("missing"));
    }

    [Fact]
    public async Task Default_falls_back_to_the_first_installed_when_the_locked_default_is_absent()
    {
        string sha = WriteModel("q17b.gguf", "small");
        await WriteManifestAsync(new AssistantManifestEntry
        { CanonicalName = "Qwen3-1.7B-Instruct", File = "q17b.gguf", Sha256 = sha, NativeCtx = 32768, License = "Apache-2.0" });
        var m = await AssistantModelManifest.LoadAsync(_root, CancellationToken.None);
        Assert.Equal("Qwen3-1.7B-Instruct", m.DefaultModel!.CanonicalName);
    }

    [Fact]
    public async Task Cache_loads_once_and_reloads_after_invalidate()
    {
        int loads = 0;
        var cache = new AssistantManifestCache(_ =>
        { loads++; return Task.FromResult(new AssistantModelManifest([], null, [])); });
        await cache.GetAsync(CancellationToken.None);
        await cache.GetAsync(CancellationToken.None);
        Assert.Equal(1, loads);
        cache.Invalidate();
        await cache.GetAsync(CancellationToken.None);
        Assert.Equal(2, loads);
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~AssistantModelManifestTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` — expected: `error CS0246: The type or namespace name 'AssistantManifestEntry' could not be found` (plus `AssistantModelManifest`, `AssistantManifestCache`).
- [ ] **Write the implementation.** Create `src\LocalScribe.Core\Assistant\AssistantModels.cs`:
```csharp
using System.Security.Cryptography;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Assistant;

/// <summary>One installed, hash-verified GGUF model (design 2026-07-18 section 7.2).
/// LOCKED contract - feat/matter-qa consumes this record. FilePath is absolute.</summary>
public sealed record AssistantModelInfo(string CanonicalName, string FilePath, string Sha256, int NativeCtx, string License);

/// <summary>Artifact provenance (design 7.3 model{file,sha256,backend}): the model FILE NAME,
/// its pinned hash, and the backend ACTUALLY used (from AssistantDone - floor-fall discipline).
/// LOCKED contract - stored inside SummaryVersion and matter-qa chat artifacts.</summary>
public sealed record AssistantModelRef(string File, string Sha256, string Backend);

/// <summary>On-disk manifest entry, written by tools/fetch-models.ps1 into
/// models/assistant-manifest.json (design 7.2 {canonicalName, file, sha256, nativeCtx, license}).</summary>
public sealed record AssistantManifestEntry
{
    public string CanonicalName { get; init; } = "";
    public string File { get; init; } = "";
    public string Sha256 { get; init; } = "";
    public int NativeCtx { get; init; }
    public string License { get; init; } = "";
}

public sealed record AssistantManifestFile
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<AssistantManifestEntry> Models { get; init; } = [];
}

/// <summary>The verified view of installed assistant models. LOCKED contract surface:
/// Installed + DefaultModel + the LoadAsync factory. Verify-on-load is deliberate (locked
/// rule: manifest with sha256; verify-on-load): a corrupt/tampered GGUF is EXCLUDED with a
/// note, never offered - stricter than whisper weights, which have no on-load hash today.</summary>
public sealed class AssistantModelManifest
{
    /// <summary>Design decisions log: default model LOCKED, no bake-off.</summary>
    public const string DefaultCanonicalName = "Qwen3-4B-Instruct-2507";
    public const int Version = 1;

    public IReadOnlyList<AssistantModelInfo> Installed { get; }
    public AssistantModelInfo? DefaultModel { get; }
    /// <summary>Human-readable reasons entries were excluded (surfaced degradation, never silent).</summary>
    public IReadOnlyList<string> Notes { get; }

    public AssistantModelManifest(IReadOnlyList<AssistantModelInfo> installed,
        AssistantModelInfo? defaultModel, IReadOnlyList<string> notes)
        => (Installed, DefaultModel, Notes) = (installed, defaultModel, notes);

    /// <summary>Loads models/assistant-manifest.json under modelsRoot and hash-verifies every
    /// entry's file. Missing manifest or empty models dir yields an EMPTY manifest (design 7.7:
    /// features off with explainer) - never a throw. Hashing is streamed; callers run this off
    /// the UI thread and cache via AssistantManifestCache.</summary>
    public static async Task<AssistantModelManifest> LoadAsync(string modelsRoot, CancellationToken ct)
    {
        string path = Path.Combine(modelsRoot, "assistant-manifest.json");
        var obj = await SchemaGuard.ReadObjectAsync(path, ct);
        if (obj is null) return new AssistantModelManifest([], null, []);
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "assistant-manifest.json");
        var file = await JsonFile.ReadAsync<AssistantManifestFile>(path, ct)
                   ?? new AssistantManifestFile();

        var installed = new List<AssistantModelInfo>();
        var notes = new List<string>();
        foreach (var entry in file.Models)
        {
            ct.ThrowIfCancellationRequested();
            string modelPath = Path.Combine(modelsRoot, entry.File);
            if (!System.IO.File.Exists(modelPath))
            { notes.Add($"{entry.File}: missing - run tools/fetch-models.ps1 -Assistant"); continue; }
            string actual;
            await using (var stream = System.IO.File.OpenRead(modelPath))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
            if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
            { notes.Add($"{entry.File}: sha256 mismatch - file excluded (re-run the fetch script)"); continue; }
            installed.Add(new AssistantModelInfo(entry.CanonicalName, modelPath,
                entry.Sha256.ToLowerInvariant(), entry.NativeCtx, entry.License));
        }
        var def = installed.FirstOrDefault(m => m.CanonicalName == DefaultCanonicalName)
                  ?? installed.FirstOrDefault();
        return new AssistantModelManifest(installed, def, notes);
    }
}

/// <summary>Process-wide once-per-load cache over LoadAsync (hash-verifying a multi-GB file
/// per call would be wasteful). Invalidate() forces a reload (Settings refresh path).</summary>
public sealed class AssistantManifestCache(Func<CancellationToken, Task<AssistantModelManifest>> load)
{
    private readonly object _lock = new();
    private Task<AssistantModelManifest>? _cached;

    public Task<AssistantModelManifest> GetAsync(CancellationToken ct)
    {
        lock (_lock) return _cached ??= load(ct);
    }

    public void Invalidate() { lock (_lock) _cached = null; }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 5 passed. (Note: `Convert.ToHexStringLower` is .NET 9+; this repo targets net10.0 — available.)
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Assistant/AssistantModels.cs tests/LocalScribe.Core.Tests/AssistantModelManifestTests.cs
git commit -m "feat(core): assistant model manifest with sha256 verify-on-load + manifest cache

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: Tooling — GGUF assistant models in `tools\fetch-models.ps1` (SHA-pinned fetch + manifest write)
**Files:**
- Modify `tools\fetch-models.ps1` (add a `param(...)` block at the very top — the file currently starts with the comment header lines 1-3 then `$ErrorActionPreference = 'Stop'` at line 4; add the assistant table + fetch loop + manifest write at the END, after line 152 `Write-Host "done -> $models"` — replace that closing line as shown).
- No unit test project change — the verification step is a parse check plus a Core test already written in Task 4 (the manifest loader consumes what this writes). The multi-GB download itself is a USER smoke step (Task 14 runbook), exactly like the existing whisper/diarisation model fetches.

**Interfaces:**
- Produces: `pwsh tools/fetch-models.ps1 -Assistant` downloads the LOCKED default `Qwen3-4B-Instruct-2507-Q4_K_M.gguf` (~2.5 GB) into `models\`, SHA-verified fail-closed, and writes `models\assistant-manifest.json` in the Task-4 `AssistantManifestFile` shape (camelCase). `-AssistantOptional` adds `Qwen3-1.7B-Instruct` q4_K_M and `Gemma 4 E2B QAT`. Running WITHOUT the switches changes nothing (the existing whisper/diarisation flow is untouched).
- Consumes: the script's existing `Get-RemoteFile` (mirrors/resume/backoff, lines 13-45) and `Assert-Sha256` (fail-closed delete-on-mismatch, lines 49-61) — reused verbatim, not duplicated.
- **Pin source (recorded deviation 2, Global Constraints):** each GGUF's sha256 is read from its Hugging Face LFS pointer (`.../raw/main/<file>` → `oid sha256:<64hex>`) over TLS BEFORE the blob download, then enforced by `Assert-Sha256` and recorded into the manifest, which Core re-verifies on every load (Task 4).

Steps:
- [ ] **Add the param block.** At the very top of `tools\fetch-models.ps1`, BEFORE line 1's comment (a `param` block must be the first statement, but comments may precede it — put it after the header comment lines 1-3 and before line 4 `$ErrorActionPreference = 'Stop'`):
```powershell
param(
    # Also fetch the LOCKED default assistant LLM (design 2026-07-18 section 7.2):
    # Qwen3-4B-Instruct-2507 q4_K_M GGUF, ~2.5 GB, Apache-2.0. SHA-pinned from the
    # Hugging Face LFS pointer (fetched over TLS before the blob), verified fail-closed,
    # and recorded into models/assistant-manifest.json (Core re-verifies on load).
    [switch] $Assistant,
    # Also fetch the optional assistant entries (Qwen3-1.7B q4_K_M ~1 GB, Gemma 4 E2B QAT).
    [switch] $AssistantOptional
)
```
- [ ] **Append the assistant flow.** Replace the final line 152:
```powershell
Write-Host "done -> $models"
```
with:
```powershell
# --- Assistant LLMs (GGUF, design 2026-07-18 section 7.2) -------------------------------
# The sha256 pin comes from the Hugging Face LFS pointer file (raw/main), fetched over TLS
# BEFORE the multi-GB blob; Assert-Sha256 then enforces it fail-closed, and the verified
# pin lands in models/assistant-manifest.json, which the app re-verifies on every load.
function Get-HfPinnedSha256 {
    param([string] $PointerUrl)
    $resp = Invoke-WebRequest -Uri $PointerUrl
    $text = if ($resp.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($resp.Content) } else { [string]$resp.Content }
    if ($text -match 'oid sha256:([0-9a-fA-F]{64})') { return $Matches[1].ToLowerInvariant() }
    throw "no sha256 oid in LFS pointer at $PointerUrl - wrong path, or the file is not LFS-tracked"
}

if ($Assistant -or $AssistantOptional) {
    # Default LOCKED: Qwen3-4B-Instruct-2507 q4_K_M (decisions log - no bake-off).
    # Optional: Qwen3-1.7B q4_K_M (low-end/CPU-only), Gemma 4 E2B QAT (Gemma ToU).
    # NOTE (plan deviation 2): confirm the optional repos' exact paths on Hugging Face at
    # execution time - Get-HfPinnedSha256 fails loudly on a wrong path, nothing silent.
    $assistantModels = @(
        @{ CanonicalName = 'Qwen3-4B-Instruct-2507'; NativeCtx = 262144; License = 'Apache-2.0'
           File = 'Qwen3-4B-Instruct-2507-Q4_K_M.gguf'; Optional = $false
           Url  = 'https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf'
           Ptr  = 'https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507-GGUF/raw/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf' },
        @{ CanonicalName = 'Qwen3-1.7B-Instruct'; NativeCtx = 32768; License = 'Apache-2.0'
           File = 'Qwen3-1.7B-Q4_K_M.gguf'; Optional = $true
           Url  = 'https://huggingface.co/Qwen/Qwen3-1.7B-GGUF/resolve/main/Qwen3-1.7B-Q4_K_M.gguf'
           Ptr  = 'https://huggingface.co/Qwen/Qwen3-1.7B-GGUF/raw/main/Qwen3-1.7B-Q4_K_M.gguf' },
        @{ CanonicalName = 'Gemma-4-E2B-QAT'; NativeCtx = 32768; License = 'Gemma Terms of Use'
           File = 'gemma-4-e2b-it-qat-q4_0.gguf'; Optional = $true
           Url  = 'https://huggingface.co/google/gemma-4-e2b-it-qat-q4_0-gguf/resolve/main/gemma-4-e2b-it-qat-q4_0.gguf'
           Ptr  = 'https://huggingface.co/google/gemma-4-e2b-it-qat-q4_0-gguf/raw/main/gemma-4-e2b-it-qat-q4_0.gguf' }
    )

    $manifestEntries = @()
    foreach ($m in $assistantModels) {
        if ($m.Optional -and -not $AssistantOptional) { continue }
        $dest = Join-Path $models $m.File
        Write-Host "pin: $($m.File)"
        $pin = Get-HfPinnedSha256 -PointerUrl $m.Ptr
        Write-Host "  pinned sha256: $pin"
        if (-not (Test-Path $dest)) {
            Write-Host "fetching: $($m.File)"
            Get-RemoteFile -Uris @($m.Url) -OutFile $dest
        } else {
            Write-Host "exists: $($m.File)"
        }
        Assert-Sha256 -Path $dest -ExpectedSha256 $pin   # fail-closed: deletes on mismatch
        $manifestEntries += [ordered]@{
            canonicalName = $m.CanonicalName
            file          = $m.File
            sha256        = $pin
            nativeCtx     = $m.NativeCtx
            license       = $m.License
        }
    }

    if ($manifestEntries.Count -gt 0) {
        # Merge with any entries already in the manifest for files still present on disk
        # (so -Assistant after -AssistantOptional does not drop the optional entries).
        $manifestPath = Join-Path $models 'assistant-manifest.json'
        if (Test-Path $manifestPath) {
            $existing = (Get-Content $manifestPath -Raw | ConvertFrom-Json).models
            foreach ($e in $existing) {
                if (($manifestEntries | Where-Object { $_.file -eq $e.file }).Count -eq 0 -and
                    (Test-Path (Join-Path $models $e.file))) {
                    $manifestEntries += [ordered]@{
                        canonicalName = $e.canonicalName; file = $e.file
                        sha256 = $e.sha256; nativeCtx = $e.nativeCtx; license = $e.license
                    }
                }
            }
        }
        $manifest = [ordered]@{ schemaVersion = 1; models = $manifestEntries }
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -Path $manifestPath -Encoding utf8
        Write-Host "manifest -> $manifestPath ($($manifestEntries.Count) model(s))"
    }
}

Write-Host "done -> $models"
```
- [ ] **Verify the script parses (no download).** Run:
```
pwsh -NoProfile -Command "$errs = $null; [System.Management.Automation.Language.Parser]::ParseFile('F:\LocalScribe\tools\fetch-models.ps1', [ref]$null, [ref]$errs) | Out-Null; if ($errs.Count) { $errs | ForEach-Object { $_.Message }; exit 1 } else { 'parse OK' }"
```
Expected: `parse OK`. Then confirm the legacy path is untouched: `pwsh -NoProfile -File tools/fetch-models.ps1` with all whisper/diarisation models already on disk prints the existing `exists:`/`verified:` lines and `done -> ...` WITHOUT touching the network for GGUFs (no switches given). Do NOT run `-Assistant` here — the 2.5 GB download is the user's smoke step (Task 14).
- [ ] **Manifest-shape cross-check (no new test needed).** The Task-4 test `Verified_entries_install_and_the_locked_default_is_preferred` already pins the exact camelCase shape (`schemaVersion`/`models`/`canonicalName`/`file`/`sha256`/`nativeCtx`/`license`) via `JsonFile.WriteAsync` + `AssistantManifestFile` — the `[ordered]` hashtable above serializes to the same property names. Re-run it as the cross-check: `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~AssistantModelManifestTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` — expected: 5 passed.
- [ ] **Commit.**
```
git add tools/fetch-models.ps1
git commit -m "build(tools): fetch-models.ps1 -Assistant fetches SHA-pinned GGUF LLMs + writes assistant-manifest.json

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: Core — process seams + `AssistantJobRunner` + warm chat-session factory
**Files:**
- Create `src\LocalScribe.Core\Assistant\AssistantJobRunner.cs`
- Create `tests\LocalScribe.Core.Tests\AssistantJobRunnerTests.cs`

**Interfaces:**
- Produces (LOCKED):
  - `public interface IAssistantJobRunner { IAsyncEnumerable<AssistantEvent> RunAsync(AssistantRequest request, CancellationToken ct); }` (impl `AssistantJobRunner` spawns the helper — spawn-per-job, design §7.1)
  - Keep-alive session variant: `IAssistantChatSession { IAsyncEnumerable<AssistantEvent> AskAsync(string questionPayloadJson, CancellationToken ct); ValueTask DisposeAsync(); }` created via `IAssistantChatSessionFactory.StartAsync(AssistantRequest warmupRequest, CancellationToken ct)`. Declared as `public interface IAssistantChatSession : IAsyncDisposable { IAsyncEnumerable<AssistantEvent> AskAsync(string questionPayloadJson, CancellationToken ct); }` — `IAsyncDisposable` supplies the contract's `ValueTask DisposeAsync()`. Factory: `public interface IAssistantChatSessionFactory { Task<IAssistantChatSession> StartAsync(AssistantRequest warmupRequest, CancellationToken ct); }`, impl `AssistantChatSessionFactory` (warm helper: model+KV prefix loaded once by the warmup request; each `AskAsync` sends `{"op":"answer",...}` on the LIVE process — §7.1 warm-chat contract; teardown = `DisposeAsync` kills the process).
  - Process seams (the `IDiarisationHelper` pattern, adapted for a persistent-stdin loop): `public interface IAssistantProcess : IAsyncDisposable { Task WriteRequestLineAsync(string requestJson, CancellationToken ct); Task<string?> ReadEventLineAsync(CancellationToken ct); void Kill(); }` and `public interface IAssistantProcessFactory { Task<IAssistantProcess> StartAsync(CancellationToken ct); }` (production impl = App's `ProcessAssistantHelper`, Task 7; tests supply fakes emitting canned lines — exactly the `SherpaHelperDiariserTests.FakeHelper` strategy).
  - `public sealed class AssistantException(string message) : Exception(message)` — typed failure for callers that need throw semantics.
- Behavior contract: malformed stdout lines are SKIPPED (never fatal); EOF before a terminal event → synthesized `AssistantError` ("exited before completing"); an inactivity watchdog (default 5 min, injectable) kills the process and synthesizes an `AssistantError` — the design's App-side watchdog; caller cancellation kills the process (cancel = process kill, §7.1) and propagates `OperationCanceledException`. After `AssistantDone` or `AssistantError` the enumeration ends. The runner NEVER writes files (App owns persistence).
- Consumes: `AssistantWire` (Task 1).

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\AssistantJobRunnerTests.cs`:
```csharp
using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

public class AssistantJobRunnerTests
{
    /// <summary>In-process fake of the process seam (SherpaHelperDiariserTests.FakeHelper
    /// strategy): replays canned stdout lines per written request; records requests and kills.</summary>
    private sealed class FakeProcess : IAssistantProcess
    {
        private readonly Func<string, IEnumerable<string?>> _script;
        private readonly Queue<string?> _pending = new();
        public List<string> Requests { get; } = [];
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public bool HangAfterScript { get; init; }   // watchdog test: reads block forever

        public FakeProcess(Func<string, IEnumerable<string?>> script) => _script = script;

        public Task WriteRequestLineAsync(string requestJson, CancellationToken ct)
        {
            Requests.Add(requestJson);
            foreach (var line in _script(requestJson)) _pending.Enqueue(line);
            return Task.CompletedTask;
        }

        public async Task<string?> ReadEventLineAsync(CancellationToken ct)
        {
            if (_pending.Count > 0) { await Task.Yield(); return _pending.Dequeue(); }
            if (HangAfterScript) { await Task.Delay(Timeout.Infinite, ct); }
            return null;   // EOF
        }

        public void Kill() => Killed = true;
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeFactory(FakeProcess proc) : IAssistantProcessFactory
    {
        public int Starts { get; private set; }
        public Task<IAssistantProcess> StartAsync(CancellationToken ct)
        { Starts++; return Task.FromResult<IAssistantProcess>(proc); }
    }

    private static AssistantRequest Req(bool keepAlive = false)
        => new("summarize", @"C:\models\q.gguf", 8192, "auto", keepAlive, "{\"prompt\":\"p\",\"maxTokens\":600}");

    private static async Task<List<AssistantEvent>> Collect(IAsyncEnumerable<AssistantEvent> stream)
    {
        var list = new List<AssistantEvent>();
        await foreach (var e in stream) list.Add(e);
        return list;
    }

    [Fact]
    public async Task Run_streams_typed_events_skips_noise_and_stops_at_done()
    {
        var proc = new FakeProcess(_ => new string?[]
        {
            "{\"type\":\"progress\",\"phase\":\"prefill\",\"current\":1,\"total\":2}",
            "llama.cpp native noise line",                                   // skipped, never fatal
            "{\"type\":\"chunk\",\"text\":\"Hello\"}",
            "{\"type\":\"done\",\"stats\":{\"backend\":\"cuda\",\"promptTokens\":9,\"outputTokens\":1}}",
        });
        var runner = new AssistantJobRunner(new FakeFactory(proc));
        var events = await Collect(runner.RunAsync(Req(), CancellationToken.None));

        Assert.Equal(new AssistantEvent[]
            { new AssistantProgress("prefill", 1, 2), new AssistantChunk("Hello"), new AssistantDone("cuda", 9, 1) },
            events);
        Assert.Single(proc.Requests);
        Assert.Contains("\"kvQuant\":\"q8_0\"", proc.Requests[0]);   // the locked wire rode through
        Assert.True(proc.Disposed);                                   // spawn-per-job: torn down after
    }

    [Fact]
    public async Task Eof_before_a_terminal_event_synthesizes_a_visible_error()
    {
        // Design 7.7: helper crash -> visible error, nothing persisted.
        var proc = new FakeProcess(_ => new string?[] { "{\"type\":\"chunk\",\"text\":\"par\"}" });
        var events = await Collect(new AssistantJobRunner(new FakeFactory(proc))
            .RunAsync(Req(), CancellationToken.None));
        var err = Assert.IsType<AssistantError>(events[^1]);
        Assert.Contains("exited before completing", err.Message);
    }

    [Fact]
    public async Task Watchdog_kills_a_silent_helper_and_surfaces_an_error()
    {
        var proc = new FakeProcess(_ => Array.Empty<string?>()) { HangAfterScript = true };
        var runner = new AssistantJobRunner(new FakeFactory(proc), inactivityTimeout: TimeSpan.FromMilliseconds(100));
        var events = await Collect(runner.RunAsync(Req(), CancellationToken.None));
        var err = Assert.IsType<AssistantError>(Assert.Single(events));
        Assert.Contains("watchdog", err.Message);
        Assert.True(proc.Killed);
    }

    [Fact]
    public async Task Cancellation_kills_the_process_and_throws()
    {
        var proc = new FakeProcess(_ => Array.Empty<string?>()) { HangAfterScript = true };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Collect(new AssistantJobRunner(new FakeFactory(proc)).RunAsync(Req(), cts.Token)));
        Assert.True(proc.Killed);
    }

    [Fact]
    public async Task Chat_session_warms_once_then_answers_on_the_live_process()
    {
        // Design 7.1 warm-chat contract: warmup prefilled once; AskAsync reuses model+KV.
        var proc = new FakeProcess(req => new string?[]
        {
            req.Contains("\"op\":\"answer\"")
                ? "{\"type\":\"chunk\",\"text\":\"A1\"}"
                : "{\"type\":\"progress\",\"phase\":\"prefill\",\"current\":1,\"total\":1}",
            "{\"type\":\"done\",\"stats\":{\"backend\":\"cpu\",\"promptTokens\":5,\"outputTokens\":1}}",
        });
        var factory = new FakeFactory(proc);
        var sessions = new AssistantChatSessionFactory(factory);

        await using var chat = await sessions.StartAsync(Req(keepAlive: true), CancellationToken.None);
        Assert.Equal(1, factory.Starts);
        Assert.Single(proc.Requests);                                  // the warmup request
        Assert.Contains("\"keepAlive\":true", proc.Requests[0]);       // warmup FORCED keep-alive

        var a1 = await Collect(chat.AskAsync("{\"prompt\":\"q1\",\"maxTokens\":400}", CancellationToken.None));
        var a2 = await Collect(chat.AskAsync("{\"prompt\":\"q2\",\"maxTokens\":400}", CancellationToken.None));
        Assert.Equal(new AssistantChunk("A1"), a1[0]);
        Assert.Equal(new AssistantChunk("A1"), a2[0]);
        Assert.Equal(3, proc.Requests.Count);                          // warmup + 2 answers, ONE process
        Assert.Equal(1, factory.Starts);
        Assert.All(proc.Requests.Skip(1), r => Assert.Contains("\"op\":\"answer\"", r));

        await chat.DisposeAsync();
        Assert.True(proc.Killed);                                      // teardown = process kill
    }

    [Fact]
    public async Task Chat_warmup_failure_disposes_the_process_and_throws()
    {
        var proc = new FakeProcess(_ => new string?[] { "{\"type\":\"error\",\"message\":\"MODEL_MISSING\"}" });
        var ex = await Assert.ThrowsAsync<AssistantException>(() =>
            new AssistantChatSessionFactory(new FakeFactory(proc))
                .StartAsync(Req(keepAlive: true), CancellationToken.None));
        Assert.Contains("MODEL_MISSING", ex.Message);
        Assert.True(proc.Disposed);
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~AssistantJobRunnerTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` — expected: `error CS0246: The type or namespace name 'IAssistantProcess' could not be found` (plus `AssistantJobRunner`, `AssistantChatSessionFactory`, `AssistantException`).
- [ ] **Write the implementation.** Create `src\LocalScribe.Core\Assistant\AssistantJobRunner.cs`:
```csharp
using System.Runtime.CompilerServices;

namespace LocalScribe.Core.Assistant;

/// <summary>Process-boundary seam (the IDiarisationHelper pattern, adapted for a persistent
/// stdin so keepAlive chat can send further requests on the live process). Production impl:
/// App's ProcessAssistantHelper (spawns LocalScribe.Assistant.exe, kills the whole tree).
/// Tests supply fakes that replay canned stdout lines.</summary>
public interface IAssistantProcess : IAsyncDisposable
{
    Task WriteRequestLineAsync(string requestJson, CancellationToken ct);
    /// <summary>Next stdout line, or null at EOF (helper exited).</summary>
    Task<string?> ReadEventLineAsync(CancellationToken ct);
    void Kill();
}

public interface IAssistantProcessFactory
{
    Task<IAssistantProcess> StartAsync(CancellationToken ct);
}

/// <summary>LOCKED contract - feat/matter-qa consumes this exact interface.</summary>
public interface IAssistantJobRunner
{
    IAsyncEnumerable<AssistantEvent> RunAsync(AssistantRequest request, CancellationToken ct);
}

/// <summary>LOCKED contract (warm-chat, design 7.1): the warmup request loads model + prefills
/// the scope context ONCE; each AskAsync sends only {"op":"answer",...} on the live process, so
/// per-question latency is generation-only. DisposeAsync (IAsyncDisposable) kills the helper.</summary>
public interface IAssistantChatSession : IAsyncDisposable
{
    IAsyncEnumerable<AssistantEvent> AskAsync(string questionPayloadJson, CancellationToken ct);
}

public interface IAssistantChatSessionFactory
{
    Task<IAssistantChatSession> StartAsync(AssistantRequest warmupRequest, CancellationToken ct);
}

/// <summary>Typed assistant failure for callers needing throw semantics (design 7.7:
/// visible error, nothing persisted).</summary>
public sealed class AssistantException(string message) : Exception(message);

/// <summary>Shared line-pump: reads stdout lines, skips non-protocol noise, applies the
/// inactivity watchdog, synthesizes an error on EOF-before-terminal, ends after done/error.</summary>
internal static class AssistantEventStream
{
    public static async IAsyncEnumerable<AssistantEvent> ReadUntilTerminalAsync(
        IAssistantProcess proc, TimeSpan inactivityTimeout, [EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            string? line = null;
            bool watchdogTripped = false;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(inactivityTimeout);
                try { line = await proc.ReadEventLineAsync(timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { watchdogTripped = true; }
            }
            if (watchdogTripped)
            {
                proc.Kill();
                yield return new AssistantError(
                    $"assistant helper produced no output for {inactivityTimeout.TotalMinutes:0.#} min - killed (watchdog)");
                yield break;
            }
            if (line is null)
            {
                yield return new AssistantError("assistant helper exited before completing the job");
                yield break;
            }
            var evt = AssistantWire.ParseEventLine(line);
            if (evt is null) continue;   // native-lib stdout noise: skip, never fatal
            yield return evt;
            if (evt is AssistantDone or AssistantError) yield break;
        }
    }
}

/// <summary>Spawn-per-job runner (design 7.1: summarize jobs remain spawn-per-job). Cancel =
/// process kill; watchdog default 5 min (CPU prefill emits progress well inside that). Never
/// writes files - the App owns all persistence via AtomicFile.</summary>
public sealed class AssistantJobRunner(IAssistantProcessFactory factory, TimeSpan? inactivityTimeout = null)
    : IAssistantJobRunner
{
    private readonly TimeSpan _inactivityTimeout = inactivityTimeout ?? TimeSpan.FromMinutes(5);

    public async IAsyncEnumerable<AssistantEvent> RunAsync(AssistantRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using IAssistantProcess proc = await factory.StartAsync(ct);
        using var reg = ct.Register(proc.Kill);   // cancel = kill (design 7.1)
        await proc.WriteRequestLineAsync(AssistantWire.SerializeRequest(request), ct);
        await foreach (var evt in AssistantEventStream.ReadUntilTerminalAsync(proc, _inactivityTimeout, ct))
            yield return evt;
    }
}

/// <summary>Warm keep-alive sessions (design 7.1). StartAsync forces keepAlive:true on the
/// warmup, drains it to done (throwing AssistantException on error/EOF), and hands back the
/// live session. Torn down by DisposeAsync - the CALLER owns idle-timeout/scope-change/
/// staleness teardown policy (matter-qa branch).</summary>
public sealed class AssistantChatSessionFactory(IAssistantProcessFactory factory, TimeSpan? inactivityTimeout = null)
    : IAssistantChatSessionFactory
{
    private readonly TimeSpan _inactivityTimeout = inactivityTimeout ?? TimeSpan.FromMinutes(5);

    public async Task<IAssistantChatSession> StartAsync(AssistantRequest warmupRequest, CancellationToken ct)
    {
        var proc = await factory.StartAsync(ct);
        try
        {
            using var reg = ct.Register(proc.Kill);
            var warm = warmupRequest with { KeepAlive = true };
            await proc.WriteRequestLineAsync(AssistantWire.SerializeRequest(warm), ct);
            await foreach (var evt in AssistantEventStream.ReadUntilTerminalAsync(proc, _inactivityTimeout, ct))
            {
                if (evt is AssistantError err) throw new AssistantException(err.Message);
                if (evt is AssistantDone) return new ChatSession(proc, warm, _inactivityTimeout);
            }
            throw new AssistantException("assistant helper exited during chat warmup");
        }
        catch
        {
            await proc.DisposeAsync();
            throw;
        }
    }

    private sealed class ChatSession(IAssistantProcess proc, AssistantRequest warm, TimeSpan inactivityTimeout)
        : IAssistantChatSession
    {
        public async IAsyncEnumerable<AssistantEvent> AskAsync(string questionPayloadJson,
            [EnumeratorCancellation] CancellationToken ct)
        {
            using var reg = ct.Register(proc.Kill);
            var req = warm with { Op = "answer", PayloadJson = questionPayloadJson };
            await proc.WriteRequestLineAsync(AssistantWire.SerializeRequest(req), ct);
            await foreach (var evt in AssistantEventStream.ReadUntilTerminalAsync(proc, inactivityTimeout, ct))
                yield return evt;
        }

        public ValueTask DisposeAsync()
        {
            proc.Kill();   // teardown = process kill (design 7.1)
            return proc.DisposeAsync();
        }
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 6 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Assistant/AssistantJobRunner.cs tests/LocalScribe.Core.Tests/AssistantJobRunnerTests.cs
git commit -m "feat(core): AssistantJobRunner + warm chat-session factory over the process seams (watchdog, cancel=kill)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 7: App — `ProcessAssistantHelper` + protocol integration tests against a STUB helper process
**Files:**
- Create `src\LocalScribe.App\Services\ProcessAssistantHelper.cs`
- Create `tests\LocalScribe.App.Tests\ProcessAssistantHelperTests.cs`

**Interfaces:**
- Produces: `public sealed class ProcessAssistantHelper(string exePath, string? arguments = null) : IAssistantProcessFactory` — the production spawner, mirroring `ProcessDiarisationHelper` mechanics exactly (`RedirectStandardInput/Output = true`, `UseShellExecute = false`, `CreateNoWindow = true`, `Kill(entireProcessTree: true)` best-effort). The `arguments` seam exists so tests can point it at `powershell.exe -File <stub>.ps1` — production passes only the exe path. A humble object at the process boundary; the REAL protocol behavior is pinned here by cross-process integration tests against a scripted stub (design §8: "helper protocol round-trip with a stub process (echo harness)"), NOT against a real model.
- Consumes: `IAssistantProcess`/`IAssistantProcessFactory` (Task 6), `System.Diagnostics.Process`.

Steps:
- [ ] **Write the failing tests (stub fully encoded).** Create `tests\LocalScribe.App.Tests\ProcessAssistantHelperTests.cs`:
```csharp
using LocalScribe.App.Services;
using LocalScribe.Core.Assistant;

namespace LocalScribe.App.Tests;

/// <summary>REAL cross-process protocol tests (design 2026-07-18 section 8): a scripted
/// PowerShell stub speaks the exact JSON-lines contract, so framing, keep-alive, crash
/// surfacing, and cancel-kill are pinned without any model. Windows PowerShell 5 ships on
/// every supported box (SystemDirectory path), so no extra tooling is needed.</summary>
public sealed class ProcessAssistantHelperTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ls-stub-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // The stub: one JSON request per stdin line; scripted JSON-lines out; behavior keyed by
    // marker words inside the request's payload; a per-process counter proves keep-alive
    // requests land on the SAME process. ASCII only (project rule).
    private const string StubScript = """
        $out = [Console]::Out
        $n = 0
        while ($null -ne ($line = [Console]::In.ReadLine())) {
            $n = $n + 1
            if ($line.Contains('CRASH-NOW')) { exit 3 }
            if ($line.Contains('HANG-NOW')) { Start-Sleep -Seconds 300; exit 0 }
            $out.WriteLine('{"type":"progress","phase":"prefill","current":1,"total":2}')
            $out.WriteLine('this line is native noise and must be skipped')
            $out.WriteLine('{"type":"chunk","text":"req-' + $n + '"}')
            $out.WriteLine('{"type":"done","stats":{"backend":"cpu","promptTokens":10,"outputTokens":1}}')
            $out.Flush()
            if (-not $line.Contains('"keepAlive":true')) { exit 0 }
        }
        exit 0
        """;

    private ProcessAssistantHelper MakeStubFactory()
    {
        string stubPath = Path.Combine(_dir, "stub-assistant.ps1");
        File.WriteAllText(stubPath, StubScript);
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return new ProcessAssistantHelper(powershell,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{stubPath}\"");
    }

    private static AssistantRequest Req(string marker, bool keepAlive = false)
        => new("summarize", @"C:\models\q.gguf", 8192, "auto", keepAlive,
            AssistantWire.PromptPayload(marker, 600));

    [Fact]
    public async Task Round_trip_streams_progress_chunk_done_and_skips_noise()
    {
        var runner = new AssistantJobRunner(MakeStubFactory());
        var events = new List<AssistantEvent>();
        await foreach (var e in runner.RunAsync(Req("normal job"), CancellationToken.None))
            events.Add(e);

        Assert.Equal(new AssistantEvent[]
        {
            new AssistantProgress("prefill", 1, 2),
            new AssistantChunk("req-1"),
            new AssistantDone("cpu", 10, 1),
        }, events);
    }

    [Fact]
    public async Task Helper_crash_surfaces_as_a_visible_error_event()
    {
        // Design 7.7: helper crash -> visible error. The stub exits 3 with no terminal line.
        var runner = new AssistantJobRunner(MakeStubFactory());
        var events = new List<AssistantEvent>();
        await foreach (var e in runner.RunAsync(Req("CRASH-NOW"), CancellationToken.None))
            events.Add(e);
        var err = Assert.IsType<AssistantError>(Assert.Single(events));
        Assert.Contains("exited before completing", err.Message);
    }

    [Fact]
    public async Task Cancel_kills_the_stub_promptly()
    {
        var runner = new AssistantJobRunner(MakeStubFactory());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in runner.RunAsync(Req("HANG-NOW"), cts.Token)) { }
        });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
            "cancel must kill the hung helper, not wait out its 300 s sleep");
    }

    [Fact]
    public async Task Keep_alive_chat_reuses_one_stub_process_across_questions()
    {
        // The stub's per-process counter is the proof: warmup sees req-1, answers see req-2/req-3.
        var sessions = new AssistantChatSessionFactory(MakeStubFactory());
        await using var chat = await sessions.StartAsync(Req("warmup", keepAlive: true), CancellationToken.None);

        var a1 = new List<AssistantEvent>();
        await foreach (var e in chat.AskAsync(AssistantWire.PromptPayload("q1", 400), CancellationToken.None)) a1.Add(e);
        var a2 = new List<AssistantEvent>();
        await foreach (var e in chat.AskAsync(AssistantWire.PromptPayload("q2", 400), CancellationToken.None)) a2.Add(e);

        Assert.Contains(new AssistantChunk("req-2"), a1);   // same process as the warmup (req-1)
        Assert.Contains(new AssistantChunk("req-3"), a2);   // and still the same process
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ProcessAssistantHelperTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` — expected: `error CS0246: The type or namespace name 'ProcessAssistantHelper' could not be found`.
- [ ] **Write the implementation.** Create `src\LocalScribe.App\Services\ProcessAssistantHelper.cs`:
```csharp
// src/LocalScribe.App/Services/ProcessAssistantHelper.cs
using System.Diagnostics;
using System.IO;
using LocalScribe.Core.Assistant;

namespace LocalScribe.App.Services;

/// <summary>Production IAssistantProcessFactory (design 2026-07-18 section 7.1): spawns
/// LocalScribe.Assistant.exe out-of-process and exposes its stdin/stdout as line streams.
/// Mirrors ProcessDiarisationHelper mechanics exactly - redirected pipes, no shell, no
/// window, and Kill(entireProcessTree: true) because the helper's native llama.cpp runtime
/// may own worker threads/child processes a plain Kill() would orphan. Unlike the Diarizer
/// one-shot, stdin STAYS OPEN so keepAlive chat can send further requests (recorded
/// deviation 1). Humble object at the process boundary - the protocol behavior is pinned by
/// ProcessAssistantHelperTests against a scripted stub, and AssistantJobRunnerTests against
/// in-process fakes. The optional arguments seam exists for those stub tests; production
/// passes only the exe path (CompositionRoot).</summary>
public sealed class ProcessAssistantHelper(string exePath, string? arguments = null) : IAssistantProcessFactory
{
    public Task<IAssistantProcess> StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (arguments is not null) psi.Arguments = arguments;
        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start the assistant helper");
        return Task.FromResult<IAssistantProcess>(new Wrapper(proc));
    }

    private sealed class Wrapper(Process proc) : IAssistantProcess
    {
        public async Task WriteRequestLineAsync(string requestJson, CancellationToken ct)
        {
            await proc.StandardInput.WriteLineAsync(requestJson.AsMemory(), ct);
            await proc.StandardInput.FlushAsync(ct);
        }

        public async Task<string?> ReadEventLineAsync(CancellationToken ct)
            => await proc.StandardOutput.ReadLineAsync(ct);

        public void Kill()
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* best-effort: it may have exited between the check and the kill */ }
        }

        public ValueTask DisposeAsync()
        {
            Kill();
            proc.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 4 passed (each test spawns real `powershell.exe` stubs; allow a few seconds).
- [ ] **Commit.**
```
git add src/LocalScribe.App/Services/ProcessAssistantHelper.cs tests/LocalScribe.App.Tests/ProcessAssistantHelperTests.cs
git commit -m "feat(app): ProcessAssistantHelper spawner + cross-process protocol tests against a scripted stub

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 8: Core — `StoragePaths` assistant paths + `SummaryVersion` + append-only `SummaryStore`
**Files:**
- Modify `src\LocalScribe.Core\Storage\StoragePaths.cs` (insert two path helpers immediately after line 24 `public string SummaryMd(string id) => Path.Combine(SessionDir(id), "summary.md");` — NOTE that pre-existing `SummaryMd` is an unrelated legacy `.md` in the session ROOT; the new sidecar lives under `assistant\`).
- Create `src\LocalScribe.Core\Assistant\SummaryStore.cs`
- Create `tests\LocalScribe.Core.Tests\SummaryStoreTests.cs`

**Interfaces:**
- Produces (LOCKED):
  - `public sealed record SummaryVersion(string Id, DateTimeOffset CreatedAt, string SourceTranscriptVersion, AssistantModelRef Model, int PromptVersion, string ContentMarkdown, bool Stale);` — `SourceTranscriptVersion` stores the value of `SessionRecord.ActiveVersion` / `LoadedProjection.VersionId` (`"v1"` or a `TranscriptVersion.Id` like `"v2-base.en-2026-07-13"`); `Model` is Task 4's `AssistantModelRef` (file, sha256, the backend ACTUALLY used).
  - `public sealed class SummaryStore { Task<IReadOnlyList<SummaryVersion>> LoadAsync(string sessionId, CancellationToken ct); Task AppendAsync(string sessionId, SummaryVersion version, CancellationToken ct); Task MarkAllStaleAsync(string sessionId, CancellationToken ct); }` writing `assistant\summaries.json` in the session folder via AtomicFile + schema stamp. Concretely: `public sealed class SummaryStore(StoragePaths paths)` with `public const int Version = 1;`, persisting `SummariesFile { SchemaVersion, Versions }` through `JsonFile`/`SchemaGuard`/`LocalScribeJson` (the `SpeakersStore` pattern — `JsonFile.WriteAsync` routes through `AtomicFile.WriteAllTextAsync`, which also creates the `assistant\` directory, `AtomicFile.cs:11`). **Append-only:** `AppendAsync` never rewrites an existing version's content; `MarkAllStaleAsync` flips only the `Stale` flag (design §7.3: regenerate appends, nothing overwritten) and is a no-op write-wise when everything is already stale (the no-op-SessionContentChanged discipline).
  - New paths: `public string AssistantDir(string id)` (= `SessionDir(id)\assistant`), `public string SummariesJson(string id)` (= `AssistantDir(id)\summaries.json`).
- Consumes: `StoragePaths`, `JsonFile`, `SchemaGuard`, `AssistantModelRef` (Task 4).

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\SummaryStoreTests.cs`:
```csharp
using System.Text.Json.Nodes;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

public sealed class SummaryStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-sumstore-").FullName;
    private readonly StoragePaths _paths;
    private readonly SummaryStore _store;

    public SummaryStoreTests()
    {
        _paths = new StoragePaths(_root);
        _store = new SummaryStore(_paths);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static SummaryVersion V(string id, bool stale = false) => new(id,
        new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.Zero), "v1",
        new AssistantModelRef("q4b.gguf", new string('a', 64), "cuda"),
        AssistantPrompts.PromptVersion, "## Summary\nBody.", stale);

    [Fact]
    public void Assistant_paths_live_under_the_session_folder()
    {
        Assert.Equal(Path.Combine(_paths.SessionDir("s1"), "assistant"), _paths.AssistantDir("s1"));
        Assert.Equal(Path.Combine(_paths.AssistantDir("s1"), "summaries.json"), _paths.SummariesJson("s1"));
    }

    [Fact]
    public async Task Load_of_a_session_without_summaries_is_empty()
        => Assert.Empty(await _store.LoadAsync("s1", CancellationToken.None));

    [Fact]
    public async Task Append_is_append_only_and_round_trips_the_locked_shape()
    {
        await _store.AppendAsync("s1", V("s1-a"), CancellationToken.None);
        await _store.AppendAsync("s1", V("s2-b"), CancellationToken.None);

        var loaded = await _store.LoadAsync("s1", CancellationToken.None);
        Assert.Equal(new[] { "s1-a", "s2-b" }, loaded.Select(v => v.Id));   // order preserved
        Assert.Equal(V("s1-a"), loaded[0]);                                  // full record round-trip

        // The sidecar carries the schema stamp + camelCase design-7.3 shape on disk.
        var obj = JsonNode.Parse(File.ReadAllText(_paths.SummariesJson("s1")))!.AsObject();
        Assert.Equal(SummaryStore.Version, obj["schemaVersion"]!.GetValue<int>());
        var first = obj["versions"]!.AsArray()[0]!.AsObject();
        Assert.Equal("v1", first["sourceTranscriptVersion"]!.GetValue<string>());
        Assert.Equal("cuda", first["model"]!.AsObject()["backend"]!.GetValue<string>());
        Assert.Equal(1, first["promptVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task MarkAllStale_flips_flags_but_never_content_and_noops_when_all_stale()
    {
        await _store.AppendAsync("s1", V("s1-a"), CancellationToken.None);
        await _store.AppendAsync("s1", V("s2-b", stale: true), CancellationToken.None);

        await _store.MarkAllStaleAsync("s1", CancellationToken.None);
        var loaded = await _store.LoadAsync("s1", CancellationToken.None);
        Assert.All(loaded, v => Assert.True(v.Stale));
        Assert.Equal("## Summary\nBody.", loaded[0].ContentMarkdown);   // content untouched

        // No-op discipline: already-all-stale must not rewrite the file (mtime unchanged).
        var before = File.GetLastWriteTimeUtc(_paths.SummariesJson("s1"));
        await Task.Delay(30);
        await _store.MarkAllStaleAsync("s1", CancellationToken.None);
        Assert.Equal(before, File.GetLastWriteTimeUtc(_paths.SummariesJson("s1")));
        // And a session with no summaries file at all is a clean no-op, not a crash or a write.
        await _store.MarkAllStaleAsync("never-summarized", CancellationToken.None);
        Assert.False(File.Exists(_paths.SummariesJson("never-summarized")));
    }

    [Fact]
    public async Task Newer_schema_is_rejected_not_mangled()
    {
        Directory.CreateDirectory(_paths.AssistantDir("s1"));
        File.WriteAllText(_paths.SummariesJson("s1"), "{\"schemaVersion\": 99, \"versions\": []}");
        await Assert.ThrowsAsync<NotSupportedException>(() => _store.LoadAsync("s1", CancellationToken.None));
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SummaryStoreTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` — expected: `error CS1061: 'StoragePaths' does not contain a definition for 'AssistantDir'` (plus CS0246 on `SummaryStore`/`SummaryVersion`).
- [ ] **Add the paths.** In `src\LocalScribe.Core\Storage\StoragePaths.cs`, immediately after line 24 (`public string SummaryMd(string id) => Path.Combine(SessionDir(id), "summary.md");`) insert:
```csharp

    /// <summary>Assistant work-product sidecars (design 2026-07-18 section 7.3): DERIVED
    /// artifacts stored separately from the transcript, never touching transcript files.
    /// Rides into zip archives automatically (SessionArchiver walks AllDirectories).</summary>
    public string AssistantDir(string id) => Path.Combine(SessionDir(id), "assistant");
    public string SummariesJson(string id) => Path.Combine(AssistantDir(id), "summaries.json");
```
- [ ] **Write the store.** Create `src\LocalScribe.Core\Assistant\SummaryStore.cs`:
```csharp
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Assistant;

/// <summary>One generated summary (design 2026-07-18 section 7.3 sidecar shape, LOCKED
/// contract - feat/matter-qa reads these for matter-scope context). SourceTranscriptVersion
/// stores SessionRecord.ActiveVersion / LoadedProjection.VersionId at generation time
/// ("v1" or a TranscriptVersion.Id). Model records the file + pinned sha256 + the backend
/// ACTUALLY used (from AssistantDone - floor-fall provenance).</summary>
public sealed record SummaryVersion(string Id, DateTimeOffset CreatedAt, string SourceTranscriptVersion,
    AssistantModelRef Model, int PromptVersion, string ContentMarkdown, bool Stale);

public sealed record SummariesFile
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<SummaryVersion> Versions { get; init; } = [];
}

/// <summary>assistant\summaries.json per session: versioned, APPEND-ONLY (design 7.3 -
/// regenerate appends a new version, nothing is overwritten; MarkAllStale flips only the
/// stale flag). SpeakersStore pattern: JsonFile + LocalScribeJson + SchemaGuard, writes
/// atomic via AtomicFile (which also creates the assistant\ folder). The HELPER never
/// writes files - only this App/Core-side store persists assistant artifacts.</summary>
public sealed class SummaryStore(StoragePaths paths)
{
    public const int Version = 1;

    public async Task<IReadOnlyList<SummaryVersion>> LoadAsync(string sessionId, CancellationToken ct)
    {
        string path = paths.SummariesJson(sessionId);
        var obj = await SchemaGuard.ReadObjectAsync(path, ct);
        if (obj is null) return [];
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "summaries.json");
        var file = await JsonFile.ReadAsync<SummariesFile>(path, ct);
        return file?.Versions ?? [];
    }

    public async Task AppendAsync(string sessionId, SummaryVersion version, CancellationToken ct)
    {
        var existing = await LoadAsync(sessionId, ct);
        await SaveAsync(sessionId, [.. existing, version], ct);
    }

    /// <summary>Transcript changed (SessionContentChanged / finalize / re-transcription):
    /// every version goes stale; regeneration stays an explicit user CTA, never automatic
    /// (design 7.3). No-op (no write) when there is nothing to flip.</summary>
    public async Task MarkAllStaleAsync(string sessionId, CancellationToken ct)
    {
        var existing = await LoadAsync(sessionId, ct);
        if (existing.Count == 0 || existing.All(v => v.Stale)) return;
        await SaveAsync(sessionId, existing.Select(v => v with { Stale = true }).ToList(), ct);
    }

    private Task SaveAsync(string sessionId, IReadOnlyList<SummaryVersion> versions, CancellationToken ct)
        => JsonFile.WriteAsync(paths.SummariesJson(sessionId),
            new SummariesFile { SchemaVersion = Version, Versions = versions }, ct);
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 5 passed. Then prove no storage regression: `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~StoragePaths" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` (any existing StoragePaths coverage must stay green — the edit is purely additive).
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Storage/StoragePaths.cs src/LocalScribe.Core/Assistant/SummaryStore.cs tests/LocalScribe.Core.Tests/SummaryStoreTests.cs
git commit -m "feat(core): append-only SummaryStore (assistant/summaries.json, schema-stamped, atomic) + assistant paths

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 9: Core — `AssistantGate` (blocked-while-recording, one assistant job at a time, visibly queued)
**Files:**
- Create `src\LocalScribe.Core\Assistant\AssistantGate.cs`
- Create `tests\LocalScribe.Core.Tests\AssistantGateTests.cs`

**Interfaces:**
- Produces (LOCKED contract member + queued-entry extension): `public sealed class AssistantGate { public bool TryEnter(out IDisposable lease); ... }` — full surface: `public sealed class AssistantGate(Func<string?> recordingBusy, int pollMs = 1000)` with `TryEnter(out IDisposable lease) : bool` (immediate, false while recording is busy OR another assistant job holds the lease), `Task<IDisposable> EnterAsync(Action<string>? onWaiting, CancellationToken ct)` (the QUEUED path: reports the busy reason — the visible "waiting for recording to finish" state, design §7.1/§7.7 — then polls until the recording is idle AND the single-job lease frees), and `string? BusyReason => recordingBusy()`.
- Wiring decision (named per the task brief): this is a NEW gate probing the SAME recording-busy condition `CompositionRoot.cs:92-95` gives `RetranscriptionRunner` (`controller.State != SessionState.Idle` / `!controller.PendingFinalize.IsCompleted`), NOT a chain into `SessionController.ExternalEngineBusy` — that seam BLOCKS RECORDING START, and the design's rule is one-directional: assistant jobs yield to recording, recording is never gated by the assistant. (The helper is a separate process with its own runtime, so the whisper one-engine invariant is not violated; the gate exists for CPU/VRAM contention and the locked §7.1 rule.)
- Consumes: nothing beyond BCL (the `Func<string?>` seam is wired in Task 13's CompositionRoot edit).

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\AssistantGateTests.cs`:
```csharp
using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

public class AssistantGateTests
{
    [Fact]
    public void TryEnter_fails_while_recording_and_succeeds_when_idle()
    {
        string? busy = "Waiting for the recording to finish...";
        var gate = new AssistantGate(() => busy);
        Assert.False(gate.TryEnter(out _));
        Assert.Equal(busy, gate.BusyReason);

        busy = null;
        Assert.True(gate.TryEnter(out var lease));
        lease.Dispose();
    }

    [Fact]
    public void One_assistant_job_at_a_time()
    {
        var gate = new AssistantGate(() => null);
        Assert.True(gate.TryEnter(out var first));
        Assert.False(gate.TryEnter(out _));      // second concurrent job refused
        first.Dispose();
        Assert.True(gate.TryEnter(out var second));
        second.Dispose();
    }

    [Fact]
    public async Task EnterAsync_queues_visibly_until_recording_finishes()
    {
        // Design 7.1/7.7: job requested mid-recording -> visibly queued, runs when idle.
        string? busy = "Waiting for the recording to finish...";
        var waits = new List<string>();
        var gate = new AssistantGate(() => busy, pollMs: 10);

        var entering = gate.EnterAsync(waits.Add, CancellationToken.None);
        await Task.Delay(80);
        Assert.False(entering.IsCompleted);      // still queued while "recording"
        Assert.NotEmpty(waits);                  // and VISIBLY so

        busy = null;                             // recording stops
        using var lease = await entering.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Waiting for the recording to finish...", waits[0]);
    }

    [Fact]
    public async Task EnterAsync_cancellation_releases_cleanly()
    {
        var gate = new AssistantGate(() => "busy forever", pollMs: 10);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync(null, cts.Token));
        Assert.False(gate.TryEnter(out _));      // recording still "busy" - refused for THAT reason
        var idle = new AssistantGate(() => null);
        Assert.True(idle.TryEnter(out var lease));   // and a fresh idle gate is enterable
        lease.Dispose();
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~AssistantGateTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` — expected: `error CS0246: The type or namespace name 'AssistantGate' could not be found`.
- [ ] **Write the implementation.** Create `src\LocalScribe.Core\Assistant\AssistantGate.cs`:
```csharp
namespace LocalScribe.Core.Assistant;

/// <summary>Locked rule (design 2026-07-18 section 7.1): assistant jobs are BLOCKED while a
/// recording session is active - queued with a VISIBLE waiting state - and only one assistant
/// job runs at a time. One-directional by design: recording is never gated by the assistant
/// (this deliberately does NOT chain into SessionController.ExternalEngineBusy, which blocks
/// recording start). recordingBusy is the same condition CompositionRoot gives
/// RetranscriptionRunner: non-null reason while State != Idle or a finalize is pending.</summary>
public sealed class AssistantGate(Func<string?> recordingBusy, int pollMs = 1000)
{
    private readonly SemaphoreSlim _jobLease = new(1, 1);

    /// <summary>The current block reason, or null when assistant jobs may run.</summary>
    public string? BusyReason => recordingBusy();

    /// <summary>Immediate entry: false while recording is busy OR another assistant job
    /// holds the lease. Dispose the lease to release.</summary>
    public bool TryEnter(out IDisposable lease)
    {
        lease = NullLease.Instance;
        if (recordingBusy() is not null) return false;
        if (!_jobLease.Wait(0)) return false;
        if (recordingBusy() is not null) { _jobLease.Release(); return false; }   // raced a Start
        lease = new Lease(_jobLease);
        return true;
    }

    /// <summary>Queued entry (design 7.7: "job requested mid-recording -> visibly queued"):
    /// reports the busy reason via onWaiting, then polls until recording is idle and the
    /// single-job lease frees. Cancellation releases cleanly.</summary>
    public async Task<IDisposable> EnterAsync(Action<string>? onWaiting, CancellationToken ct)
    {
        bool reported = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (TryEnter(out var lease)) return lease;
            if (!reported && BusyReason is string reason) { onWaiting?.Invoke(reason); reported = true; }
            await Task.Delay(pollMs, ct);
        }
    }

    private sealed class Lease(SemaphoreSlim sem) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) sem.Release();
        }
    }

    private sealed class NullLease : IDisposable
    {
        public static readonly NullLease Instance = new();
        public void Dispose() { }
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 4 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Assistant/AssistantGate.cs tests/LocalScribe.Core.Tests/AssistantGateTests.cs
git commit -m "feat(core): AssistantGate - assistant jobs yield to recording, one at a time, visibly queued

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 10: Core — `SummarizationService` (orchestration: gate → projection → shaping → fits-check → single/map-reduce → persist) + additive `AssistantSetting`
**Files:**
- Modify `src\LocalScribe.Core\Model\Settings.cs` (one additive property inside the `Settings` record after line 38 `public PrivacySetting Privacy { get; init; } = new();`, and one new sub-record after line 49's `PrivacySetting` record — the `SectionGapMs`/`DocxFooterText` additive precedent: default value, NO schema bump, NO migration).
- Create `src\LocalScribe.Core\Assistant\SummarizationService.cs`
- Create `tests\LocalScribe.Core.Tests\SummarizationServiceTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record AssistantSetting { public bool Enabled { get; init; } = true; public string? Model { get; init; } }` + `public AssistantSetting Assistant { get; init; } = new();` on `Settings` (additive; `Model` = canonical name pick, null = the locked default).
  - `public sealed class SummarizationService(StoragePaths paths, Func<Settings> settings, TimeProvider time, IAssistantJobRunner runner, SummaryStore store, AssistantGate gate, AssistantManifestCache models, Func<string, CancellationToken, Task<LoadedProjection>>? loadProjection = null)` — the seam defaults to `SessionProjectionLoader.LoadAsync(paths, settings(), time, sessionId, ct)` (the ACTIVE version, corrections applied — the same shared load pipeline docx/search use); tests inject an in-memory projection (repo seam style).
  - `public Task<SummaryVersion> SummarizeAsync(string sessionId, Action<AssistantEvent>? onEvent, Action<string>? onWaiting, CancellationToken ct)` — flow: gate `EnterAsync` (visibly queued via `onWaiting`) → manifest model pick (`Settings.Assistant.Model` by canonical name, else `DefaultModel`, else `AssistantException` "no model") → projection → roster preamble + transcript text (Task 2) → fits-check at the 80% gate against the 32k operating budget → single call (`JobCtxTokens`-sized ctx) or map-reduce (`MapCtxTokens = 16384` map jobs capped at `MapOutputCapTokens`; hierarchical reduce max depth 2, then honest `AssistantException` "session too long for the configured model") → empty output ⇒ `AssistantException`, **nothing persisted** (§7.4) → `SummaryStore.AppendAsync` of a `SummaryVersion` with `Id = "s{n}"`, `SourceTranscriptVersion = loaded.VersionId`, `Model = AssistantModelRef(fileName, sha256, done.Backend)` (the backend ACTUALLY used — §7.7 provenance), `PromptVersion`, `Stale = false`.
  - `public static IReadOnlyList<string> SplitIntoChunks(string text, int chunkBudgetChars)` — line-boundary splitter with a hard split for oversize single lines (public: tests drive it; no InternalsVisibleTo).
  - `public const int MapCtxTokens = 16384;`
- Consumes: Tasks 2/3/4/6/8/9, `SessionProjectionLoader`/`LoadedProjection` (`SessionProjectionLoader.cs:12-37`), `DisplayRow`.

Steps:
- [ ] **Add the setting.** In `src\LocalScribe.Core\Model\Settings.cs`, immediately after line 38 (`public PrivacySetting Privacy { get; init; } = new();`, still inside the `Settings` record) insert:
```csharp
    /// <summary>v3 (Steno round, design 2026-07-18 section 7): local assistant. Additive -
    /// existing v3 files without it load at this default, so no schema bump / migration is
    /// required (the SectionGapMs / DocxFooterText precedent).</summary>
    public AssistantSetting Assistant { get; init; } = new();
```
and after the `PrivacySetting` record (line 49, `public sealed record PrivacySetting { public bool ExcludeWindowsFromCapture { get; init; } = true; }`) insert:
```csharp
/// <summary>Model is a manifest canonical name; null = the locked default
/// (Qwen3-4B-Instruct-2507). Enabled=false hides/disables all assistant UI.</summary>
public sealed record AssistantSetting { public bool Enabled { get; init; } = true; public string? Model { get; init; } }
```
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\SummarizationServiceTests.cs`:
```csharp
using System.Runtime.CompilerServices;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

public sealed class SummarizationServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-sumsvc-").FullName;
    private readonly StoragePaths _paths;
    private readonly SummaryStore _store;
    private Settings _settings = new();

    public SummarizationServiceTests() { _paths = new StoragePaths(_root); _store = new SummaryStore(_paths); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class FakeRunner(Func<AssistantRequest, IEnumerable<AssistantEvent>> script) : IAssistantJobRunner
    {
        public List<AssistantRequest> Requests { get; } = [];
        public async IAsyncEnumerable<AssistantEvent> RunAsync(AssistantRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add(request);
            foreach (var e in script(request)) { await Task.Yield(); yield return e; }
        }
    }

    private static readonly AssistantModelInfo Qwen4B =
        new("Qwen3-4B-Instruct-2507", @"C:\models\q4b.gguf", new string('a', 64), 262144, "Apache-2.0");
    private static readonly AssistantModelInfo Qwen17 =
        new("Qwen3-1.7B-Instruct", @"C:\models\q17.gguf", new string('b', 64), 32768, "Apache-2.0");

    private static AssistantManifestCache Cache(params AssistantModelInfo[] models)
        => new(_ => Task.FromResult(new AssistantModelManifest(models,
            models.FirstOrDefault(m => m.CanonicalName == AssistantModelManifest.DefaultCanonicalName)
                ?? models.FirstOrDefault(), [])));

    private static LoadedProjection Projection(IReadOnlyList<DisplayRow> rows, string versionId = "v1")
    {
        var started = new DateTimeOffset(2026, 7, 19, 9, 0, 0, TimeSpan.Zero);
        return new LoadedProjection(
            new SessionRecord(), SessionMeta.CreateDefault("Webex", started, self: null),
            [], null, null, new Dictionary<string, Matter>(), [], started, rows,
            new TranscriptHeader("t", "Webex", started, 0, "base.en", "CPU"),
            new SessionTextView("t", [], [], started, null, 0, "call", "", null),
            versionId);
    }

    private SummarizationService Make(FakeRunner runner, IReadOnlyList<DisplayRow> rows,
        AssistantGate? gate = null, AssistantManifestCache? cache = null, string versionId = "v1")
        => new(_paths, () => _settings, TimeProvider.System, runner, _store,
            gate ?? new AssistantGate(() => null, pollMs: 10), cache ?? Cache(Qwen4B, Qwen17),
            loadProjection: (_, _) => Task.FromResult(Projection(rows, versionId)));

    private static IReadOnlyList<DisplayRow> SmallRows() =>
    [
        new DisplayRow { DisplayName = "Sam", Text = "We agreed to file Tuesday.", StartMs = 0, EndMs = 2000 },
        new DisplayRow { DisplayName = "Client", Text = "Yes.", StartMs = 2500, EndMs = 2900 },
    ];

    private static IEnumerable<AssistantEvent> GoodScript(string text, string backend = "cuda")
        => [new AssistantChunk(text), new AssistantDone(backend, 100, 20)];

    [Fact]
    public async Task Single_call_summary_persists_with_full_provenance()
    {
        var runner = new FakeRunner(_ => GoodScript("## Summary\nFiled Tuesday."));
        var seen = new List<AssistantEvent>();
        var v = await Make(runner, SmallRows(), versionId: "v2-base.en-2026-07-13")
            .SummarizeAsync("s1", seen.Add, null, CancellationToken.None);

        var req = Assert.Single(runner.Requests);                       // fits -> ONE call
        Assert.Equal("summarize", req.Op);
        Assert.Equal(@"C:\models\q4b.gguf", req.ModelPath);             // locked default picked
        Assert.Equal(TokenBudget.MinCtxTokens, req.CtxTokens);          // tiny job -> ctx floor
        Assert.Contains("Speakers in this call: Sam, Client.", req.PayloadJson);   // roster rode in
        Assert.Contains(AssistantPrompts.GroundingLine, req.PayloadJson);

        Assert.Equal("s1", v.Id);
        Assert.Equal("v2-base.en-2026-07-13", v.SourceTranscriptVersion);   // ACTIVE version recorded
        Assert.Equal(new AssistantModelRef("q4b.gguf", new string('a', 64), "cuda"), v.Model);
        Assert.Equal(AssistantPrompts.PromptVersion, v.PromptVersion);
        Assert.False(v.Stale);
        Assert.Equal("## Summary\nFiled Tuesday.", v.ContentMarkdown);
        Assert.Contains(seen, e => e is AssistantChunk);                // streamed to the caller
        Assert.Single(await _store.LoadAsync("s1", CancellationToken.None));
    }

    [Fact]
    public async Task Explicit_settings_model_pick_is_honored()
    {
        _settings = new Settings { Assistant = new AssistantSetting { Model = "Qwen3-1.7B-Instruct" } };
        var runner = new FakeRunner(_ => GoodScript("## Summary\nok.", backend: "cpu"));
        var v = await Make(runner, SmallRows()).SummarizeAsync("s1", null, null, CancellationToken.None);
        Assert.Equal(@"C:\models\q17.gguf", runner.Requests[0].ModelPath);
        Assert.Equal("cpu", v.Model.Backend);                           // ACTUAL backend from done
    }

    [Fact]
    public async Task Empty_output_and_error_events_throw_and_persist_nothing()
    {
        // Design 7.4: empty model output -> error surfaced, nothing persisted (never a blank artifact).
        var empty = new FakeRunner(_ => GoodScript("   \n"));
        await Assert.ThrowsAsync<AssistantException>(() =>
            Make(empty, SmallRows()).SummarizeAsync("s1", null, null, CancellationToken.None));

        var err = new FakeRunner(_ => [new AssistantError("JOB_FAILED: oom")]);
        var ex = await Assert.ThrowsAsync<AssistantException>(() =>
            Make(err, SmallRows()).SummarizeAsync("s1", null, null, CancellationToken.None));
        Assert.Contains("oom", ex.Message);
        Assert.Empty(await _store.LoadAsync("s1", CancellationToken.None));
    }

    [Fact]
    public async Task No_installed_model_is_an_honest_error()
    {
        var runner = new FakeRunner(_ => GoodScript("x"));
        var ex = await Assert.ThrowsAsync<AssistantException>(() =>
            Make(runner, SmallRows(), cache: Cache())
                .SummarizeAsync("s1", null, null, CancellationToken.None));
        Assert.Contains("model", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task Long_sessions_map_reduce_and_persist_the_merged_result()
    {
        // ~120k chars of turns >> 80% of the 32k operating budget -> map-reduce (design 7.4).
        var rows = Enumerable.Range(0, 400).Select(i => new DisplayRow
        { DisplayName = "Sam", Text = new string('x', 300), StartMs = i * 1000, EndMs = i * 1000 + 900 })
            .ToList();
        int expectedChunks = SummarizationService.SplitIntoChunks(
            AssistantInputShaper.BuildTranscriptText(rows),
            TokenBudget.ChunkBudgetChars(SummarizationService.MapCtxTokens)).Count;
        Assert.True(expectedChunks > 1);

        var runner = new FakeRunner(req => GoodScript(
            req.PayloadJson.Contains("You are reading part") ? "- note" : "## Summary\nmerged."));
        var seen = new List<AssistantEvent>();
        var v = await Make(runner, rows).SummarizeAsync("s1", seen.Add, null, CancellationToken.None);

        Assert.Equal(expectedChunks + 1, runner.Requests.Count);        // N maps + 1 reduce
        Assert.All(runner.Requests.Take(expectedChunks),
            r => Assert.Equal(SummarizationService.MapCtxTokens, r.CtxTokens));
        Assert.Equal("## Summary\nmerged.", v.ContentMarkdown);
        Assert.Contains(seen, e => e is AssistantProgress { Phase: "map" });
        Assert.Contains(seen, e => e is AssistantProgress { Phase: "reduce" });
    }

    [Fact]
    public async Task Queued_while_recording_then_runs_when_idle()
    {
        // Design 7.1/7.7: mid-recording -> visibly queued, never refused, never auto-cancelled.
        string? busy = "Waiting for the recording to finish...";
        var gate = new AssistantGate(() => busy, pollMs: 10);
        var runner = new FakeRunner(_ => GoodScript("## Summary\nok."));
        var waits = new List<string>();

        var job = Make(runner, SmallRows(), gate: gate)
            .SummarizeAsync("s1", null, waits.Add, CancellationToken.None);
        await Task.Delay(80);
        Assert.False(job.IsCompleted);
        busy = null;
        var v = await job.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Waiting for the recording to finish...", Assert.Single(waits.Distinct()));
        Assert.False(v.Stale);
    }

    [Fact]
    public void SplitIntoChunks_respects_line_boundaries_and_hard_splits_oversize_lines()
    {
        var chunks = SummarizationService.SplitIntoChunks("aa\nbb\ncc\ndd", 6);
        Assert.Equal(new[] { "aa\nbb", "cc\ndd" }, chunks);
        var hard = SummarizationService.SplitIntoChunks(new string('z', 10), 4);
        Assert.Equal(new[] { "zzzz", "zzzz", "zz" }, hard);
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SummarizationServiceTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` — expected: `error CS0246: The type or namespace name 'SummarizationService' could not be found`. (If `SessionMeta.CreateDefault`'s parameter list differs from `(string app, DateTimeOffset startedLocal, SelfIdentity? self)`, adjust the ONE call in `Projection(...)` to the real signature — `MaintenanceService.cs:76` shows the canonical call.)
- [ ] **Write the implementation.** Create `src\LocalScribe.Core\Assistant\SummarizationService.cs`:
```csharp
using System.Text;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Assistant;

/// <summary>Per-session summarization orchestration (design 2026-07-18 section 7.4).
/// Gate (visibly queued behind recording) -> projection (ACTIVE version, corrections applied
/// - the same SessionProjectionLoader pipeline docx/search use) -> input shaping (roster
/// kept, timestamps stripped) -> fits-check at 80% of the 32k operating budget with
/// worst-case 2 chars/token -> single call or map-reduce (map output capped, hierarchical
/// reduce max depth 2, then an HONEST too-long error) -> append-only persistence with full
/// provenance (model file + sha256 + the backend ACTUALLY used). Empty output persists
/// NOTHING. The helper never writes files; this service owns persistence via SummaryStore.</summary>
public sealed class SummarizationService(
    StoragePaths paths,
    Func<Settings> settings,
    TimeProvider time,
    IAssistantJobRunner runner,
    SummaryStore store,
    AssistantGate gate,
    AssistantManifestCache models,
    Func<string, CancellationToken, Task<LoadedProjection>>? loadProjection = null)
{
    /// <summary>Map jobs run at a fixed mid-size ctx: big enough for meaty chunks, small
    /// enough to stay GPU-resident per the design 7.2 KV sizing math.</summary>
    public const int MapCtxTokens = 16384;

    private readonly Func<string, CancellationToken, Task<LoadedProjection>> _loadProjection =
        loadProjection ?? ((sessionId, ct)
            => SessionProjectionLoader.LoadAsync(paths, settings(), time, sessionId, ct));

    public async Task<SummaryVersion> SummarizeAsync(string sessionId,
        Action<AssistantEvent>? onEvent, Action<string>? onWaiting, CancellationToken ct)
    {
        using var lease = await gate.EnterAsync(onWaiting, ct);

        var manifest = await models.GetAsync(ct);
        var pick = settings().Assistant.Model;
        var model = (pick is not null ? manifest.Installed.FirstOrDefault(m => m.CanonicalName == pick) : null)
            ?? manifest.DefaultModel
            ?? throw new AssistantException(
                "No assistant model is installed - see Settings > Assistant for fetch instructions.");

        var loaded = await _loadProjection(sessionId, ct);
        var roster = loaded.Rows.Where(r => !r.IsMarker && r.DisplayName is not null)
            .Select(r => r.DisplayName!).Distinct().ToList();
        string preamble = AssistantInputShaper.BuildSpeakerPreamble(roster);
        string transcript = AssistantInputShaper.BuildTranscriptText(loaded.Rows);
        if (transcript.Length == 0)
            throw new AssistantException("This session has no transcript content to summarize.");

        string singlePrompt = AssistantPrompts.BuildSummaryPrompt(preamble, transcript);
        int est = TokenBudget.EstimateTokens(singlePrompt.Length);
        (string content, AssistantDone done) =
            !TokenBudget.NeedsChunking(est + TokenBudget.OutputReserveTokens, TokenBudget.MaxCtxTokens)
                ? await RunJobAsync(model, singlePrompt, TokenBudget.JobCtxTokens(est),
                    TokenBudget.OutputReserveTokens, onEvent, ct)
                : await MapReduceAsync(model, preamble, transcript, onEvent, ct);

        if (string.IsNullOrWhiteSpace(content))
            throw new AssistantException("The model returned no content - nothing was saved.");

        var existing = await store.LoadAsync(sessionId, ct);
        var version = new SummaryVersion(
            Id: $"s{existing.Count + 1}",
            CreatedAt: time.GetUtcNow(),
            SourceTranscriptVersion: loaded.VersionId,
            Model: new AssistantModelRef(Path.GetFileName(model.FilePath), model.Sha256, done.Backend),
            PromptVersion: AssistantPrompts.PromptVersion,
            ContentMarkdown: content.Trim(),
            Stale: false);
        await store.AppendAsync(sessionId, version, ct);
        return version;
    }

    private async Task<(string, AssistantDone)> MapReduceAsync(AssistantModelInfo model,
        string preamble, string transcript, Action<AssistantEvent>? onEvent, CancellationToken ct)
    {
        var chunks = SplitIntoChunks(transcript, TokenBudget.ChunkBudgetChars(MapCtxTokens));
        var outputs = new List<string>();
        for (int i = 0; i < chunks.Count; i++)
        {
            onEvent?.Invoke(new AssistantProgress("map", i + 1, chunks.Count));
            var (text, _) = await RunJobAsync(model,
                AssistantPrompts.BuildMapPrompt(preamble, chunks[i], i + 1, chunks.Count),
                MapCtxTokens, TokenBudget.MapOutputCapTokens, onEvent, ct);
            outputs.Add(text);
        }

        // Hierarchical reduce, max depth 2 (design 7.4), then an honest too-long error.
        for (int depth = 1; depth <= TokenBudget.MaxReduceDepth; depth++)
        {
            string reducePrompt = AssistantPrompts.BuildReducePrompt(preamble, outputs);
            int est = TokenBudget.EstimateTokens(reducePrompt.Length);
            if (!TokenBudget.NeedsChunking(est + TokenBudget.OutputReserveTokens, TokenBudget.MaxCtxTokens))
            {
                onEvent?.Invoke(new AssistantProgress("reduce", depth, depth));
                return await RunJobAsync(model, reducePrompt, TokenBudget.JobCtxTokens(est),
                    TokenBudget.OutputReserveTokens, onEvent, ct);
            }
            // Batch the notes into groups that fit, reduce each, then loop one level deeper.
            var batches = new List<List<string>>();
            var current = new List<string>();
            int chars = 0;
            int budget = TokenBudget.ChunkBudgetChars(TokenBudget.MaxCtxTokens);
            foreach (var o in outputs)
            {
                if (current.Count > 0 && chars + o.Length > budget)
                { batches.Add(current); current = []; chars = 0; }
                current.Add(o);
                chars += o.Length;
            }
            if (current.Count > 0) batches.Add(current);
            if (batches.Count <= 1) break;   // cannot shrink further - fall through to the error
            var next = new List<string>();
            for (int b = 0; b < batches.Count; b++)
            {
                onEvent?.Invoke(new AssistantProgress("reduce", b + 1, batches.Count));
                var (text, _) = await RunJobAsync(model,
                    AssistantPrompts.BuildReducePrompt(preamble, batches[b]),
                    TokenBudget.MaxCtxTokens, TokenBudget.MapOutputCapTokens, onEvent, ct);
                next.Add(text);
            }
            outputs = next;
        }
        throw new AssistantException(
            "This session is too long for the configured model - the summary cannot be generated.");
    }

    private async Task<(string Text, AssistantDone Done)> RunJobAsync(AssistantModelInfo model,
        string prompt, int ctxTokens, int maxOutputTokens, Action<AssistantEvent>? onEvent, CancellationToken ct)
    {
        var request = new AssistantRequest("summarize", model.FilePath, ctxTokens, "auto",
            KeepAlive: false, AssistantWire.PromptPayload(prompt, maxOutputTokens));
        var sb = new StringBuilder();
        AssistantDone? done = null;
        await foreach (var evt in runner.RunAsync(request, ct))
        {
            onEvent?.Invoke(evt);
            switch (evt)
            {
                case AssistantChunk c: sb.Append(c.Text); break;
                case AssistantDone d: done = d; break;
                case AssistantError e: throw new AssistantException(e.Message);
            }
        }
        return done is null
            ? throw new AssistantException("assistant helper ended without a result")
            : (sb.ToString(), done);
    }

    /// <summary>Line-boundary chunking; a single line larger than the budget is hard-split
    /// (never an over-budget chunk - the gate must hold). Public: tests drive it directly.</summary>
    public static IReadOnlyList<string> SplitIntoChunks(string text, int chunkBudgetChars)
    {
        var chunks = new List<string>();
        var sb = new StringBuilder();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            while (line.Length > chunkBudgetChars)
            {
                if (sb.Length > 0) { chunks.Add(sb.ToString()); sb.Clear(); }
                chunks.Add(line[..chunkBudgetChars]);
                line = line[chunkBudgetChars..];
            }
            if (sb.Length > 0 && sb.Length + line.Length + 1 > chunkBudgetChars)
            { chunks.Add(sb.ToString()); sb.Clear(); }
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }
}
```
- [ ] **Run tests and see PASS.** Same filter — expected: 7 passed. Then run the settings round-trip coverage to prove the additive field breaks nothing: `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~Settings" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\`.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Model/Settings.cs src/LocalScribe.Core/Assistant/SummarizationService.cs tests/LocalScribe.Core.Tests/SummarizationServiceTests.cs
git commit -m "feat(core): SummarizationService - gated, budgeted single/map-reduce summaries with provenance + AssistantSetting

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 11: Helper exe — `src\LocalScribe.Assistant\` (LLamaSharp host, request loop, backend pick, KV q8_0)
**Files:**
- Create `src\LocalScribe.Assistant\LocalScribe.Assistant.csproj`
- Create `src\LocalScribe.Assistant\Program.cs`
- Create `src\LocalScribe.Assistant\LlamaEngine.cs`
- Modify `LocalScribe.slnx` (one line, after line 5 `<Project Path="src/LocalScribe.Diarizer/LocalScribe.Diarizer.csproj" />`)

**Interfaces:**
- Produces: `LocalScribe.Assistant.exe` — the §7.1 runtime. Reads one JSON request PER STDIN LINE (`AssistantWire.ParseRequestLine` — Core is a ProjectReference, exactly like Diarizer references Core for its wire types), runs LLamaSharp inference streaming `chunk` events, emits `progress` during load/prefill, exactly one terminal `done` (with the backend ACTUALLY used + token stats) or `error` per request; `keepAlive:false` → exit 0 after done; `keepAlive:true` → loop for further `answer` requests reusing the loaded model + KV (the `InteractiveExecutor` retains conversation state — the warm-prefix mechanism). Backend pick: `"cuda"` → GPU or error; `"cpu"` → CPU; `"auto"` → try CUDA, fall to CPU (the fall is visible: the `done.stats.backend` says `"cpu"` — floor-fall provenance). KV cache q8_0 + FlashAttention; per-request `ContextSize = ctxTokens`; ChatML template with thinking disabled by model choice (Qwen3-*-Instruct-2507 is a non-thinking model — the whole token budget goes to the answer, design §7.2). **The helper never writes files and opens no sockets.**
- This is a **humble object at the native boundary** (the `SherpaDiarisationRunner` precedent): NOT unit-tested; the protocol is pinned by Tasks 6-7 (fakes + scripted stub), and the real model path is the Task 14 user smoke. The gate for this task is: clean build, zero warnings, App/Core untouched by its packages.
- Consumes: `LocalScribe.Core.Assistant.AssistantWire` (Task 1); NuGet `LLamaSharp` + `LLamaSharp.Backend.Cpu` + `LLamaSharp.Backend.Cuda12` @ 0.25.0 (Global Constraints deviation 3: if restore rejects the pin, re-pin all three to the newest stable found via `dotnet package search LLamaSharp --take 1`; if any LLamaSharp API member below has drifted in that version, fix mechanically — the stdout contract, not this file's internals, is what tests pin).

Steps:
- [ ] **Create the project.** `src\LocalScribe.Assistant\LocalScribe.Assistant.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <Platforms>x64;ARM64</Platforms>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\LocalScribe.Core\LocalScribe.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <!-- MIT. Owns its OWN llama.cpp native runtimes (CPU + CUDA 12) in THIS process only.
         The App must NEVER reference these packages or this project - same native-DLL
         isolation rationale as Diarizer's sherpa/ORT split (LocalScribe.App.csproj:24-52).
         Publish: single-file self-contained with IncludeNativeLibrariesForSelfExtract=true,
         then copy ONLY the .exe beside the App output (Stage 5 smoke runbook pattern).
         CUDA functions on win-x64 only; on ARM64 the CPU backend is the sole path. -->
    <PackageReference Include="LLamaSharp" Version="0.25.0" />
    <PackageReference Include="LLamaSharp.Backend.Cpu" Version="0.25.0" />
    <PackageReference Include="LLamaSharp.Backend.Cuda12" Version="0.25.0" />
  </ItemGroup>
</Project>
```
- [ ] **Add it to the solution.** In `LocalScribe.slnx`, after line 5 (`    <Project Path="src/LocalScribe.Diarizer/LocalScribe.Diarizer.csproj" />`) insert:
```xml
    <Project Path="src/LocalScribe.Assistant/LocalScribe.Assistant.csproj" />
```
- [ ] **Write the request loop.** Create `src\LocalScribe.Assistant\Program.cs`:
```csharp
// Out-of-process local-LLM helper (design 2026-07-18 section 7.1). Reads one JSON request
// PER STDIN LINE (unlike Diarizer's one-shot read-to-end: keepAlive chat sends further
// requests on the same stdin), streams JSON-lines events to stdout via the Core wire codec,
// and NEVER writes files or opens sockets. keepAlive:false exits after done; keepAlive:true
// stays resident, reusing the loaded model + KV prefix for follow-up "answer" requests.
// Exit 0 = clean; 1 = a job/protocol error was emitted before exiting.
using LocalScribe.Assistant;
using LocalScribe.Core.Assistant;

var stdout = Console.Out;
void Emit(AssistantEvent evt)
{
    stdout.WriteLine(AssistantWire.SerializeEvent(evt));
    stdout.Flush();   // the App reads line-by-line; never let events sit in the buffer
}

LlamaEngine? engine = null;
try
{
    string? line;
    while ((line = await Console.In.ReadLineAsync()) is not null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        var request = AssistantWire.ParseRequestLine(line);
        if (request is null)
        {
            Emit(new AssistantError("BAD_REQUEST: stdin line is not a valid assistant request"));
            return 1;
        }
        try
        {
            // First request loads the model (progress phases surface the slow parts);
            // keepAlive follow-ups reuse it - the warm-chat KV contract (design 7.1).
            engine ??= LlamaEngine.Load(request.ModelPath, request.CtxTokens, request.Backend,
                phase => Emit(new AssistantProgress(phase, 0, 0)));

            var (prompt, maxTokens) = LlamaEngine.ReadPayload(request.PayloadJson);
            Emit(new AssistantProgress("prefill", 0, 0));
            int outputTokens = 0;
            await foreach (string piece in engine.InferAsync(prompt, maxTokens))
            {
                Emit(new AssistantChunk(piece));
                outputTokens++;
            }
            Emit(new AssistantDone(engine.Backend, engine.LastPromptTokens, outputTokens));
        }
        catch (Exception ex)
        {
            Emit(new AssistantError("JOB_FAILED: " + ex.Message));
            return 1;
        }
        if (!request.KeepAlive) return 0;
    }
    return 0;
}
catch (Exception ex)
{
    Emit(new AssistantError("HELPER_CRASH: " + ex.Message));
    return 1;
}
finally
{
    engine?.Dispose();
}
```
- [ ] **Write the inference wrapper.** Create `src\LocalScribe.Assistant\LlamaEngine.cs`:
```csharp
// Humble object at the LLamaSharp/native boundary (the SherpaDiarisationRunner precedent):
// not unit-tested; the stdio contract around it is pinned by AssistantJobRunnerTests and
// ProcessAssistantHelperTests, and the real-model path is smoke-only.
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using LLama;
using LLama.Common;
using LLama.Native;

namespace LocalScribe.Assistant;

internal sealed class LlamaEngine : IDisposable
{
    /// <summary>The backend ACTUALLY used ("cuda" or "cpu") - reported in every done event
    /// (floor-fall provenance, design 7.7: CUDA fall to CPU is recorded, never silent).</summary>
    public string Backend { get; }
    public int LastPromptTokens { get; private set; }

    private readonly LLamaWeights _weights;
    private readonly LLamaContext _context;
    private readonly InteractiveExecutor _executor;

    /// <summary>Backend pick (design 7.1): "cuda" -> GPU or throw; "cpu" -> CPU;
    /// "auto" -> try CUDA (all layers offloaded), fall to CPU on ANY load failure.</summary>
    public static LlamaEngine Load(string modelPath, int ctxTokens, string backendRequest, Action<string> phase)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"model file missing: {modelPath}", modelPath);
        if (backendRequest != "cpu")
        {
            try
            {
                phase("load-cuda");
                return new LlamaEngine(modelPath, ctxTokens, gpuLayerCount: int.MaxValue, "cuda");
            }
            catch (Exception) when (backendRequest == "auto")
            {
                // fall through: CPU always works; the done event will honestly say "cpu"
            }
        }
        phase("load-cpu");
        return new LlamaEngine(modelPath, ctxTokens, gpuLayerCount: 0, "cpu");
    }

    private LlamaEngine(string modelPath, int ctxTokens, int gpuLayerCount, string backend)
    {
        var p = new ModelParams(modelPath)
        {
            ContextSize = (uint)Math.Max(ctxTokens, 2048),   // per-job num_ctx (design 7.2)
            GpuLayerCount = gpuLayerCount,
            TypeK = GGMLType.GGML_TYPE_Q8_0,                 // KV cache q8_0 (design 7.2)
            TypeV = GGMLType.GGML_TYPE_Q8_0,
            FlashAttention = true,                           // required for the quantized V cache
        };
        _weights = LLamaWeights.LoadFromFile(p);
        _context = _weights.CreateContext(p);
        // InteractiveExecutor keeps the KV state across InferAsync calls - the warm-chat
        // prefix-reuse mechanism (design 7.1): the warmup prefills once, answers append.
        _executor = new InteractiveExecutor(_context);
        Backend = backend;
    }

    public static (string Prompt, int MaxTokens) ReadPayload(string payloadJson)
    {
        var o = JsonNode.Parse(payloadJson)!.AsObject();
        return (o["prompt"]?.GetValue<string>()
                    ?? throw new InvalidDataException("payload has no prompt"),
                o["maxTokens"]?.GetValue<int>() ?? 1024);
    }

    public async IAsyncEnumerable<string> InferAsync(string prompt, int maxTokens,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // ChatML template (Qwen3 family). Qwen3-*-Instruct-2507 is a NON-thinking model -
        // thinking disabled by model choice, the whole budget goes to the answer (design 7.2).
        string wrapped = "<|im_start|>user\n" + prompt + "<|im_end|>\n<|im_start|>assistant\n";
        LastPromptTokens = _context.Tokenize(wrapped).Length;
        var ip = new InferenceParams
        {
            MaxTokens = maxTokens,
            AntiPrompts = ["<|im_end|>"],
        };
        await foreach (string piece in _executor.InferAsync(wrapped, ip, ct))
            yield return piece;
    }

    public void Dispose()
    {
        _context.Dispose();
        _weights.Dispose();
    }
}
```
- [ ] **Build the whole solution 0-warning (the gate for this task).** Run:
```
dotnet build "src\LocalScribe.Assistant\LocalScribe.Assistant.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\
dotnet build "LocalScribe.slnx" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\
```
Expected: both succeed with 0 warnings. If the LLamaSharp API drifted from the pinned version (member names like `FlashAttention`/`TypeK`/`AntiPrompts`), fix THIS project mechanically to the restored version's API, preserving: ctx sizing, KV q8_0, cuda-then-cpu pick with honest `Backend`, streamed pieces, anti-prompt stop. Then verify isolation: `dotnet list "src\LocalScribe.App\LocalScribe.App.csproj" package` and `dotnet list "src\LocalScribe.Core\LocalScribe.Core.csproj" package` must show NO LLamaSharp entries, and `LocalScribe.App.csproj` has NO ProjectReference to LocalScribe.Assistant.
- [ ] **Publish smoke-prep (document, do not run the model).** Append nothing to any doc yet — Task 14's runbook carries the publish command. Sanity-publish only if quick: `dotnet publish src/LocalScribe.Assistant -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\assistant-publish` and confirm a single `LocalScribe.Assistant.exe` lands there. Copy ONLY the `.exe` when deploying (never the folder — native-DLL collision rule).
- [ ] **Commit.**
```
git add src/LocalScribe.Assistant/LocalScribe.Assistant.csproj src/LocalScribe.Assistant/Program.cs src/LocalScribe.Assistant/LlamaEngine.cs LocalScribe.slnx
git commit -m "feat(assistant): LocalScribe.Assistant.exe - LLamaSharp stdio helper (request loop, cuda->cpu pick, KV q8_0)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 12: App — assistant services in `CompositionRoot` + Settings Assistant section (toggle, model picker, fetch instructions)
**Files:**
- Modify `src\LocalScribe.App\CompositionRoot.cs` (add `using LocalScribe.Core.Assistant;` — `LocalScribe.Core.Transcription` is already imported for `WhisperEngineFactory`/`ModelPaths`, verify; extend the `AppComposition` record at lines 17-31 with four members; construct the services immediately after the Diarizer block at lines 124-125; extend the `return new AppComposition(...)` at lines 127-129).
- Modify `src\LocalScribe.App\ViewModels\SettingsPageViewModel.cs` (append one optional ctor param; add the Assistant property group; kick the manifest load in the ctor).
- Modify `src\LocalScribe.App\SettingsPage.xaml` (one new `ui:Card` after the Privacy card, lines 130-146).
- Modify `src\LocalScribe.App\App.xaml.cs` (pass `comp.AssistantModels` at the `settingsVm` construction, lines 187-196).
- Create `tests\LocalScribe.App.Tests\SettingsPageViewModelAssistantTests.cs`

**Interfaces:**
- Produces:
  - `AppComposition` gains `SummaryStore Summaries`, `SummarizationService Summarizer`, `AssistantManifestCache AssistantModels`, `IAssistantChatSessionFactory AssistantChat` (the chat factory is wired NOW so `feat/matter-qa` only consumes — it has no consumer on this branch, which is intentional).
  - The `AssistantGate` recording probe — the SAME idle/finalizing condition `CompositionRoot.cs:92-95` gives `RetranscriptionRunner` (single source of truth for "recording busy").
  - `SettingsPageViewModel`: `public bool AssistantEnabled` + `public string AssistantModel` (both via the existing `Commit(s => s with { Assistant = s.Assistant with { ... } })` auto-save pattern, `SettingsPageViewModel.cs:186-195` precedent), `public ObservableCollection<string> AssistantModelChoices`, `[ObservableProperty] string AssistantModelsNote`, `[ObservableProperty] bool HasAssistantModels`, `public Task AssistantModelsLoad` (tests await it — the `LastSave` precedent), `public const string NoAssistantModelsNote` (the fetch instructions). New optional ctor param `AssistantManifestCache? assistantModels = null` appended AFTER `string? modelsRoot = null` — every existing construction/test call keeps compiling.
- Consumes: Tasks 4/7/8/9/10/11; `ProcessAssistantHelper` (Task 7); `ISettingsService`/`Commit` plumbing; `ui:Card`/`FieldRow`/`FieldLabel`/`Note` styles; the App-layer fakes (`FakeSettingsService`, `FakeUiErrorReporter`, `FakeRecycleBin`, `FakeLaunchAtLogin`, `FakeCaptureDeviceEnumerator` — the exact set `SettingsPageViewModelTests.MakeVm` at `SettingsPageViewModelTests.cs:30-39` uses; mirror that arrangement).

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\SettingsPageViewModelAssistantTests.cs` (mirror the harness arrangement of `SettingsPageViewModelTests.cs:30-39` exactly — if a fake's constructor differs, copy that file's usage):
```csharp
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.Tests;

public sealed class SettingsPageViewModelAssistantTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-set-assist-").FullName;
    private readonly FakeSettingsService _settings = new(new Settings());
    private readonly FakeUiErrorReporter _errors = new();
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly AssistantModelInfo Qwen4B =
        new("Qwen3-4B-Instruct-2507", @"C:\m\q4b.gguf", new string('a', 64), 262144, "Apache-2.0");
    private static readonly AssistantModelInfo Qwen17 =
        new("Qwen3-1.7B-Instruct", @"C:\m\q17.gguf", new string('b', 64), 32768, "Apache-2.0");

    private SettingsPageViewModel MakeVm(AssistantManifestCache? cache = null)
    {
        var maintenance = new Services.MaintenanceService(
            new StoragePaths(Path.Combine(_root, "storage")), _settings, new FakeRecycleBin(),
            TimeProvider.System);
        return new SettingsPageViewModel(_settings, maintenance, new FakeLaunchAtLogin(),
            pickFolder: () => null, openFolder: _ => { }, _errors,
            dispatch: a => a(), new FakeCaptureDeviceEnumerator(),
            modelsRoot: Path.Combine(_root, "models"), assistantModels: cache);
    }

    [Fact]
    public async Task Toggle_and_model_pick_persist_via_the_commit_pattern()
    {
        var cache = new AssistantManifestCache(_ => Task.FromResult(
            new AssistantModelManifest([Qwen4B, Qwen17], Qwen4B, [])));
        var vm = MakeVm(cache);
        await vm.AssistantModelsLoad;

        vm.AssistantEnabled = false;
        await vm.LastSave;
        Assert.False(_settings.Current.Assistant.Enabled);

        vm.AssistantModel = "Qwen3-1.7B-Instruct";
        await vm.LastSave;
        Assert.Equal("Qwen3-1.7B-Instruct", _settings.Current.Assistant.Model);

        // Picking the locked default stores null (the "no explicit pick" sentinel).
        vm.AssistantModel = "Qwen3-4B-Instruct-2507";
        await vm.LastSave;
        Assert.Null(_settings.Current.Assistant.Model);
        Assert.Equal("Qwen3-4B-Instruct-2507", vm.AssistantModel);   // getter echoes the default
    }

    [Fact]
    public async Task Installed_models_populate_the_picker()
    {
        var cache = new AssistantManifestCache(_ => Task.FromResult(
            new AssistantModelManifest([Qwen4B, Qwen17], Qwen4B, [])));
        var vm = MakeVm(cache);
        await vm.AssistantModelsLoad;
        Assert.Equal(new[] { "Qwen3-4B-Instruct-2507", "Qwen3-1.7B-Instruct" },
            vm.AssistantModelChoices);
        Assert.True(vm.HasAssistantModels);
        Assert.Equal("", vm.AssistantModelsNote);
    }

    [Fact]
    public async Task No_model_shows_fetch_instructions_and_disables_the_picker()
    {
        // Design 7.6: fetch instructions when no model is present; features off with explainer.
        var vm = MakeVm(new AssistantManifestCache(_ => Task.FromResult(
            new AssistantModelManifest([], null, []))));
        await vm.AssistantModelsLoad;
        Assert.False(vm.HasAssistantModels);
        Assert.Contains("fetch-models.ps1 -Assistant", vm.AssistantModelsNote);
        Assert.Contains("Qwen3-4B-Instruct-2507", vm.AssistantModelsNote);
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SettingsPageViewModelAssistantTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` — expected: `error CS1739: The best overload for 'SettingsPageViewModel' does not have a parameter named 'assistantModels'` (plus CS1061 on the new properties).
- [ ] **Extend `SettingsPageViewModel`.** In `src\LocalScribe.App\ViewModels\SettingsPageViewModel.cs`: add `using LocalScribe.Core.Assistant;` to the using block; append the ctor parameter — the signature currently ends (lines 84-87):
```csharp
    public SettingsPageViewModel(ISettingsService settings, MaintenanceService maintenance,
        ILaunchAtLogin launchAtLogin, Func<string?> pickFolder, Action<string> openFolder,
        IUiErrorReporter errors, Action<Action> dispatch, ICaptureDeviceEnumerator deviceEnumerator,
        string? modelsRoot = null)
```
— change the last line to `string? modelsRoot = null, AssistantManifestCache? assistantModels = null)`, store the field `private readonly AssistantManifestCache? _assistantModels;` (assign in the ctor body alongside the existing assignments: `_assistantModels = assistantModels;`), and at the END of the ctor body add `AssistantModelsLoad = LoadAssistantModelsAsync();`. Then add the property group (place it after the existing Privacy/Overlay property region, before the private `Commit` helpers at line 404):
```csharp

    // --- Assistant (design 2026-07-18 sections 7.2/7.6) ---------------------------------

    /// <summary>Fetch instructions shown when no model is installed (design 7.6). Public
    /// const: tests and the note binding share one source of truth.</summary>
    public const string NoAssistantModelsNote =
        "No assistant model is installed. Run: pwsh tools/fetch-models.ps1 -Assistant "
        + "(downloads Qwen3-4B-Instruct-2507 q4_K_M, about 2.5 GB, SHA-verified, local-only). "
        + "Assistant features stay off until a model is present.";

    /// <summary>Awaitable manifest-load (the LastSave precedent - tests await it).</summary>
    public Task AssistantModelsLoad { get; private set; } = Task.CompletedTask;

    public ObservableCollection<string> AssistantModelChoices { get; } = [];

    [ObservableProperty] private string _assistantModelsNote = "";
    [ObservableProperty] private bool _hasAssistantModels;

    /// <summary>Master toggle (design 7.6). Auto-saved via the standard Commit pattern.</summary>
    public bool AssistantEnabled
    {
        get => _settings.Current.Assistant.Enabled;
        set
        {
            Commit(s => s with { Assistant = s.Assistant with { Enabled = value } });
            OnPropertyChanged();
        }
    }

    /// <summary>Model picker over manifest canonical names. Storing the locked default
    /// stores null (the "no explicit pick" sentinel), so a future default change follows.</summary>
    public string AssistantModel
    {
        get => _settings.Current.Assistant.Model ?? AssistantModelManifest.DefaultCanonicalName;
        set
        {
            Commit(s => s with
            {
                Assistant = s.Assistant with
                {
                    Model = string.IsNullOrWhiteSpace(value)
                            || value == AssistantModelManifest.DefaultCanonicalName ? null : value,
                },
            });
            OnPropertyChanged();
        }
    }

    /// <summary>Loads installed models off the UI thread (hash verify is seconds on a
    /// multi-GB file) and projects them onto the picker. No cache injected (tests of the
    /// non-assistant surface) -> instructions note only.</summary>
    private async Task LoadAssistantModelsAsync()
    {
        if (_assistantModels is null)
        {
            _dispatch(() => AssistantModelsNote = NoAssistantModelsNote);
            return;
        }
        try
        {
            var manifest = await Task.Run(() => _assistantModels.GetAsync(CancellationToken.None));
            _dispatch(() =>
            {
                AssistantModelChoices.Clear();
                foreach (var m in manifest.Installed) AssistantModelChoices.Add(m.CanonicalName);
                HasAssistantModels = manifest.Installed.Count > 0;
                AssistantModelsNote = manifest.Installed.Count > 0
                    ? string.Join(" ", manifest.Notes)   // surfaced degradation (excluded entries)
                    : NoAssistantModelsNote;
                OnPropertyChanged(nameof(AssistantModel));
            });
        }
        catch (Exception ex) { _errors.Report("Loading assistant models", ex); }
    }
```
(If this file's settings-service/dispatch/errors fields are named differently from `_settings`/`_dispatch`/`_errors`, use ITS names — verify against the existing `Commit`/`RemoteApp` members quoted above.)
- [ ] **Add the Settings card.** In `src\LocalScribe.App\SettingsPage.xaml`, the Privacy card currently closes (lines 145-146):
```xml
                </StackPanel>
            </ui:Card>
```
(the one whose StackPanel starts with `<TextBlock Text="Privacy" ...` at line 132 — match by that context). Immediately after that `</ui:Card>` insert:
```xml

            <!-- Local assistant (design 2026-07-18 section 7.6): master toggle + manifest
                 model picker + fetch instructions. Strictly local-only; every artifact is an
                 AI-generated draft. Disabled-with-explainer until a model exists. -->
            <ui:Card Style="{StaticResource SectionCard}">
                <StackPanel>
                    <TextBlock Text="Assistant" FontWeight="SemiBold" Margin="0,0,0,8" />
                    <CheckBox Content="Enable the local assistant (session summaries)"
                              IsChecked="{Binding AssistantEnabled}" Margin="0,4,0,4" />
                    <StackPanel Style="{StaticResource FieldRow}">
                        <TextBlock Text="Model" Style="{StaticResource FieldLabel}" />
                        <ComboBox ItemsSource="{Binding AssistantModelChoices}"
                                  SelectedItem="{Binding AssistantModel}" MinWidth="240"
                                  IsEnabled="{Binding HasAssistantModels}" />
                    </StackPanel>
                    <TextBlock Text="{Binding AssistantModelsNote, Mode=OneWay}"
                               Style="{StaticResource Note}" />
                    <TextBlock Text="Runs entirely on this computer - transcript content never leaves the machine. Summaries are AI-generated drafts, not transcripts. Generation may take several minutes on CPU-only machines."
                               Style="{StaticResource Note}" />
                </StackPanel>
            </ui:Card>
```
- [ ] **Wire the composition root.** In `src\LocalScribe.App\CompositionRoot.cs`: add `using LocalScribe.Core.Assistant;`. Extend the `AppComposition` record (lines 17-31) by appending four members to its parameter list, keeping the existing order intact:
```csharp
    SummaryStore Summaries,
    SummarizationService Summarizer,
    AssistantManifestCache AssistantModels,
    IAssistantChatSessionFactory AssistantChat
```
The Diarizer block currently reads (lines 124-125):
```csharp
        string diarizerExe = Path.Combine(AppContext.BaseDirectory, "LocalScribe.Diarizer.exe");
        IDiarisationEngine diarisation = new SherpaHelperDiariser(new ProcessDiarisationHelper(diarizerExe));
```
Immediately after it insert:
```csharp

        // Local assistant (design 2026-07-18 section 7): out-of-process LLamaSharp helper,
        // resolved beside the app exactly like Diarizer - no ProjectReference, no auto-copy
        // (native-DLL isolation, see the csproj comment). AssistantGate probes the SAME
        // recording-busy condition RetranscriptionRunner uses (above): assistant jobs yield
        // to recording, visibly queued; recording is NEVER gated by the assistant.
        string assistantExe = Path.Combine(AppContext.BaseDirectory, "LocalScribe.Assistant.exe");
        var assistantProcs = new ProcessAssistantHelper(assistantExe);
        var assistantModels = new AssistantManifestCache(
            ct => Task.Run(() => AssistantModelManifest.LoadAsync(ModelPaths.ModelsRoot, ct), ct));
        var summaries = new SummaryStore(paths);
        var assistantGate = new AssistantGate(() => controller.State != SessionState.Idle
            ? "Waiting for the recording to finish before running the assistant..."
            : !controller.PendingFinalize.IsCompleted
                ? "Waiting for the previous recording to finish finalizing..."
                : null);
        var summarizer = new SummarizationService(paths, current, TimeProvider.System,
            new AssistantJobRunner(assistantProcs), summaries, assistantGate, assistantModels);
        var assistantChat = new AssistantChatSessionFactory(assistantProcs);   // consumed by feat/matter-qa
```
and extend the `return new AppComposition(...)` (lines 127-129) with `, summaries, summarizer, assistantModels, assistantChat` appended before the closing parenthesis.
- [ ] **Pass the cache to the Settings VM.** In `src\LocalScribe.App\App.xaml.cs` the construction currently ends (line 196):
```csharp
            openFolder: p => System.Diagnostics.Process.Start("explorer.exe", p),
            errors, dispatch, comp.DeviceEnumerator);
```
Replace the last line with:
```csharp
            errors, dispatch, comp.DeviceEnumerator, assistantModels: comp.AssistantModels);
```
(the existing call passes no `modelsRoot`, so use the named argument).
- [ ] **Run tests and see PASS.** Same filter — expected: 3 passed. Then the pinned neighbors: `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~SettingsPageViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` (the ctor change is additive-optional — every existing call keeps compiling) and a full solution build 0-warning.
- [ ] **Commit.**
```
git add src/LocalScribe.App/CompositionRoot.cs src/LocalScribe.App/ViewModels/SettingsPageViewModel.cs src/LocalScribe.App/SettingsPage.xaml src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/SettingsPageViewModelAssistantTests.cs
git commit -m "feat(app): assistant services in the composition root + Settings Assistant section (toggle, picker, fetch note)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 13: App — Session Details **Assistant** tab (summary render, version switcher, stale badge, Regenerate, streaming, queued/error, AI-draft label)
**Files:**
- Create `src\LocalScribe.App\ViewModels\AssistantTabViewModel.cs`
- Modify `src\LocalScribe.App\ViewModels\MetadataEditorViewModel.cs` (optional ctor param + property + one `LoadAsync` hook — purely additive; the pinned ctor call in `MetadataEditorViewModelTests.cs:60-61` keeps compiling).
- Modify `src\LocalScribe.App\SessionDetailsWindow.xaml` (third `TabItem` inside the `TabControl`, lines 54-256).
- Modify `src\LocalScribe.App\App.xaml.cs` (build the tab VM inside the `openSessionDetails` factory, lines 271-314).
- Create `tests\LocalScribe.App.Tests\AssistantTabViewModelTests.cs`

**Interfaces:**
- Produces: `public sealed partial class AssistantTabViewModel : ObservableObject` — ctor `(SummarizationService summarizer, SummaryStore store, AssistantManifestCache models, ISettingsService settings, IUiErrorReporter errors, Action<Action> dispatch)`; surface: `string DraftLabel` (=> `AssistantPrompts.DraftLabel` — the locked label, rendered above every summary), `ObservableCollection<SummaryVersion> Versions` (newest first), `SummaryVersion? SelectedVersion` (switcher; drives `ContentText`/`IsStale`/`VersionInfo`/`HasSummary`), `bool IsRunning`, `string WaitingText` (the VISIBLE queued state), `string PhaseText` (progress phases — the CPU-honesty surface), `string StreamText` (live chunk stream), `string ErrorText` (§7.7: visible error, nothing persisted), `bool AssistantAvailable` + `string DisabledExplainer` (disabled-with-explainer until the master toggle is on AND a model exists), `IAsyncRelayCommand RegenerateCommand` (the explicit CTA — regeneration is never automatic), `Task LoadAsync(string sessionId, CancellationToken ct)`. House rules honored: WPF-free, `Action<Action> dispatch` injected (never `Progress<T>`), heavy work on `Task.Run`.
- `MetadataEditorViewModel` gains `public AssistantTabViewModel? Assistant { get; }` via optional ctor param `AssistantTabViewModel? assistant = null`; its `LoadAsync` chains `Assistant.LoadAsync(sessionId, ct)`.
- Consumes: Tasks 4/8/10 services from `comp.*` (Task 12), the `TabControl` structure (`SessionDetailsWindow.xaml:54-256`), `SectionCard`/`FieldRow`/`FieldLabel`/`Note`/`MutedText`/`WarningText` styles, `BoolToVis`, `ui:Button`, the caution-Border banner precedent (`SettingsPage.xaml:38-41`).

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.App.Tests\AssistantTabViewModelTests.cs`:
```csharp
using System.Runtime.CompilerServices;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.Tests;

public sealed class AssistantTabViewModelTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-assist-tab-").FullName;
    private readonly StoragePaths _paths;
    private readonly SummaryStore _store;
    public AssistantTabViewModelTests() { _paths = new StoragePaths(_root); _store = new SummaryStore(_paths); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class FakeRunner(Func<AssistantRequest, IEnumerable<AssistantEvent>> script) : IAssistantJobRunner
    {
        public async IAsyncEnumerable<AssistantEvent> RunAsync(AssistantRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var e in script(request)) { await Task.Yield(); yield return e; }
        }
    }

    private static readonly AssistantModelInfo Model =
        new("Qwen3-4B-Instruct-2507", @"C:\m\q4b.gguf", new string('a', 64), 262144, "Apache-2.0");

    private static LoadedProjection Projection()
    {
        var started = new DateTimeOffset(2026, 7, 19, 9, 0, 0, TimeSpan.Zero);
        var rows = new List<DisplayRow>
        { new() { DisplayName = "Sam", Text = "We agreed to file Tuesday.", StartMs = 0, EndMs = 2000 } };
        return new LoadedProjection(
            new SessionRecord(), SessionMeta.CreateDefault("Webex", started, self: null),
            [], null, null, new Dictionary<string, Matter>(), [], started, rows,
            new TranscriptHeader("t", "Webex", started, 0, "base.en", "CPU"),
            new SessionTextView("t", [], [], started, null, 0, "call", "", null), "v1");
    }

    private AssistantTabViewModel MakeVm(
        Func<AssistantRequest, IEnumerable<AssistantEvent>>? script = null,
        bool enabled = true, bool anyModel = true)
    {
        var runner = new FakeRunner(script ?? (_ =>
            [new AssistantChunk("## Summary\nFiled Tuesday."), new AssistantDone("cpu", 5, 3)]));
        var cache = new AssistantManifestCache(_ => Task.FromResult(new AssistantModelManifest(
            anyModel ? [Model] : [], anyModel ? Model : null, [])));
        var settings = new FakeSettingsService(new Settings
        { Assistant = new AssistantSetting { Enabled = enabled } });
        var summarizer = new SummarizationService(_paths, () => settings.Current, TimeProvider.System,
            runner, _store, new AssistantGate(() => null, pollMs: 10), cache,
            loadProjection: (_, _) => Task.FromResult(Projection()));
        return new AssistantTabViewModel(summarizer, _store, cache, settings,
            new FakeUiErrorReporter(), dispatch: a => a());
    }

    [Fact]
    public async Task Disabled_with_explainer_when_toggle_off_or_no_model()
    {
        // Design 7.6: all assistant UI disabled-with-explainer until a model exists.
        var off = MakeVm(enabled: false);
        await off.LoadAsync("s1", CancellationToken.None);
        Assert.False(off.AssistantAvailable);
        Assert.Contains("turned off in Settings", off.DisabledExplainer);
        Assert.False(off.RegenerateCommand.CanExecute(null));

        var noModel = MakeVm(anyModel: false);
        await noModel.LoadAsync("s1", CancellationToken.None);
        Assert.False(noModel.AssistantAvailable);
        Assert.Contains("No assistant model", noModel.DisabledExplainer);
    }

    [Fact]
    public async Task Regenerate_streams_persists_and_selects_the_new_version_with_the_label()
    {
        var vm = MakeVm();
        await vm.LoadAsync("s1", CancellationToken.None);
        Assert.True(vm.AssistantAvailable);
        Assert.Empty(vm.Versions);

        await vm.RegenerateCommand.ExecuteAsync(null);

        var v = Assert.Single(vm.Versions);
        Assert.Same(v, vm.SelectedVersion);
        Assert.Equal("## Summary\nFiled Tuesday.", vm.ContentText);
        Assert.False(vm.IsStale);
        Assert.Equal("", vm.ErrorText);
        Assert.Contains("q4b.gguf", vm.VersionInfo);                 // provenance line
        Assert.Contains("CPU", vm.VersionInfo);                      // ACTUAL backend surfaced
        Assert.Equal(AssistantPrompts.DraftLabel, vm.DraftLabel);    // the locked label
        Assert.Single(await _store.LoadAsync("s1", CancellationToken.None));   // really persisted
    }

    [Fact]
    public async Task Error_is_visible_and_persists_nothing()
    {
        // Design 7.7: helper crash -> visible error, nothing persisted.
        var vm = MakeVm(_ => [new AssistantError("JOB_FAILED: boom")]);
        await vm.LoadAsync("s1", CancellationToken.None);
        await vm.RegenerateCommand.ExecuteAsync(null);
        Assert.Contains("boom", vm.ErrorText);
        Assert.Empty(vm.Versions);
        Assert.Empty(await _store.LoadAsync("s1", CancellationToken.None));
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Stale_badge_follows_the_selected_stored_version()
    {
        await _store.AppendAsync("s1", new SummaryVersion("s1", DateTimeOffset.UtcNow, "v1",
            new AssistantModelRef("q4b.gguf", new string('a', 64), "cuda"),
            AssistantPrompts.PromptVersion, "## Summary\nOld.", Stale: true), CancellationToken.None);
        var vm = MakeVm();
        await vm.LoadAsync("s1", CancellationToken.None);
        Assert.True(vm.IsStale);                                     // the stale badge state
        Assert.Equal("## Summary\nOld.", vm.ContentText);            // old versions stay readable
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~AssistantTabViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` — expected: `error CS0246: The type or namespace name 'AssistantTabViewModel' could not be found`.
- [ ] **Write the tab VM.** Create `src\LocalScribe.App\ViewModels\AssistantTabViewModel.cs`:
```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Assistant;

namespace LocalScribe.App.ViewModels;

/// <summary>Session Details "Assistant" tab (design 2026-07-18 section 7.6): summary render
/// with version switcher, stale badge, explicit Regenerate CTA (never automatic), streaming,
/// the VISIBLE queued-behind-recording state, visible errors (7.7), and the locked
/// AI-generated-draft label. WPF-free; dispatch injected (house rule - never Progress&lt;T&gt;).</summary>
public sealed partial class AssistantTabViewModel : ObservableObject
{
    private readonly SummarizationService _summarizer;
    private readonly SummaryStore _store;
    private readonly AssistantManifestCache _models;
    private readonly ISettingsService _settings;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private string _sessionId = "";

    public AssistantTabViewModel(SummarizationService summarizer, SummaryStore store,
        AssistantManifestCache models, ISettingsService settings, IUiErrorReporter errors,
        Action<Action> dispatch)
    {
        (_summarizer, _store, _models, _settings, _errors, _dispatch) =
            (summarizer, store, models, settings, errors, dispatch);
        RegenerateCommand = new AsyncRelayCommand(RegenerateAsync, () => AssistantAvailable && !IsRunning);
    }

    /// <summary>The LOCKED artifact label, rendered above every summary (evidentiary rule).</summary>
    public string DraftLabel => AssistantPrompts.DraftLabel;

    /// <summary>Newest first; the switcher keeps every old version readable (append-only store).</summary>
    public ObservableCollection<SummaryVersion> Versions { get; } = [];

    [ObservableProperty] private SummaryVersion? _selectedVersion;
    [ObservableProperty] private string _contentText = "";
    [ObservableProperty] private bool _isStale;
    [ObservableProperty] private string _versionInfo = "";
    [ObservableProperty] private bool _hasSummary;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _waitingText = "";
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string _streamText = "";
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _assistantAvailable;
    [ObservableProperty] private string _disabledExplainer = "";

    public IAsyncRelayCommand RegenerateCommand { get; }

    partial void OnSelectedVersionChanged(SummaryVersion? value)
    {
        ContentText = value?.ContentMarkdown ?? "";
        IsStale = value?.Stale ?? false;
        HasSummary = value is not null;
        VersionInfo = value is null ? "" : string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{value.Id} · {value.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm} · {value.Model.File} ({value.Model.Backend.ToUpperInvariant()}) · transcript {value.SourceTranscriptVersion}");
    }

    partial void OnIsRunningChanged(bool value) => RegenerateCommand.NotifyCanExecuteChanged();
    partial void OnAssistantAvailableChanged(bool value) => RegenerateCommand.NotifyCanExecuteChanged();

    public async Task LoadAsync(string sessionId, CancellationToken ct)
    {
        _sessionId = sessionId;
        try
        {
            bool enabled = _settings.Current.Assistant.Enabled;
            var manifest = enabled ? await Task.Run(() => _models.GetAsync(ct), ct) : null;
            var versions = await _store.LoadAsync(sessionId, ct);
            _dispatch(() =>
            {
                AssistantAvailable = enabled && manifest is { Installed.Count: > 0 };
                DisabledExplainer = !enabled
                    ? "The assistant is turned off in Settings."
                    : manifest is { Installed.Count: 0 }
                        ? "No assistant model is installed - see Settings > Assistant for fetch instructions."
                        : "";
                Versions.Clear();
                foreach (var v in versions.Reverse()) Versions.Add(v);   // newest first
                SelectedVersion = Versions.FirstOrDefault();
            });
        }
        catch (Exception ex) { _errors.Report("Loading assistant state", ex); }
    }

    private async Task RegenerateAsync()
    {
        IsRunning = true;
        ErrorText = ""; WaitingText = ""; PhaseText = ""; StreamText = "";
        try
        {
            var v = await Task.Run(() => _summarizer.SummarizeAsync(_sessionId,
                evt => _dispatch(() => OnJobEvent(evt)),
                reason => _dispatch(() => WaitingText = reason),   // VISIBLY queued (7.1/7.7)
                CancellationToken.None));
            _dispatch(() => { Versions.Insert(0, v); SelectedVersion = v; });
        }
        catch (AssistantException ex) { ErrorText = ex.Message; }   // visible, nothing persisted (7.7)
        catch (Exception ex) { _errors.Report("Generating summary", ex); ErrorText = ex.Message; }
        finally { IsRunning = false; WaitingText = ""; PhaseText = ""; StreamText = ""; }
    }

    private void OnJobEvent(AssistantEvent evt)
    {
        switch (evt)
        {
            case AssistantChunk c:
                StreamText += c.Text;
                WaitingText = "";
                break;
            case AssistantProgress p:
                PhaseText = p.Total > 0 ? $"{p.Phase} {p.Current}/{p.Total}" : p.Phase;
                WaitingText = "";
                break;
        }
    }
}
```
- [ ] **Hook the editor VM.** In `src\LocalScribe.App\ViewModels\MetadataEditorViewModel.cs`: the ctor signature (lines 173-175):
```csharp
    public MetadataEditorViewModel(MaintenanceService maintenance, SessionViewModel session,
        IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time,
        Func<string, bool> confirm)
```
— append `, AssistantTabViewModel? assistant = null` to the parameter list, add the property near the other public surface (immediately before the ctor):
```csharp
    /// <summary>The Assistant tab's sub-VM (design 2026-07-18 section 7.6), or null in tests
    /// that exercise only the Details/Speakers surface. The XAML tab binds Assistant.* -
    /// bindings on a null sub-VM are inert (debug-noise only), matching the optional param.</summary>
    public AssistantTabViewModel? Assistant { get; }
```
assign `Assistant = assistant;` in the ctor body, and at the END of `LoadAsync(string sessionId, CancellationToken ct)` (declared near line 314 — locate the method by name; append after its last existing statement, inside the method) add:
```csharp
        if (Assistant is not null) await Assistant.LoadAsync(sessionId, ct);
```
- [ ] **Add the tab XAML.** In `src\LocalScribe.App\SessionDetailsWindow.xaml`, the Speakers tab currently closes (lines 255-256):
```xml
            </TabItem>
        </TabControl>
```
Replace with:
```xml
            </TabItem>

            <!-- Assistant tab (design 2026-07-18 section 7.6): summary + version switcher +
                 stale badge + explicit Regenerate CTA + streaming + queued/error states.
                 Deliberately NOT inside the IsEditable gate - summaries are derived work
                 product, readable while a session is live/pending; the gate that matters is
                 AssistantAvailable (master toggle + installed model) and the recording gate
                 inside SummarizationService (visibly queued). -->
            <TabItem Header="Assistant">
                <ScrollViewer VerticalScrollBarVisibility="Auto">
                    <StackPanel Margin="4,8,4,4">
                        <TextBlock Text="{Binding Assistant.DisabledExplainer}" TextWrapping="Wrap"
                                   Margin="0,0,0,8">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock" BasedOn="{StaticResource WarningText}">
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Assistant.DisabledExplainer}" Value="">
                                            <Setter Property="Visibility" Value="Collapsed" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>
                        <ui:Card Style="{StaticResource SectionCard}"
                                 IsEnabled="{Binding Assistant.AssistantAvailable}">
                            <StackPanel>
                                <TextBlock Text="Summary" FontWeight="SemiBold" Margin="0,0,0,8" />
                                <StackPanel Style="{StaticResource FieldRow}">
                                    <TextBlock Text="Version" Style="{StaticResource FieldLabel}" />
                                    <ComboBox ItemsSource="{Binding Assistant.Versions}"
                                              SelectedItem="{Binding Assistant.SelectedVersion}"
                                              DisplayMemberPath="Id" MinWidth="140" />
                                    <ui:Button Appearance="Primary" Content="Regenerate summary"
                                               Command="{Binding Assistant.RegenerateCommand}"
                                               Margin="8,0,0,0" />
                                </StackPanel>
                                <Border Background="{DynamicResource SystemFillColorCautionBackgroundBrush}"
                                        CornerRadius="4" Padding="8" Margin="0,6,0,0"
                                        Visibility="{Binding Assistant.IsStale, Converter={StaticResource BoolToVis}}">
                                    <TextBlock Text="Stale - the transcript changed after this summary was generated. Regenerate to refresh; older versions stay available."
                                               TextWrapping="Wrap" />
                                </Border>
                                <TextBlock Text="{Binding Assistant.WaitingText}" TextWrapping="Wrap"
                                           Margin="0,6,0,0">
                                    <TextBlock.Style>
                                        <Style TargetType="TextBlock" BasedOn="{StaticResource WarningText}">
                                            <Style.Triggers>
                                                <DataTrigger Binding="{Binding Assistant.WaitingText}" Value="">
                                                    <Setter Property="Visibility" Value="Collapsed" />
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </TextBlock.Style>
                                </TextBlock>
                                <StackPanel Visibility="{Binding Assistant.IsRunning, Converter={StaticResource BoolToVis}}">
                                    <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
                                        <ProgressBar Width="120" Height="6" IsIndeterminate="True"
                                                     VerticalAlignment="Center" Margin="0,0,8,0" />
                                        <TextBlock Text="{Binding Assistant.PhaseText}" VerticalAlignment="Center" />
                                    </StackPanel>
                                    <TextBlock Text="Generation runs locally in the background and may take several minutes on CPU-only machines. Recording always takes priority."
                                               Style="{StaticResource Note}" />
                                    <TextBox Text="{Binding Assistant.StreamText, Mode=OneWay}" IsReadOnly="True"
                                             TextWrapping="Wrap" BorderThickness="0" MaxHeight="200"
                                             VerticalScrollBarVisibility="Auto" Margin="0,4,0,0" />
                                </StackPanel>
                                <TextBlock Text="{Binding Assistant.ErrorText}" TextWrapping="Wrap"
                                           Margin="0,6,0,0">
                                    <TextBlock.Style>
                                        <Style TargetType="TextBlock" BasedOn="{StaticResource WarningText}">
                                            <Style.Triggers>
                                                <DataTrigger Binding="{Binding Assistant.ErrorText}" Value="">
                                                    <Setter Property="Visibility" Value="Collapsed" />
                                                </DataTrigger>
                                            </Style.Triggers>
                                        </Style>
                                    </TextBlock.Style>
                                </TextBlock>
                                <StackPanel Visibility="{Binding Assistant.HasSummary, Converter={StaticResource BoolToVis}}"
                                            Margin="0,8,0,0">
                                    <TextBlock Text="{Binding Assistant.DraftLabel}" FontStyle="Italic"
                                               Style="{StaticResource Note}" />
                                    <TextBlock Text="{Binding Assistant.VersionInfo}"
                                               Style="{StaticResource MutedText}" Margin="0,2,0,6" />
                                    <TextBox Text="{Binding Assistant.ContentText, Mode=OneWay}" IsReadOnly="True"
                                             TextWrapping="Wrap" BorderThickness="0" />
                                </StackPanel>
                            </StackPanel>
                        </ui:Card>
                    </StackPanel>
                </ScrollViewer>
            </TabItem>
        </TabControl>
```
(If `WarningText`'s TargetType makes `BasedOn` invalid here, drop the `BasedOn` and set `Foreground="{DynamicResource SystemFillColorCautionBrush}"` in the style — match whatever `Fluent.Shared.xaml:16-20` declares. No hardcoded `#AARRGGBB` anywhere — `XamlHygieneTests` must stay green.)
- [ ] **Wire the factory.** In `src\LocalScribe.App\App.xaml.cs`, inside `openSessionDetails` the editor is built (lines 278-286):
```csharp
            var detailEditor = new ViewModels.MetadataEditorViewModel(comp.Maintenance, session,
                errors, dispatch, TimeProvider.System,
```
Immediately BEFORE that statement insert:
```csharp
            var assistantTab = new ViewModels.AssistantTabViewModel(comp.Summarizer, comp.Summaries,
                comp.AssistantModels, comp.Settings, errors, dispatch);
```
and append `, assistant: assistantTab` as the final argument of the `MetadataEditorViewModel(...)` call (after the `confirm:` lambda argument, before its closing `);`).
- [ ] **Run tests and see PASS.** Same filter — expected: 4 passed. Then the pinned neighbors + XAML hygiene: `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~MetadataEditor" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` and `--filter "FullyQualifiedName~XamlHygiene"` — all green (the ctor change is additive-optional).
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/AssistantTabViewModel.cs src/LocalScribe.App/ViewModels/MetadataEditorViewModel.cs src/LocalScribe.App/SessionDetailsWindow.xaml src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/AssistantTabViewModelTests.cs
git commit -m "feat(app): Session Details Assistant tab - versioned summary render, stale badge, streaming Regenerate, queued/error states

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 14: App — staleness wiring on `SessionContentChanged` + archive inclusion test + whole-branch gate + smoke runbook
**Files:**
- Modify `src\LocalScribe.App\App.xaml.cs` (staleness hooks beside the existing search-index wiring at lines 176-181 and 258).
- Create `tests\LocalScribe.Core.Tests\SessionArchiverAssistantTests.cs`
- Add THIS plan file (`docs\plans\2026-07-19-llm-foundation-summaries-plan.md`) to the branch if not already committed (`docs(plans):` commit — the design doc is already on master via the ux-round merge).

**Interfaces:**
- Produces: transcript-content changes mark every summary **stale** (design §7.3: "transcript change (new version, correction save, split) → summaries marked stale; regeneration is an explicit user CTA, never automatic"). Trigger set = `MaintenanceService.SessionContentChanged` + `SessionController.SessionFinalizeCompleted` + `RetranscriptionRunner.RetranscriptionCompleted` — the SAME trio the search index re-derives on (`App.xaml.cs:180-181,258`). Note (deliberate over-approximation, recorded): `SessionContentChanged` also fires on meta saves — title/participants feed the roster preamble and prompt context, so a meta-triggered stale badge is truthful, not a false positive; and `MarkAllStaleAsync` no-ops (no write, no badge flip) when there are no summaries or all are already stale (Task 8).
- Docx exclusion (design §7.3 "docx transcript export excludes assistant content"): **by construction** — `DocxRenderer` consumes only the `Header/TextView/Rows` triple, and `SessionProjectionLoader` never reads `assistant\` (verified in the investigation; no code change, asserted in the self-review). Zip inclusion (design §7.3 "zip archives include the `assistant\` folder as-is"): automatic via `SessionArchiver`'s `SearchOption.AllDirectories` walk — pinned by the new test so a future archiver rewrite cannot silently drop it.
- Consumes: `comp.Summaries` (Task 12), `_shutdownCts` (existing field in `App.xaml.cs`), `SessionArchiver`.

Steps:
- [ ] **Write the failing archive test.** Create `tests\LocalScribe.Core.Tests\SessionArchiverAssistantTests.cs`:
```csharp
using System.IO.Compression;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

public class SessionArchiverAssistantTests
{
    [Fact]
    public async Task Zip_includes_the_assistant_folder_automatically()
    {
        // Design 2026-07-18 section 7.3: zip archives include assistant\ as-is (clearly
        // separated). SessionArchiver walks AllDirectories, so this holds with NO archiver
        // change - this test pins that so a future rewrite cannot silently drop the folder.
        string dir = Directory.CreateTempSubdirectory("ls-arch-assist-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "transcript.jsonl"), "");
            Directory.CreateDirectory(Path.Combine(dir, "assistant"));
            File.WriteAllText(Path.Combine(dir, "assistant", "summaries.json"),
                "{\"schemaVersion\":1,\"versions\":[]}");

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                await SessionArchiver.AddSessionFolderAsync(zip, dir, "s1/", CancellationToken.None);

            ms.Position = 0;
            using var read = new ZipArchive(ms, ZipArchiveMode.Read);
            Assert.NotNull(read.GetEntry("s1/assistant/summaries.json"));
            Assert.NotNull(read.GetEntry("s1/transcript.jsonl"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
```
Run it: `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionArchiverAssistantTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` — expected: **1 passed on the first run** (this pins EXISTING behavior — the deliberate exception to fail-first, mirroring the plan-format's "prove no regression" runs; there is no new production code for it to fail against).
- [ ] **Wire staleness.** In `src\LocalScribe.App\App.xaml.cs`, the search-index wiring currently reads (lines 176-181):
```csharp
        // Search-index live updates (design 2026-07-13 section 2.1): a finalized recording and any
        // gated content mutation (edit save, pins, diarisation, recovery, re-render, version
        // switch, delete) re-index just that session. ReindexSessionAsync catches everything and
        // needs no dispatcher (the index is lock-guarded), so bare fire-and-forget is safe.
        comp.Controller.SessionFinalizeCompleted += id => _ = searchIndex.ReindexSessionAsync(id, _shutdownCts.Token);
        comp.Maintenance.SessionContentChanged += id => _ = searchIndex.ReindexSessionAsync(id, _shutdownCts.Token);
```
Immediately AFTER those two subscription lines insert:
```csharp
        // Assistant summaries staleness (design 2026-07-18 section 7.3): any content change
        // marks EVERY summary stale - regeneration stays an explicit user CTA, never
        // automatic (unlike the search index, which re-derives). Meta saves count too:
        // title/participants feed the roster preamble, so a meta-triggered stale badge is
        // truthful. MarkAllStaleAsync no-ops when there is nothing to flip (Task 8), and a
        // failed mark must never fault the caller - staleness is advisory.
        Action<string> markSummariesStale = id => _ = Task.Run(async () =>
        {
            try { await comp.Summaries.MarkAllStaleAsync(id, _shutdownCts.Token); }
            catch { /* advisory only */ }
        });
        comp.Controller.SessionFinalizeCompleted += markSummariesStale;
        comp.Maintenance.SessionContentChanged += markSummariesStale;
```
Then find the retranscription wiring near line 258 (`comp.Retranscription.RetranscriptionCompleted += ...ReindexSessionAsync...` — locate by the quoted member name; the AppComposition member is the one holding the `RetranscriptionRunner`) and add beside it:
```csharp
        comp.Retranscription.RetranscriptionCompleted += markSummariesStale;   // new version -> stale (7.3)
```
(if `markSummariesStale` is out of scope there, declare it before both wiring sites — one delegate, three subscriptions; audio-import's `Completed` is deliberately NOT wired: a freshly imported session has no summaries, so the mark would always no-op.)
- [ ] **End-to-end staleness proof (App test).** Append to `tests\LocalScribe.App.Tests\AssistantTabViewModelTests.cs` (inside the class, before the closing brace):
```csharp
    [Fact]
    public async Task Store_marked_stale_shows_the_badge_on_reload()
    {
        // The wiring calls MarkAllStaleAsync on SessionContentChanged; this pins the
        // store->tab half of that path (the event->store half is the one-line delegate above,
        // exercised by the existing MaintenanceService event coverage).
        var vm = MakeVm();
        await vm.LoadAsync("s1", CancellationToken.None);
        await vm.RegenerateCommand.ExecuteAsync(null);
        Assert.False(vm.IsStale);

        await _store.MarkAllStaleAsync("s1", CancellationToken.None);   // = the wired reaction
        await vm.LoadAsync("s1", CancellationToken.None);
        Assert.True(vm.IsStale);
    }
```
Run: `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~AssistantTabViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\` — expected: 5 passed.
- [ ] **Whole-branch gate.** Run:
```
dotnet build "LocalScribe.slnx" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "Category!=Fixture" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\llm-foundation\
```
Expected: build 0 warnings; App suite fully green; Core suite green except the 2 known pre-existing fixture failures. Also re-verify the isolation invariants one last time: no LLamaSharp reference in App/Core csproj; no ProjectReference to `LocalScribe.Assistant` anywhere; `git grep -l "Socket\|TcpClient\|HttpClient" src/LocalScribe.Assistant src/LocalScribe.Core/Assistant` returns nothing (no-sockets rule).
- [ ] **Manual smoke runbook (USER, real model — the ONLY real-model runs, per the locked rule).** Prereqs: `pwsh tools/fetch-models.ps1 -Assistant` (~2.5 GB, SHA-verified); publish + deploy the helper:
```
dotnet publish src/LocalScribe.Assistant -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o C:\temp\assistant-publish
Copy-Item C:\temp\assistant-publish\LocalScribe.Assistant.exe src\LocalScribe.App\bin\Debug\net10.0-windows\LocalScribe.Assistant.exe
```
(copy ONLY the `.exe` — never the publish folder; Diarizer native-DLL collision rule.) Then:
  1. **Settings:** Assistant card shows the toggle + "Qwen3-4B-Instruct-2507" in the picker; delete/rename the manifest → note shows the fetch instructions and the picker disables.
  2. **First summary (GPU box):** open a real session's Session Details > Assistant, Regenerate — phases stream (`load-cuda`/`prefill`), chunks stream live, result renders under the "AI-generated draft — not a transcript; verify against the record." label with provenance reading `(CUDA)`; `assistant\summaries.json` appears in the session folder; transcript files untouched (compare mtimes).
  3. **Stale + versions:** save a correction in the Read view → Assistant tab shows the stale badge on reload; Regenerate → a second version appears in the switcher; the old one is still selectable and readable.
  4. **Queued behind recording:** start a recording, hit Regenerate → visible "Waiting for the recording to finish..." state, NO helper process spawns (Task Manager); stop the recording → the job proceeds by itself. Recording start is NEVER blocked by a running summary job.
  5. **CPU floor:** Settings backend is auto — on a GPU-less box (or with CUDA hidden), the run completes with provenance `(CPU)` and the UI shows the "may take several minutes" note; a 1 h session lands in the §7.2 envelope (~4-8 min).
  6. **2 h map-reduce:** a long session (or a re-transcribed concatenation) goes down the map path — progress reads `map i/N` then `reduce`; the result carries all four section headers.
  7. **Failure posture:** rename the GGUF mid-flight → visible error in the tab, `summaries.json` unchanged; kill `LocalScribe.Assistant.exe` mid-run → visible "exited before completing" error, nothing persisted.
  8. **Export:** session zip export contains `assistant/summaries.json`; docx export contains NO assistant content.
- [ ] **Commit (including the plan doc if untracked).**
```
git add src/LocalScribe.App/App.xaml.cs tests/LocalScribe.Core.Tests/SessionArchiverAssistantTests.cs tests/LocalScribe.App.Tests/AssistantTabViewModelTests.cs docs/plans/2026-07-19-llm-foundation-summaries-plan.md
git commit -m "feat(app): mark summaries stale on content change; pin assistant/ zip inclusion; plan doc

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Self-review

**(a) Spec coverage — every assigned design section maps to tasks:**
- **§7.1 runtime** → Task 11 (`LocalScribe.Assistant.exe`, LLamaSharp CUDA→CPU with actual-backend reporting, stdio JSON-lines, keepAlive loop, per-job ctx, KV q8_0, thinking disabled via non-thinking model), Task 1 (wire), Task 6 (spawn-per-job runner, cancel = kill, App-side inactivity watchdog, warm chat-session factory with KV reuse via the resident process + `InteractiveExecutor`), Task 7 (production spawner + stub protocol tests), Task 9 + 12 (one-heavy-engine: blocked-while-recording, visibly queued, recording never gated). No sockets anywhere; helper never writes files (grep-verified in Task 14's gate).
- **§7.2 models** → Task 4 (manifest `{canonicalName, file, sha256, nativeCtx, license}`, verify-on-load fail-closed, locked default preferred), Task 5 (SHA-pinned fetch flow extension + manifest write; never bundled), Task 2 (`JobCtxTokens` per-job sizing, 32k operating budget), Task 11 (KV q8_0 + FlashAttention), Task 12 + 13 (CPU-envelope honesty in UI copy: "may take several minutes on CPU-only machines").
- **§7.3 storage** → Task 8 (append-only versioned `assistant\summaries.json` with exactly `{id, createdAt, sourceTranscriptVersion, model{file,sha256,backend}, promptVersion, contentMarkdown, stale}` via AtomicFile + schema stamp; `SourceTranscriptVersion` = `SessionRecord.ActiveVersion`/`LoadedProjection.VersionId`), Task 14 (stale on `SessionContentChanged`/finalize/retranscription; regenerate = explicit CTA only; zip includes `assistant\` — pinned; docx excludes — by construction), Task 3 (`PromptVersion` bumped constant). Chat history (`chats.json`) is matter-qa's.
- **§7.4 summarization** → Task 2 (80% gate, 2 chars/token, timestamps stripped line-anchored, named-speaker roster + preamble), Task 3 (fixed section headers, grounding line, map cap 600), Task 10 (single-call vs map-reduce, hierarchical reduce depth 2 then honest too-long error, empty output ⇒ error + nothing persisted).
- **§7.6 (summary portion)** → Task 13 (Assistant tab: version switcher, stale badge, Regenerate CTA, streaming render, queued/error states, locked AI-draft label) + Task 12 (Settings section: master toggle, model picker, fetch instructions, disabled-with-explainer). Matters tab + chat UI = matter-qa (NOT built; contracts produced).
- **§7.7 failure posture** → helper crash ⇒ visible error, nothing persisted (Tasks 6/7/10/13 tests); mid-recording ⇒ visibly queued (Tasks 9/10/13); model missing ⇒ off-with-explainer (Tasks 4/12/13); CUDA→CPU fall ⇒ recorded in provenance and surfaced (Tasks 11/10/13 `VersionInfo`).
- **Branch-split contracts for matter-qa** — every locked signature appears verbatim in Produces blocks: wire protocol + `AssistantRequest`/`AssistantEvent` family + codecs (Task 1), `TokenBudget` three locked members (Task 2), `AssistantPrompts.PromptVersion`/`BuildAnswerPrompt`/`StripLeadingTimestamps`/`BuildSpeakerPreamble` (Tasks 2-3), `AssistantModelInfo`/`AssistantModelManifest`/`AssistantModelRef` (Task 4), `IAssistantJobRunner`/`IAssistantChatSession`/`IAssistantChatSessionFactory` (Task 6), `SummaryVersion`/`SummaryStore` (Task 8), `AssistantGate.TryEnter(out IDisposable)` (Task 9). `AssistantChatSessionFactory` is composition-wired (Task 12) with zero consumers on this branch — intentional.

**(b) Placeholder scan:** no TBD / "similar to Task N" / elided bodies. Two knowingly-deferred literals are RECORDED DEVIATIONS, not placeholders: GGUF sha256 pins (acquired at fetch time from the HF LFS pointer, fail-closed — deviation 2) and the LLamaSharp version pin re-check (deviation 3, with the exact fallback command and the invariants any API-drift fix must preserve). Every test step carries full code, an exact run command with the isolated BaseOutputPath, and the expected failure/pass; the one deliberate pass-first test (Task 14 archive pin) says so explicitly.

**(c) Type consistency across tasks:** `AssistantRequest.PayloadJson : string` flows Task 1 → runner (Task 6) → helper `ReadPayload` (Task 11) → built by `AssistantWire.PromptPayload` in Task 10. `AssistantEvent` subtypes flow helper `SerializeEvent` → `ParseEventLine` → `IAsyncEnumerable<AssistantEvent>` → `Action<AssistantEvent>` onEvent (Task 10) → `_dispatch`-projected VM state (Task 13). `AssistantDone.Backend : string` → `AssistantModelRef.Backend` → `SummaryVersion.Model` → `VersionInfo` display. `LoadedProjection.VersionId : string` → `SummaryVersion.SourceTranscriptVersion`. `AssistantModelInfo.FilePath` (absolute) → `AssistantRequest.ModelPath`; `Path.GetFileName` → `AssistantModelRef.File`. `AssistantManifestCache` is shared by `SummarizationService` (Task 10), `SettingsPageViewModel` (Task 12), and `AssistantTabViewModel` (Task 13) — one hash-verify per process. All test-called members are `public` (no InternalsVisibleTo). Optional-param additions (`SettingsPageViewModel`, `MetadataEditorViewModel`) keep every pinned ctor call compiling. Known verify-at-execution seams are called out inline where they occur: `SessionMeta.CreateDefault` arg list (Task 10 note), `WarningText` `BasedOn` compatibility (Task 13 note), the `AppComposition` retranscription member name (Task 14 note), private field names in `SettingsPageViewModel` (Task 12 note) — each with the concrete fallback.

**(d) Locked-rules re-check:** no task touches `transcript.jsonl`/`edits.json`/`speakers.json`/`session.json` or any transcript renderer; the only new writes are `assistant\summaries.json` (AtomicFile), `settings.json` (additive field), `assistant-manifest.json` (dev script). The helper writes nothing and the Core assistant namespace's only file writer is `SummaryStore` (App side of the boundary). Recording start paths are untouched — `AssistantGate` is probe-only, `ExternalEngineBusy` is NOT extended. Real models never load in tests. The label string exists in exactly one constant and is asserted in three test layers.










