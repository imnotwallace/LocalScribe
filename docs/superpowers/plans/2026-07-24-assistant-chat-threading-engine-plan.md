# Assistant Chat Threading Engine — Implementation Plan (Phase 0 + Phase 1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the Sessions action-bar wrap bug (Phase 0), then build the chat-threading backend (Phase 1) — named chat threads with real conversation memory and auto-summarized overflow — wired behind the *existing* chat UI so it is fully testable before any windows move (Phases 2-4, later plans).

**Architecture:** `chats.json` v1→v2 stores named threads each carrying verbatim turns plus a rolling recap. A pure `AssistantConversation` builder renders the threaded ChatML tail (recap → prior turns → question) after the byte-identical scope-context prefix. `AssistantQaService` gains an active-thread concept and a budget-driven condense policy that folds oldest turns into the recap via one gated helper call. The existing chat VM is wired to a single default active thread so current UI keeps working with memory + condense; the thread selector/New/Rename/Archive UI is Phase 2.

**Tech Stack:** .NET 10 / WPF, xunit, the existing assistant helper wire (`AssistantWire`, `AssistantJobRunner`, `IAssistantChatSession`), `TokenBudget`, `AssistantChatStore`.

**Spec:** `docs/superpowers/specs/2026-07-24-assistant-chat-surfaces-design.md` (@ 029c179). Read it. This plan implements **Phase 0 and Phase 1 only** — the Architecture > Core section, the engine behind the existing surfaces.

## Global Constraints

- Build gate: `dotnet build LocalScribe.slnx` **0 warnings**; `dotnet test` green except the 2 known Core fixture fails (`DiarisationFixtureTests`, `GoldenCorpusFixtureTests`) and the 1 known App flake (`Stop_upserts...`).
- LOCKED contracts, do not reshape: the stdio wire (`AssistantWire` request/event JSON), `AssistantDone`, `AssistantModelRef`, `IAssistantJobRunner` / `IAssistantChatSession` / factory interfaces. `AssistantChatTurn` stays as-is (its `CudaFellToCpu` additive field remains).
- `AssistantPrompts.PromptVersion` covers **every** prompt change and is snapshot-pinned. Any change to answer-prompt text (this plan adds threaded history) MUST bump it and update the pinned snapshot tests. Bump `1 -> 2` exactly once, in Task 3.
- Warm KV-reuse depends on the scope context (`SpeakerPreamble` + `ContextText`) remaining a byte-identical **prefix** across the warmup and every ask. Threaded history and the question are the **tail** after the context — never interleaved into the context.
- Evidentiary posture: nothing persists on a failed or cancelled ask **or condense**. Degradation (a condense happened, a CUDA fall) is surfaced, never silent.
- Transcript/evidence model is untouched. Only `chats.json` changes.
- `chats.json` v2 must read v1 **forward** (migrate), never reject it. Only a newer-than-v2 file fails loud.
- No Unicode emojis in code or tests. `///` doc comments explain WHY. File-scoped namespaces.
- Branch `feat/assistant-chat-threading` off master. Do not push. Commit locally per task.

## File Structure

```
src/LocalScribe.App/Pages/SessionsPage.xaml                       (Phase 0: StackPanel -> WrapPanel)
src/LocalScribe.Core/Assistant/AssistantChatStore.cs              (v2 model + migration + thread API)
src/LocalScribe.Core/Assistant/AssistantConversation.cs           (new: pure threaded-prompt builder)
src/LocalScribe.Core/Assistant/AssistantPrompts.cs                (recap prompt + PromptVersion bump)
src/LocalScribe.Core/Assistant/AssistantQaService.cs              (active thread + condense policy)
src/LocalScribe.App/ViewModels/AssistantChatViewModel.cs          (load/append against the active thread)
tests/LocalScribe.Core.Tests/AssistantChatStoreTests.cs           (extend: v2 + migration)
tests/LocalScribe.Core.Tests/AssistantConversationTests.cs        (new)
tests/LocalScribe.Core.Tests/AssistantPromptsTests.cs             (bump snapshot; find existing snapshot test)
tests/LocalScribe.Core.Tests/AssistantQaServiceTests.cs           (extend: threading + condense)
tests/LocalScribe.App.Tests/AssistantChatViewModelTests.cs        (extend: active-thread load/append)
```

---

### Task 0: Sessions action-bar wrap fix (Phase 0)

**Files:**
- Modify: `src/LocalScribe.App/Pages/SessionsPage.xaml` (the action-bar container, ~line 58)

**Interfaces:** none.

- [ ] **Step 1: Change the container**

In `SessionsPage.xaml`, the action bar is `<StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">` (~line 58, the one holding View transcript … Delete…). Change the opening tag to `<WrapPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">` and its closing `</StackPanel>` (~line 84) to `</WrapPanel>`. This matches the filter-row `WrapPanel` at line 24 and the buttons already carry `Margin="0,0,8,0"` so they space correctly when wrapped. Do NOT change any button.

- [ ] **Step 2: Build + eyeball**

Run: `dotnet build F:\LocalScribe\LocalScribe.slnx` → 0 warnings. This is a pure XAML layout change; there is no unit test for WPF layout. Verification is the build plus the manual note below.

- [ ] **Step 3: Commit**

```
git add src/LocalScribe.App/Pages/SessionsPage.xaml
git commit -m "fix(sessions): action bar wraps instead of pushing Delete off-screen (StackPanel -> WrapPanel)"
```

Manual smoke (record in the task report, not automated): expand the nav rail so the content pane narrows; the action bar buttons wrap to a second row and **Delete…** stays on-screen.

---

### Task 1: `chats.json` v2 data model + migration

**Files:**
- Modify: `src/LocalScribe.Core/Assistant/AssistantChatStore.cs`
- Test: `tests/LocalScribe.Core.Tests/AssistantChatStoreTests.cs` (extend)

**Interfaces:**
- Produces:
  - `AssistantChatThread { string Id; string Name; DateTimeOffset CreatedAt; bool Archived; string? Recap; string? RecapThroughTurnId; IReadOnlyList<AssistantChatTurn> Turns }`
  - `AssistantChatLog { int SchemaVersion; IReadOnlyList<AssistantChatThread> Chats }` (v2)
  - `AssistantChatStore.Version = 2`
  - `Task<AssistantChatLog> LoadAsync(ct)` — reads v2, **migrates v1 forward**, rejects newer-than-v2.
  - `Task SaveAsync(AssistantChatLog log, ct)` — atomic full-file write (threads are rewritten wholesale: append-turn, update-recap, rename, archive all go through a load-modify-save).
  - `static AssistantChatThread NewThread(string name, DateTimeOffset createdAt)` — empty thread with a fresh `Id` (`Guid.NewGuid().ToString("N")`).
  - `const string MigratedThreadName = "Chat 1"`
- Task 3 (`AssistantQaService`) and Task 4 (VM) consume these.

- [ ] **Step 1: Write the failing tests**

Extend `AssistantChatStoreTests.cs`. Reuse its existing temp-dir + path setup (grep the file for how it constructs the store path). Add:

```csharp
[Fact]
public async Task V1_flat_log_migrates_forward_to_a_single_named_thread()
{
    // A pre-existing v1 chats.json: {schemaVersion:1, turns:[...]}. Hand-author it so this is a
    // genuine old-format read, not a round-trip of the new type.
    var t = new AssistantChatTurn("t1", new DateTimeOffset(2026,7,11,9,0,0,TimeSpan.Zero),
        "q?", "a [00:08].", [], "q4b.gguf", "cpu", "1", false, null, ["s1"], [], [], 0);
    await File.WriteAllTextAsync(_path,
        """{"schemaVersion":1,"turns":[]}""".Replace("[]",
            System.Text.Json.JsonSerializer.Serialize(new[]{ t })));
    var log = await _store.LoadAsync(CancellationToken.None);
    Assert.Equal(2, log.SchemaVersion);
    var thread = Assert.Single(log.Chats);
    Assert.Equal(AssistantChatStore.MigratedThreadName, thread.Name);
    Assert.False(thread.Archived);
    Assert.Null(thread.Recap);
    Assert.Equal("t1", Assert.Single(thread.Turns).Id);   // the v1 turn is preserved
}

[Fact]
public async Task V2_round_trips_multiple_threads_including_recap_and_archived()
{
    var log = new AssistantChatLog
    {
        SchemaVersion = AssistantChatStore.Version,
        Chats =
        [
            AssistantChatStore.NewThread("Deadlines", new DateTimeOffset(2026,7,24,9,0,0,TimeSpan.Zero))
                with { Recap = "earlier: filing due Tue", RecapThroughTurnId = "t3" },
            AssistantChatStore.NewThread("Old", new DateTimeOffset(2026,7,20,9,0,0,TimeSpan.Zero))
                with { Archived = true },
        ],
    };
    await _store.SaveAsync(log, CancellationToken.None);
    var back = await _store.LoadAsync(CancellationToken.None);
    Assert.Equal(2, back.Chats.Count);
    Assert.Equal("earlier: filing due Tue", back.Chats[0].Recap);
    Assert.Equal("t3", back.Chats[0].RecapThroughTurnId);
    Assert.True(back.Chats[1].Archived);
}

[Fact]
public async Task Newer_than_v2_fails_loud()
{
    await File.WriteAllTextAsync(_path, """{"schemaVersion":3,"chats":[]}""");
    await Assert.ThrowsAnyAsync<Exception>(() => _store.LoadAsync(CancellationToken.None));
}

[Fact]
public async Task Missing_file_is_an_empty_v2_log()
{
    var log = await _store.LoadAsync(CancellationToken.None);
    Assert.Equal(AssistantChatStore.Version, log.SchemaVersion);
    Assert.Empty(log.Chats);
}
```

(If the existing file already has a v1 back-compat test asserting the old flat shape, update it to the migrated expectation and note the change in the report.)

- [ ] **Step 2: Run — verify failures**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter AssistantChatStore`
Expected: compile/assert failures — the v2 types and `SaveAsync`/`NewThread`/`MigratedThreadName` do not exist.

- [ ] **Step 3: Implement the v2 store**

Rewrite `AssistantChatStore.cs`. Keep `AssistantChatTurn` exactly as it is now (do not touch it). New content:

```csharp
using System.Text.Json.Nodes;
using LocalScribe.Core.Storage;
namespace LocalScribe.Core.Assistant;

// AssistantChatTurn stays unchanged above/here (leave the existing record + its doc comment).

/// <summary>One named chat thread (design 2026-07-24). Turns are verbatim, append order.
/// Recap is the condensed running summary of the oldest turns that no longer fit the context
/// window (null until the first condense); RecapThroughTurnId is the last turn folded in, so a
/// reopened thread knows where verbatim history resumes. Archived hides the thread from the
/// active selector but keeps it on disk (nothing destroyed).</summary>
public sealed record AssistantChatThread(string Id, string Name, DateTimeOffset CreatedAt,
    bool Archived, string? Recap, string? RecapThroughTurnId, IReadOnlyList<AssistantChatTurn> Turns);

/// <summary>chats.json v2: schema stamp + named threads (design 2026-07-24). v1 was a flat
/// {turns:[...]} single log; LoadAsync migrates that forward to one "Chat 1" thread.</summary>
public sealed record AssistantChatLog
{
    public int SchemaVersion { get; init; } = AssistantChatStore.Version;
    public IReadOnlyList<AssistantChatThread> Chats { get; init; } = [];
}

/// <summary>Per-scope chat store over AtomicFile: assistant\chats.json in the session or matter
/// folder. v2 (design 2026-07-24): named threads, each append-only in its turns but with mutable
/// thread metadata (name/archived) and a rolling recap - so the whole file is a load-modify-save,
/// not a blind append. A v1 flat log is migrated forward on read (never rewritten until the next
/// save); a NEWER-than-v2 file fails loud (SchemaGuard).</summary>
public sealed class AssistantChatStore
{
    public const int Version = 2;
    public const string MigratedThreadName = "Chat 1";
    private readonly string _path;

    public AssistantChatStore(string chatsJsonPath) => _path = chatsJsonPath;

    public static AssistantChatThread NewThread(string name, DateTimeOffset createdAt)
        => new(Guid.NewGuid().ToString("N"), name, createdAt, Archived: false,
               Recap: null, RecapThroughTurnId: null, Turns: []);

    public async Task<AssistantChatLog> LoadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return new AssistantChatLog();
        int version = SchemaGuard.ReadVersion(obj);
        SchemaGuard.RejectIfNewer(version, Version, "chats.json");
        if (version < Version) return MigrateForward(obj, version);
        return await JsonFile.ReadAsync<AssistantChatLog>(_path, ct) ?? new AssistantChatLog();
    }

    public Task SaveAsync(AssistantChatLog log, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        return JsonFile.WriteAsync(_path, log with { SchemaVersion = Version }, ct);
    }

    /// <summary>v1 {schemaVersion:1, turns:[...]} -> one "Chat 1" thread. Pure; the file is not
    /// rewritten here - the next SaveAsync persists v2 (design 2026-07-24 migration is load-only
    /// until a write). CreatedAt takes the first turn's time, else DateTimeOffset default.</summary>
    private static AssistantChatLog MigrateForward(JsonObject obj, int version)
    {
        if (version != 1)
            throw new InvalidDataException($"chats.json v{version} has no forward migration to v{Version}.");
        var turns = obj["turns"].Deserialize<IReadOnlyList<AssistantChatTurn>>(LocalScribeJson.Options)
                    ?? [];
        var created = turns.Count > 0 ? turns[0].AskedAtUtc : default;
        return new AssistantChatLog
        {
            SchemaVersion = Version,
            Chats = [new AssistantChatThread(Guid.NewGuid().ToString("N"), MigratedThreadName,
                        created, Archived: false, Recap: null, RecapThroughTurnId: null, Turns: turns)],
        };
    }
}
```

Verify the actual helper names against the repo before finalizing: `SchemaGuard.ReadObjectAsync/ReadVersion/RejectIfNewer`, `JsonFile.ReadAsync/WriteAsync`, and the shared serializer options constant (the file previously used `JsonFile`; confirm the options type used elsewhere in Core — likely `LocalScribeJson.Options` — and match it; if `JsonNode.Deserialize` needs different options, use what `JsonFile` uses internally). Adjust identifiers only, never behavior.

- [ ] **Step 4: Run — verify pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter AssistantChatStore` → all pass.

- [ ] **Step 5: Commit**

```
git add src/LocalScribe.Core/Assistant/AssistantChatStore.cs tests/LocalScribe.Core.Tests/AssistantChatStoreTests.cs
git commit -m "feat(assistant): chats.json v2 named threads + recap, v1 forward migration"
```

---

### Task 2: `AssistantConversation` — pure threaded-prompt tail builder

**Files:**
- Create: `src/LocalScribe.Core/Assistant/AssistantConversation.cs`
- Test: `tests/LocalScribe.Core.Tests/AssistantConversationTests.cs`

**Interfaces:**
- Produces: `AssistantConversation.BuildHistoryBlock(string? recap, IReadOnlyList<AssistantChatTurn> priorTurns)` → `string` — the block inserted between the scope context and the question. Empty string when there is no recap and no prior turns (so a first question in a fresh thread is byte-identical to today's single-turn prompt tail up to the question). Task 3 concatenates this into the answer prompt.

**Interfaces (consumed):** `AssistantChatTurn.Question` / `AssistantChatTurn.AnswerMarkdown` (Task 1's unchanged turn record).

- [ ] **Step 1: Write the failing tests**

```csharp
using LocalScribe.Core.Assistant;
namespace LocalScribe.Core.Tests;

public sealed class AssistantConversationTests
{
    private static AssistantChatTurn Turn(string q, string a) =>
        new("id", default, q, a, [], "m", "cpu", "1", false, null, [], [], [], 0);

    [Fact]
    public void No_recap_no_turns_is_empty()
        => Assert.Equal("", AssistantConversation.BuildHistoryBlock(null, []));

    [Fact]
    public void Prior_turns_render_as_labelled_pairs_in_order()
    {
        string block = AssistantConversation.BuildHistoryBlock(null,
            [Turn("who spoke?", "Sam [00:08]."), Turn("when?", "Tuesday [00:12].")]);
        // earlier Q/A appear before later ones, each clearly a prior exchange
        Assert.Contains("who spoke?", block);
        Assert.Contains("Sam [00:08].", block);
        Assert.True(block.IndexOf("who spoke?") < block.IndexOf("when?"));
    }

    [Fact]
    public void Recap_precedes_the_verbatim_turns()
    {
        string block = AssistantConversation.BuildHistoryBlock("earlier: filing due Tuesday",
            [Turn("who agreed?", "Sam and you [00:08].")]);
        Assert.Contains("earlier: filing due Tuesday", block);
        Assert.True(block.IndexOf("earlier: filing due Tuesday") < block.IndexOf("who agreed?"));
    }
}
```

- [ ] **Step 2: Run — verify failure** (`AssistantConversation` missing).

Run: `dotnet test tests/LocalScribe.Core.Tests --filter AssistantConversation` → compile failure.

- [ ] **Step 3: Implement**

```csharp
using System.Text;
namespace LocalScribe.Core.Assistant;

/// <summary>Renders a chat thread's memory (design 2026-07-24) as the block inserted BETWEEN the
/// scope context and the new question in the answer prompt: the running recap (condensed oldest
/// turns) then the verbatim prior turns as labelled Q/A pairs. Pure and snapshot-adjacent - the
/// answer prompt's PromptVersion covers it. Empty when a thread has no history yet, so a first
/// question reduces to today's single-turn tail.</summary>
public static class AssistantConversation
{
    public static string BuildHistoryBlock(string? recap, IReadOnlyList<AssistantChatTurn> priorTurns)
    {
        if (string.IsNullOrEmpty(recap) && priorTurns.Count == 0) return "";
        var sb = new StringBuilder();
        sb.Append("Earlier in this conversation (for reference; still cite the transcript):\n");
        if (!string.IsNullOrEmpty(recap))
            sb.Append("Summary of earlier exchanges: ").Append(recap).Append('\n');
        foreach (var t in priorTurns)
            sb.Append("Q: ").Append(t.Question).Append('\n')
              .Append("A: ").Append(t.AnswerMarkdown).Append('\n');
        return sb.ToString();
    }
}
```

- [ ] **Step 4: Run — verify pass.**

- [ ] **Step 5: Commit**

```
git add src/LocalScribe.Core/Assistant/AssistantConversation.cs tests/LocalScribe.Core.Tests/AssistantConversationTests.cs
git commit -m "feat(assistant): AssistantConversation renders thread recap + prior turns for the answer prompt"
```

---

### Task 3: Threaded answer prompt + condense policy in `AssistantQaService`

**Files:**
- Modify: `src/LocalScribe.Core/Assistant/AssistantPrompts.cs` (thread the answer prompt; add the recap prompt; **bump `PromptVersion` 1 -> 2**)
- Modify: `src/LocalScribe.Core/Assistant/AssistantQaService.cs` (active thread; build with history; condense-on-overflow; append to the thread)
- Test: `tests/LocalScribe.Core.Tests/AssistantQaServiceTests.cs` (extend), and the existing prompt snapshot test (find it: grep `PromptVersion` / `BuildAnswerPrompt` under `tests/`)

**Interfaces:**
- Consumes: `AssistantConversation.BuildHistoryBlock` (Task 2); `AssistantChatStore` v2 + `AssistantChatThread` (Task 1); `TokenBudget.EstimateTokens/MaxCtxTokens/FitsGatePercent`; the existing warm-session/gate machinery.
- Produces: `AssistantQaService.AskAsync(string question, string threadId, IProgress<string>? chunks, CancellationToken ct)` → `Task<AssistantChatTurn>` — the turn is appended to thread `threadId`; a condense may have folded older turns first. `AssistantPrompts.BuildAnswerPrompt` gains a `historyBlock` parameter (see below). Task 4 (VM) calls the new `AskAsync` overload with the active thread id.

**Design of the threaded prompt (keeps the warm prefix intact):** the context stays the byte-identical prefix; history + question are the tail.

```
BuildAnswerPrompt(preamble, contextText, historyBlock, question):
   <all existing instruction lines, GroundingLine, citation rule, "if not answered" line, preamble>
   "Context:\n" + contextText + "\n"
   + historyBlock            // "" for a first question -> identical tail to today
   + "Question:\n" + question
```

The warmup (`QaScopeFactory.Warmup`) passes `historyBlock: ""` and an empty question, so the warmup prompt text is unchanged; real asks pass the thread's history block. **Bump `PromptVersion` to 2** because the real-answer prompt shape changed, and update the pinned snapshot(s).

**Condense policy (before building the ask):**

```
budget      = TokenBudget.MaxCtxTokens * FitsGatePercent / 100   (the fits gate)
contextTok  = EstimateTokens(scope context chars)   // always kept
answerRsv   = QaScopeFactory.MaxAnswerTokens
available   = budget - contextTok - answerRsv        // room for recap + verbatim turns + question
loop:
   historyTok = EstimateTokens( BuildHistoryBlock(recap, verbatimTurns).Length ) + EstimateTokens(question.Length)
   if historyTok <= available OR verbatimTurns.Count == 0: break
   // fold the OLDEST verbatim turn into the recap via one gated helper call
   recap = condense(recap, oldestTurn)      // AssistantPrompts.BuildRecapPrompt
   recapThroughTurnId = oldestTurn.Id
   verbatimTurns = verbatimTurns without the oldest
persist the thread (recap, recapThroughTurnId, trimmed verbatim window) BEFORE the answer call
```

`condense` runs one summarize job through the SAME gated runner path the service already uses (reuse `_acquireEngineLease` + a one-shot job; it is cancellable and persists nothing on failure). If the scope context alone already exceeds the gate, keep the existing too-long/no-answer behavior — do not loop forever (guard: if `available <= 0`, skip history entirely and answer with context only, setting the recap indicator).

- [ ] **Step 1: Write the failing tests**

Extend `AssistantQaServiceTests.cs` using its `FakeAssistantChatSessionFactory`/`MakeService` helpers. Add a `FakeRunner`/scripted-session path for the condense call if the service uses a separate runner for it (mirror how the file fakes the chat session). Cases:

```csharp
[Fact]
public async Task Follow_up_includes_prior_turn_in_the_prompt()
{
    // Ask twice in one thread; assert the SECOND ask's payload (captured by the fake session)
    // contains the first question/answer text -> memory is in the prompt.
}

[Fact]
public async Task Turn_is_appended_to_the_named_thread()
{
    // AskAsync(q, threadId,...) appends to that thread's Turns in the store, not a flat log.
}

[Fact]
public async Task Overflow_condenses_oldest_turns_into_recap_and_keeps_context()
{
    // Force a tiny budget (inject a small scope context sized near the gate, or a test seam on the
    // budget) so a third ask must condense: assert the thread's Recap becomes non-empty,
    // RecapThroughTurnId advances, the oldest verbatim turn is dropped from Turns, and the scope
    // context is still present in the ask payload.
}

[Fact]
public async Task Condense_failure_persists_nothing()
{
    // Script the condense call to error: the ask throws, and the thread on disk is unchanged
    // (no partial recap, no dropped turns, no appended turn).
}
```

If forcing overflow needs a seam, add a minimal internal constructor parameter to `AssistantQaService` for the fits-budget (defaulting to `TokenBudget.MaxCtxTokens`), used only by tests — document it as a test seam. Do not change production budgets.

- [ ] **Step 2: Run — verify failures.**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "AssistantQaService|AssistantPrompts"`
Expected: failures (new `AskAsync` overload / `BuildAnswerPrompt` arity / `BuildRecapPrompt` / bumped `PromptVersion`).

- [ ] **Step 3: Implement**

1. `AssistantPrompts.cs`: bump `PromptVersion = 2`; change `BuildAnswerPrompt` to the 4-arg form above (insert `historyBlock` after the context line, before `"Question:\n"`); add:

```csharp
/// <summary>Condense older chat turns into a running recap (design 2026-07-24 overflow policy).
/// Extractive and terse; still grounded in the transcript, never new knowledge.</summary>
public static string BuildRecapPrompt(string? existingRecap, AssistantChatTurn oldest)
    => "Condense the earlier Q&A below into a short running recap (a few sentences), preserving "
     + "any commitments, names, dates and their [HH:MM:SS] citations. Do not add anything new.\n"
     + (string.IsNullOrEmpty(existingRecap) ? "" : "Recap so far: " + existingRecap + "\n")
     + "Q: " + oldest.Question + "\nA: " + oldest.AnswerMarkdown;
```

2. Update every `BuildAnswerPrompt(` call site: `QaScopeFactory.Warmup` passes `historyBlock: ""`; `AssistantQaService` passes the thread's block. Grep for other callers and pass `""` where there is no thread.

3. `AssistantQaService.AskAsync`: add the `string threadId` parameter (keep a back-compat overload only if a caller cannot yet supply it — Task 4 updates the VM, so prefer changing the signature and fixing callers). Load the log, find the thread, run the condense loop (persist the thread on any condense), build the prompt with `BuildHistoryBlock(thread.Recap, thread.Turns)`, run the ask, then append the answered turn to that thread and save. On any exception, do not save the appended turn (the existing catch already resets the warm session; ensure no partial thread write survives — persist condense results only if the whole ask then succeeds, OR persist condense before the answer but guarantee the condense itself is atomic and correct even if the answer later fails; choose the simpler correct option and document it: **persist condense before the answer** is acceptable because a folded recap is valid regardless of whether the next answer succeeds — but a dropped verbatim turn must already be inside the recap, which it is. State this reasoning in the report.).

4. Update the prompt snapshot test to the v2 output and assert `PromptVersion == 2`.

- [ ] **Step 4: Run — verify pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "AssistantQaService|AssistantPrompts|AssistantConversation"` → green.

- [ ] **Step 5: Commit**

```
git add src/LocalScribe.Core/Assistant/AssistantPrompts.cs src/LocalScribe.Core/Assistant/AssistantQaService.cs src/LocalScribe.Core/Assistant/QaScopeFactory.cs tests/
git commit -m "feat(assistant): threaded answer prompt + budget-driven condense-to-recap (PromptVersion 2)"
```

---

### Task 4: Wire the existing chat VM to the active thread

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/AssistantChatViewModel.cs`
- Modify: `src/LocalScribe.App/App.xaml.cs` (the two chat-VM construction sites — session + matter — pass the active thread; grep `new ViewModels.AssistantChatViewModel(` and `new AssistantQaService(`)
- Test: `tests/LocalScribe.App.Tests/AssistantChatViewModelTests.cs` (extend)

**Interfaces:**
- Consumes: `AssistantChatStore.LoadAsync` (v2), `AssistantQaService.AskAsync(question, threadId, …)` (Task 3).
- Produces: the VM exposes the current active thread id (default: the first non-archived thread, creating a "Chat 1" if the log is empty) and renders that thread's turns; `AskAsync` targets it. **No thread selector UI yet** — that is Phase 2. This task only keeps the existing single-panel UI working against v2 with memory + condense.

- [ ] **Step 1: Write the failing tests**

Extend `AssistantChatViewModelTests.cs`:

```csharp
[Fact]
public async Task Loads_the_active_thread_turns_and_appends_there()
{
    // Seed a v2 store with one thread carrying two turns; LoadHistoryAsync renders both;
    // after an ask, the new turn is appended to that same thread on disk.
}

[Fact]
public async Task Empty_store_starts_a_default_thread_on_first_ask()
{
    // No chats.json: the VM asks, and a single "Chat 1" thread with the turn is persisted.
}
```

- [ ] **Step 2: Run — verify failures.**

- [ ] **Step 3: Implement**

In `AssistantChatViewModel`: `LoadHistoryAsync` loads the log, picks the active thread (`Chats.FirstOrDefault(c => !c.Archived)`), stores its id, and renders `thread.Turns`. If the log is empty, defer creating the thread until the first ask (or create it lazily in the service). `AskAsync` calls `_service.AskAsync(question, _activeThreadId, ...)`; if `_activeThreadId` is null (empty store), have the service create a default "Chat 1" (via `AssistantChatStore.NewThread`) and return its id, or create it in the VM before the first ask — pick one and keep it consistent. Update `App.xaml.cs` construction sites so the chat VM and `AssistantQaService` agree on the store and the active-thread source.

Keep everything else (citation nav, InvalidateContext, CancelForRecording, Shutdown, availability) unchanged.

- [ ] **Step 4: Run — verify pass**

Run: `dotnet test tests/LocalScribe.App.Tests --filter AssistantChatViewModel` → green.

- [ ] **Step 5: Commit**

```
git add src/LocalScribe.App/ViewModels/AssistantChatViewModel.cs src/LocalScribe.App/App.xaml.cs tests/LocalScribe.App.Tests/AssistantChatViewModelTests.cs
git commit -m "feat(assistant): chat VM loads/appends against the active thread (v2 store, memory + condense behind existing UI)"
```

---

### Task 5: Phase-1 gate + real-model smoke

**Files:** none (verification only).

- [ ] **Step 1: Full gate**

```
dotnet build F:\LocalScribe\LocalScribe.slnx      # 0 warnings
dotnet test F:\LocalScribe\tests\LocalScribe.Core.Tests
dotnet test F:\LocalScribe\tests\LocalScribe.App.Tests
```

Expected: build 0/0; Core green except the 2 known fixture fails; App green except the known `Stop_upserts...` flake.

- [ ] **Step 2: Real-model smoke (behind the existing Session Details Assistant tab)**

With the helper deployed (`assistant\` beside the App) and the model installed, open a session's chat and:
- Ask a question, then a **follow-up that needs memory** ("and who agreed to that?") — the answer reflects the prior turn.
- Confirm the persisted `chats.json` is now v2 with a named thread.
- (If feasible) drive a long thread to trigger a condense; confirm the answer stays coherent and `Recap` becomes non-empty on disk.

Record the transcript in the task report. GUI thread-selector behavior is Phase 2 and out of scope here.

- [ ] **Step 3: Finish**

Use superpowers:finishing-a-development-branch (merge choice is the user's).

---

## Self-Review (plan-time)

- **Spec coverage (Phases 0-1):** action-bar bug → T0; `chats.json` v2 + migration → T1; `AssistantConversation` memory → T2; threaded prompt + condense/recap + `PromptVersion` bump → T3; wire behind existing UI → T4; gate + smoke → T5. Phases 2-4 (panel relocation, matters, overview) are explicitly deferred to later plans.
- **Placeholder scan:** none — each task carries concrete test code and the store/prompt/condense implementations; the one deliberately-specified-not-coded piece is the condense loop wiring in T3, pinned by four behavioral tests and an explicit algorithm.
- **Type consistency:** `AssistantChatThread`/`AssistantChatLog`/`AssistantChatStore.{Version,NewThread,MigratedThreadName,LoadAsync,SaveAsync}`, `AssistantConversation.BuildHistoryBlock`, `AssistantPrompts.{BuildAnswerPrompt(4-arg),BuildRecapPrompt,PromptVersion=2}`, `AssistantQaService.AskAsync(question, threadId, chunks, ct)` — used consistently across T1-T4.
- **Risk carried from the spec:** condense latency and budget correctness live in T3; the migration-is-load-bearing risk lives in T1 (pure, tested, load-only until first write).
