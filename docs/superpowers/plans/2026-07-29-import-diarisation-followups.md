# Import-time Speaker-Detection Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the four deferred, non-blocking follow-ups from the import-time speaker-detection round: fix the hydrated-row name-revert, auto-select a hydrated source, add the 2-channel downmix marker e2e test, and sweep stale comments.

**Architecture:** Follow-up 1 centralises the read view's owner-then-overlay name precedence in one new pure-Core `NameResolver.ResolveClusterName` helper, and seeds hydrated Split Speakers rows through it so the dialog can never drift from what the transcript renders. Follow-ups 2-4 are narrow, localised changes.

**Tech Stack:** .NET 10 (`net10.0-windows`), WPF + CommunityToolkit.Mvvm, xunit 2.9.3, hand-written fakes only.

**Spec:** `docs/superpowers/specs/2026-07-29-import-diarisation-followups-design.md` (committed @ `2e02d84`)

## Global Constraints

- **Evidentiary rules (locked):** never delete or rewrite transcript content; degradation is never silent; the commit path never touches audio for any `AudioRetention`; the app never auto-assigns a name from a voiceprint match.
- **New/extended VM tests MUST use the queued dispatch fake** (`QueuedDispatch` with `PumpOne`/`Pump`, already present in `SplitSpeakersHydrationTests.cs:33-44`), never the synchronous `a => a()`. A synchronous fake masks the `BeginInvoke` stamp-ordering bug the assistant-surfaces round shipped.
- **Never `System.Progress<T>`** in a VM or a test.
- **No Unicode emojis** in any test or tool script.
- **Test framework is xunit 2.9.3 with `Assert.*` only.** No Moq/NSubstitute/FluentAssertions. Every double is a hand-written `Fake*`. `tests/LocalScribe.App.Tests` has **no** global `using Xunit;` — files write it explicitly (the target files already do).
- **Build must be 0 warnings.** Run `dotnet clean LocalScribe.slnx` before the final 0-warning check — analyser warnings (e.g. xUnit1031) only surface after a clean build.
- **Test commands.** Core: `dotnet test tests/LocalScribe.Core.Tests --filter "Category!=Fixture" 2>&1 | tail -6` (a fresh worktree has no `models/`; unfiltered Core has ~7 expected fixture failures). App/Mcp run unfiltered and must be fully green. Per-task: add `--filter "FullyQualifiedName~XxxTests"`.
- **Known flaky (pre-existing, not a regression):** `SessionsPageViewModelTests.Stop_upserts_the_just_stopped_row_as_Finalizing_without_a_reset` (collection-modified race). Re-run before treating as a regression.
- **Baseline (measured in this worktree @ `2e02d84`):** build 0-warn; Core (non-fixture) 1007/1007; App 815/815; Mcp 6/6.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `src/LocalScribe.Core/Projection/NameResolver.cs` | New public `ResolveClusterName` helper; `ResolveClusterKey` delegates to it (behaviour-preserving) | 1 |
| `tests/LocalScribe.Core.Tests/NameResolverTests.cs` | Precedence tests for `ResolveClusterName` | 1 |
| `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs` | Seed hydrated row `Name` via `ResolveClusterName`; auto-select hydrated sources after `HydrateClusters` | 2, 3 |
| `tests/LocalScribe.App.Tests/SplitSpeakersHydrationTests.cs` | The rename-revert repro + the auto-select tests | 2, 3 |
| `tests/LocalScribe.Core.Tests/AudioImporterTests.cs` | 2-channel downmix marker e2e `[Fact]` | 4 |
| `src/LocalScribe.App/App.xaml.cs` | Comment sweep (rename + line-ref rot) | 5 |

---

### Task 1: `NameResolver.ResolveClusterName` — shared owner-then-overlay resolver

**Files:**
- Modify: `src/LocalScribe.Core/Projection/NameResolver.cs:63-75`
- Test: `tests/LocalScribe.Core.Tests/NameResolverTests.cs` (append methods)

**Interfaces:**
- Consumes: nothing.
- Produces: `public static string? NameResolver.ResolveClusterName(string clusterKey, Speakers? speakers, SessionMeta meta)` — returns the owning Named participant's name, else the `speakers.Names` overlay value, else `null` (no derived `"Speaker N"` fallback). Task 2 calls exactly this.

**Context you need:** `ResolveClusterKey` (`NameResolver.cs:63-75`) already resolves a clusterKey as owner (tier 1a) → overlay (tier 1b) → derived `"Speaker " + clusterId`. This task extracts tiers 1a+1b into a public null-returning helper and has `ResolveClusterKey` delegate to it. The derived fallback must stay **inside** `ResolveClusterKey`: `DefaultSpeakerLabels.For` is side-prefixed and 1-based (`"Local Speaker 1"`), a different string from the resolver's 0-based `"Speaker 0"`, so a hydrated row must keep its own default rather than adopt the derived label. `NameResolverTests` already imports `LocalScribe.Core.Projection` and has `Meta(int local, int remote, params SessionParticipant[] ps)`. `SessionParticipant` has init members `Id`, `Name`, `Side` (`SourceKind`), `ClusterKey` (`string?`), `Kind` (`ParticipantKind`, default `Named`).

- [ ] **Step 1: Write the failing tests**

Append to the `NameResolverTests` class in `tests/LocalScribe.Core.Tests/NameResolverTests.cs`:

```csharp
    [Fact]
    public void ResolveClusterName_prefers_the_owner_over_the_overlay()
    {
        // design 2026-07-29 follow-up 1: a Named slot owns Local:0 and was renamed after diarisation,
        // so meta.Name diverges from the speakers.json overlay. The owner tier must win - exactly as
        // ResolveClusterKey (and therefore the read view) resolves it.
        var speakers = new Speakers
        { Names = new Dictionary<string, string> { ["Local:0"] = "Sarah Chen" } };
        var meta = Meta(2, 1, new SessionParticipant
        { Id = "p1", Name = "Sarah Chen-Smith", Side = SourceKind.Local, ClusterKey = "Local:0" });

        Assert.Equal("Sarah Chen-Smith", NameResolver.ResolveClusterName("Local:0", speakers, meta));
    }

    [Fact]
    public void ResolveClusterName_falls_back_to_the_overlay_when_no_participant_owns_the_key()
    {
        var speakers = new Speakers
        { Names = new Dictionary<string, string> { ["Local:0"] = "Sarah Chen" } };
        Assert.Equal("Sarah Chen", NameResolver.ResolveClusterName("Local:0", speakers, Meta(2, 1)));
    }

    [Fact]
    public void ResolveClusterName_is_null_when_neither_tier_supplies_a_name()
    {
        // No owner and no overlay entry: the caller keeps its OWN default (DefaultSpeakerLabels),
        // never the derived "Speaker N", so this returns null rather than a string.
        Assert.Null(NameResolver.ResolveClusterName("Local:0", speakers: null, Meta(2, 1)));
        Assert.Null(NameResolver.ResolveClusterName("Local:0",
            new Speakers { Names = new Dictionary<string, string>() }, Meta(2, 1)));
    }

    [Fact]
    public void ResolveClusterName_ignores_a_blank_named_owner_and_uses_the_overlay()
    {
        // Mirrors ResolveClusterKey tier 1a: only a Named slot with a NON-EMPTY name owns a cluster.
        var meta = Meta(2, 1, new SessionParticipant
        { Id = "p1", Name = "", Side = SourceKind.Local, ClusterKey = "Local:0" });
        var speakers = new Speakers
        { Names = new Dictionary<string, string> { ["Local:0"] = "Sarah Chen" } };
        Assert.Equal("Sarah Chen", NameResolver.ResolveClusterName("Local:0", speakers, meta));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~NameResolverTests" 2>&1 | tail -6`
Expected: FAIL — compile error, `ResolveClusterName` does not exist.

- [ ] **Step 3: Write the implementation**

In `src/LocalScribe.Core/Projection/NameResolver.cs`, replace the `ResolveClusterKey` method (lines 63-75) with:

```csharp
    /// <summary>The owner-then-overlay display name for a clusterKey, or null when neither tier
    /// supplies one (design 2026-07-29 follow-up 1). Public so Split Speakers hydration seeds a
    /// row's editable name from the SAME precedence the read view renders, instead of the raw
    /// speakers.json overlay - otherwise a participant renamed in Session Details after diarisation
    /// shows a stale name in the dialog and a Confirm silently reverts the transcript. Deliberately
    /// WITHOUT the "Speaker N" derived fallback: a hydrated row keeps its own DefaultSpeakerLabels
    /// default (side-prefixed, 1-based), a different string from the 0-based derived label below.</summary>
    public static string? ResolveClusterName(string clusterKey, Speakers? speakers, SessionMeta meta)
    {
        SessionParticipant? owner = meta.Participants.FirstOrDefault(p =>
            p.ClusterKey == clusterKey
            && p.Kind == ParticipantKind.Named
            && !string.IsNullOrEmpty(p.Name));
        if (owner is not null) return owner.Name;

        if (speakers is not null && speakers.Names.TryGetValue(clusterKey, out string? named)) return named;
        return null;
    }

    // 1a) ownership (Stage 5.4 section 5.2): a NAMED slot durably owns the detected voice
    // bound to it - its meta.json Name wins over the speakers.json overlay, so renaming the
    // slot relabels its lines WITHOUT rewriting speakers.json. An Unnamed owner has an empty
    // Name by design and falls through: the design renders unnamed slots "Speaker N", which
    // is exactly the overlay/derived tiers below.
    // 1b) speakers.json name overlay, else the derived per-cluster label.
    // Extracted verbatim so a split-child clusterKey override resolves the same way.
    private static string ResolveClusterKey(string clusterKey, Speakers? speakers, SessionMeta meta)
    {
        if (ResolveClusterName(clusterKey, speakers, meta) is { } name) return name;
        int colon = clusterKey.IndexOf(':');
        string clusterId = colon >= 0 ? clusterKey[(colon + 1)..] : clusterKey;
        return "Speaker " + clusterId;
    }
```

(The 1a/1b comment block that previously sat above `ResolveClusterKey` moves with it as shown; do not duplicate it.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~NameResolverTests" 2>&1 | tail -6`
Expected: PASS (the 4 new tests plus every pre-existing `NameResolverTests` case — the refactor is behaviour-preserving).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.Core/Projection/NameResolver.cs tests/LocalScribe.Core.Tests/NameResolverTests.cs
git commit -F - <<'EOF'
feat(projection): NameResolver.ResolveClusterName owner-then-overlay helper

Extracts the read view's tier-1a (participant ownership) and tier-1b
(speakers.json overlay) precedence into a public null-returning helper;
ResolveClusterKey delegates to it then appends the derived "Speaker N"
fallback. Behaviour-preserving. Split Speakers hydration will seed rows
through this so the dialog cannot drift from what the transcript renders.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YNvncusnYQA95oayXbvAyB
EOF
```

---

### Task 2: Seed hydrated rows from the effective name (fixes the rename-revert)

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs` (using block ~line 2-10; `HydrateClusters` call site ~:496; `HydrateClusters` signature ~:506; the name seed ~:553-558)
- Test: `tests/LocalScribe.App.Tests/SplitSpeakersHydrationTests.cs` (append one method + one using)

**Interfaces:**
- Consumes: `NameResolver.ResolveClusterName` (Task 1).
- Produces: nothing later tasks depend on.

**Context you need:** `HydrateClusters` today takes `(Speakers committed, IReadOnlyDictionary<string, VoiceprintSuggestion> suggestions)` and is called once from `Apply` (`:496`) where `loaded.Meta` is in scope. The name seed at `:553-558` reads only `committed.Names`. `NameResolver` lives in `LocalScribe.Core.Projection`, which this VM does **not** yet import (its usings end at `LocalScribe.Core.Storage`). The confirm path (`ConfirmAsync`) matches each row's effective name against that side's Named candidates to build the ownership map; a stale seed matches no candidate and drops ownership (the revert). Seeding from the owner tier makes the seed re-match the candidate. The test harness (`QueuedDispatch`, `MakeFinalizedSession`, `MakeVm`, invocation idiom `LoadAsync(id, default)` → `dispatcher.Pump()` → `Sources[i].Selected = true` → `ConfirmCommand.ExecuteAsync(null)` → `Pump()`) is established in the file.

- [ ] **Step 1: Write the failing test**

Add `using LocalScribe.Core.Projection;` to the using block of `tests/LocalScribe.App.Tests/SplitSpeakersHydrationTests.cs` (after `using LocalScribe.Core.People;`), then append this method to the `SplitSpeakersHydrationTests` class:

```csharp
    [Fact]
    public async Task A_Session_Details_rename_after_diarisation_survives_a_hydrated_confirm()
    {
        // design 2026-07-29 follow-up 1 - the reviewer's deterministic repro. Local was diarised and
        // participant p1 owns Local:0. p1 is then renamed in Session Details, so meta.Name
        // ("Sarah Chen-Smith") diverges from the speakers.json overlay ("Sarah Chen") while ownership
        // (ClusterKey) stays intact - the read view already renders the new name via the owner tier.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 1, retained: [SourceKind.Local], localCount: 2);

        await new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            LocalCount = 2,
            Participants =
            [
                new SessionParticipant
                { Id = "p1", Name = "Sarah Chen-Smith", Side = SourceKind.Local, ClusterKey = "Local:0" },
            ],
        }, default);
        await new SpeakersStore(paths.SpeakersJson(id)).SaveAsync(new Speakers
        {
            Names = new Dictionary<string, string>
            { ["Local:0"] = "Sarah Chen", ["Local:1"] = "Local Speaker 2" },
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Local"] = new() { ["1"] = "Local:0", ["2"] = "Local:1" } },
            DiarisedSources = [SourceKind.Local],
            Method = "sherpa",
            DiarisedAtUtc = DateTimeOffset.UnixEpoch,
        }, default);

        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        // The fix: the hydrated row shows the effective (owner) name, not the stale overlay.
        var row = vm.Clusters.Single(c => c.ClusterKey == "Local:0");
        Assert.Equal("Sarah Chen-Smith", row.Name);
        Assert.Equal(0, engine.Calls);

        // Confirm the untouched hydration. Select manually - auto-select is follow-up 2 (task 3).
        vm.Sources.Single(s => s.Source == SourceKind.Local).Selected = true;
        await vm.ConfirmCommand.ExecuteAsync(null);
        dispatcher.Pump();

        // Ownership re-asserted (not cleared), overlay converged, transcript NOT reverted.
        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal("Local:0", meta!.Participants.Single(p => p.Id == "p1").ClusterKey);
        var sp = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Sarah Chen-Smith", sp!.Names["Local:0"]);
        Assert.Equal("Sarah Chen-Smith", NameResolver.Resolve(
            TranscriptLine.Segment(1, TranscriptSource.Local, 0, 1000, "hi", "Me"), sp, meta));
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~A_Session_Details_rename_after_diarisation_survives_a_hydrated_confirm" 2>&1 | tail -8`
Expected: FAIL at `Assert.Equal("Sarah Chen-Smith", row.Name)` — today the row seeds `"Sarah Chen"` from the raw overlay.

- [ ] **Step 3: Write the implementation**

In `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs`:

1. Add `using LocalScribe.Core.Projection;` to the using block (after `using LocalScribe.Core.People;`).

2. At the `HydrateClusters` call site (`:496`), pass the meta:

```csharp
        if (loaded.Committed is { } committed) HydrateClusters(committed, loaded.Meta, suggestions);
```

3. Change the `HydrateClusters` signature (`:506`) to accept the meta:

```csharp
    private void HydrateClusters(Speakers committed, SessionMeta meta,
        IReadOnlyDictionary<string, VoiceprintSuggestion> suggestions)
```

4. Replace the name seed (currently `:553-558`, the `if (committed.Names.TryGetValue(clusterKey, out string? name) && !string.IsNullOrWhiteSpace(name)) row.Name = name;` block) with:

```csharp
                // Seed from the SAME precedence the read view renders (design 2026-07-29 follow-up 1):
                // NameResolver ranks the participant-ownership tier ABOVE the speakers.json overlay, so
                // a participant renamed in Session Details after diarisation would otherwise show a
                // STALE overlay name here - and confirming that stale row matches no candidate, clears
                // the owner's ClusterKey, and reverts the transcript. A null/blank result leaves the
                // row on its DefaultSpeakerLabels default (never the resolver's 0-based "Speaker N").
                if (NameResolver.ResolveClusterName(clusterKey, committed, meta) is { } seeded
                    && !string.IsNullOrWhiteSpace(seeded))
                    row.Name = seeded;
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~A_Session_Details_rename_after_diarisation_survives_a_hydrated_confirm" 2>&1 | tail -6`
Expected: PASS.

- [ ] **Step 5: Confirm no hydration regression**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SplitSpeakersHydrationTests" 2>&1 | tail -6`
Expected: PASS — the existing hydration tests (no participant owners) resolve to the overlay value exactly as before.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs tests/LocalScribe.App.Tests/SplitSpeakersHydrationTests.cs
git commit -F - <<'EOF'
fix(speakers): hydrated rows show the effective name, not the raw overlay

HydrateClusters seeded a row's editable Name from speakers.Names, but
NameResolver ranks participant ownership above that overlay. After a
Session-Details rename the dialog showed a stale name and a Confirm
matched no candidate, cleared the owner's ClusterKey and reverted the
transcript. Seed via NameResolver.ResolveClusterName so the row re-matches
the candidate and re-asserts ownership. Non-evidentiary; transcript.jsonl
untouched.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YNvncusnYQA95oayXbvAyB
EOF
```

---

### Task 3: Auto-select a source that has hydrated rows

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs` (in `Apply`, immediately after the `HydrateClusters` call at ~:496)
- Test: `tests/LocalScribe.App.Tests/SplitSpeakersHydrationTests.cs` (append methods)

**Interfaces:**
- Consumes: nothing (uses `_assignmentBySource`, populated by hydration).
- Produces: nothing later tasks depend on.

**Context you need:** `SplitSourceOption._selected` defaults to `false` (`:28`) and the load path adds sources unticked (`:392, :396`). `CanConfirm` (`:330`) is `!IsRunning && Clusters.Count > 0 && Sources.Any(s => s.Selected)`, so a hydrated dialog opened to rename leaves Confirm disabled until a box is ticked. `HydrateClusters` populates `_assignmentBySource` for exactly the diarised sources; it is empty on a never-diarised load. Setting `SplitSourceOption.Selected` fires the per-option `PropertyChanged` wired at `:463`, re-notifying the Confirm/Run/ForceCount commands.

- [ ] **Step 1: Write the failing tests**

Append to the `SplitSpeakersHydrationTests` class:

```csharp
    [Fact]
    public async Task Hydration_auto_selects_the_committed_source_so_confirm_is_enabled()
    {
        // design 2026-07-29 follow-up 2: a dialog reopened purely to rename must have Confirm enabled
        // without the user first ticking a source - the whole point of the rename-hydration path.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        await SeedCommittedDiarisationAsync(paths, id);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.True(vm.Sources.Single(s => s.Source == SourceKind.Remote).Selected);
        Assert.True(vm.ConfirmCommand.CanExecute(null));
    }

    [Fact]
    public async Task Only_the_hydrated_source_is_auto_selected_not_a_merely_retained_one()
    {
        // Both legs retained, but only Remote was diarised. Auto-select ticks Remote and leaves Local
        // (offered by the source gate, but with no hydrated rows) unticked.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 2, retained: [SourceKind.Local, SourceKind.Remote], localCount: 2);
        await SeedCommittedDiarisationAsync(paths, id);   // Remote only
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.True(vm.Sources.Single(s => s.Source == SourceKind.Remote).Selected);
        Assert.False(vm.Sources.Single(s => s.Source == SourceKind.Local).Selected);
    }

    [Fact]
    public async Task A_never_diarised_load_selects_no_source_and_leaves_confirm_disabled()
    {
        // No speakers.json committed: hydration builds no rows, nothing to auto-select, Confirm stays
        // disabled (CanConfirm requires Clusters AND a ticked source).
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher);

        await vm.LoadAsync(id, default);
        dispatcher.Pump();

        Assert.All(vm.Sources, s => Assert.False(s.Selected));
        Assert.False(vm.ConfirmCommand.CanExecute(null));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~Hydration_auto_selects_the_committed_source_so_confirm_is_enabled|FullyQualifiedName~Only_the_hydrated_source_is_auto_selected_not_a_merely_retained_one" 2>&1 | tail -8`
Expected: FAIL — sources are unticked after hydration (the never-diarised test already passes; it guards against over-selecting).

- [ ] **Step 3: Write the implementation**

In `src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs`, in `Apply`, immediately after the `HydrateClusters` call line (`if (loaded.Committed is { } committed) HydrateClusters(committed, loaded.Meta, suggestions);`), add:

```csharp
        // Auto-select the sources hydration just built rows for (design 2026-07-29 follow-up 2), so a
        // dialog reopened purely to rename has Confirm enabled without the user first ticking a box.
        // _assignmentBySource is populated only by hydration at load time, so this is exactly the
        // hydrated set and is empty on a never-diarised load (CanConfirm stays false there, as before).
        foreach (var s in Sources)
            if (_assignmentBySource.ContainsKey(s.Source))
                s.Selected = true;
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/LocalScribe.App.Tests --filter "FullyQualifiedName~SplitSpeakersHydrationTests" 2>&1 | tail -6`
Expected: PASS (all hydration tests, including Task 2's).

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/ViewModels/SplitSpeakersViewModel.cs tests/LocalScribe.App.Tests/SplitSpeakersHydrationTests.cs
git commit -F - <<'EOF'
feat(speakers): auto-select hydrated sources so a rename reopen can confirm

A hydrated Split Speakers dialog opened to rename left every source
unticked, so CanConfirm stayed false until the user ticked a box. Tick the
sources hydration built rows for (those in _assignmentBySource). Inert on a
never-diarised load.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YNvncusnYQA95oayXbvAyB
EOF
```

---

### Task 4: End-to-end test for the 2-channel downmix marker

**Files:**
- Test: `tests/LocalScribe.Core.Tests/AudioImporterTests.cs` (append one `[Fact]`)

**Interfaces:**
- Consumes: nothing.
- Produces: nothing.

**Context you need:** This is a **coverage** task — the behaviour already exists (the prior round widened `ChannelMapper` to mark a 2-channel downmix). `AudioImporterTests` runs the real importer against in-memory fakes (`FakeDecoder`, `EchoFactory`, `EnergyProbe`), so no `models/`/ffmpeg are needed. The existing `Multichannel_downmixes_with_a_note_and_no_claim_means_no_gate` (`:332-357`) covers the >2-channel case; this mirrors it for 2 channels. Helpers in the fixture: `WriteBurstWav(name, rate, channels, params int[] toneChannels)`, `MakeImporter(decoder)`, `Request(sourcePath, title, stereo, model, language)`. `MappingLabel` returns `"downmix"` for a 2-channel downmixed plan (`AudioImporter.cs:282`). Because the behaviour already ships, the new test is expected to **pass immediately** — Step 3 mutation-verifies it actually pins the 2-channel case.

- [ ] **Step 1: Write the test**

Append to the `AudioImporterTests` class in `tests/LocalScribe.Core.Tests/AudioImporterTests.cs`:

```csharp
    [Fact]
    public async Task Two_channel_downmix_writes_the_downmixed_marker_end_to_end()
    {
        // design 2026-07-29 follow-up 3. ChannelMapperDownmixMarkerTests pins Plan(2, Downmix).Downmixed
        // and the importer's append path is covered for >2 channels; this pins their COMPOSITION for
        // the 2-channel case - the primary path import-time speaker detection serves.
        string source = Path.Combine(_root, "twoparty.m4a");
        await File.WriteAllBytesAsync(source, new byte[] { 1, 2, 3 });
        var decoder = new FakeDecoder
        {
            DecodedWavPath = WriteBurstWav("decoded-2ch.wav", 16000, 2, 0, 1),
            Probe = new AudioProbeResult { FormatName = "m4a", ClaimedDurationMs = null, ClaimedChannels = 2 },
        };

        string id = await MakeImporter(decoder).ImportAsync(
            Request(source, title: "Two-party", stereo: StereoMapping.Downmix),
            progress: null, _ => Task.FromResult(true), CancellationToken.None);

        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("downmix", session!.ImportedSource!.ChannelMapping);
        Assert.Equal(2, session.ImportedSource.DecodedChannels);
        Assert.Equal([SourceKind.Local], session.Sources);            // one downmixed leg, not a split
        var lines = await new TranscriptStore(_paths.TranscriptJsonl(id)).ReadAllAsync(default);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker
            && l.Text == string.Format(Markers.ImportedDownmixed, 2));
    }
```

- [ ] **Step 2: Run the test to verify it passes**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~Two_channel_downmix_writes_the_downmixed_marker_end_to_end" 2>&1 | tail -6`
Expected: PASS (the behaviour already ships).

- [ ] **Step 3: Mutation-verify the test is meaningful**

Temporarily change `src/LocalScribe.Core/Import/ChannelMapper.cs` single-leg branch from `Downmixed: decodedChannels > 1` back to `Downmixed: decodedChannels > 2`, re-run the test, and confirm it now **FAILS** (no marker for 2 channels). Then revert the mutation and confirm PASS again. Do not commit the mutation.

Run (after revert): `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~ChannelMapper|FullyQualifiedName~Two_channel_downmix" 2>&1 | tail -6`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/LocalScribe.Core.Tests/AudioImporterTests.cs
git commit -F - <<'EOF'
test(import): e2e assert a 2-channel downmix writes the ImportedDownmixed line

ChannelMapper is unit-tested and the marker append path is covered for >2
channels; their composition for the 2-channel case - the primary path
import-time speaker detection serves - was untested. Mutation-verified
against reverting the > 1 condition to > 2.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YNvncusnYQA95oayXbvAyB
EOF
```

---

### Task 5: Sweep stale comments in `App.xaml.cs`

**Files:**
- Modify: `src/LocalScribe.App/App.xaml.cs` (two comment regions, found by content)

**Interfaces:**
- Consumes: nothing.
- Produces: nothing. Comment-only, zero behaviour change.

**Context you need:** Two comments rotted during the import round. Find them by content (line numbers below are approximate and may drift):

1. Around the import-start re-check (~:628-631): the phrase **"importBusy is still null here"** — the captured local was renamed to `importLane.BusyReason` (an `ImportLaneState` field). The related comment above the lane setup (~:597) also refers to `importBusy` being "set/cleared by the runner wrapper".
2. Around the shared engine-gate declaration (~:236-239): a comment citing **`App.xaml.cs:602`**, **`:605`**, and **`:581`** as the locations of the `Controller.State` check, the `ExternalEngineBusy` func check, and the import chain. Those numbers have moved.

- [ ] **Step 1: Read the exact current comment text**

Run: `grep -n "importBusy\|:602\|:605\|:581" src/LocalScribe.App/App.xaml.cs`
Then Read each region to capture the exact surrounding lines before editing (the edits must match current text exactly).

- [ ] **Step 2: Fix comment 1 — the `importBusy` rename**

Replace `importBusy is still null here` with `importLane.BusyReason is still null here` in the import-start re-check comment. If the ~:597 comment still says `` `importBusy` is set/cleared by the runner wrapper ``, update it to `` `importLane.BusyReason` is set/cleared by the runner wrapper ``. Leave any comment that is explicitly a historical note (e.g. "used to be a plain captured local") accurate — reword only the name if it reads as the current field.

- [ ] **Step 3: Fix comment 2 — the shifted line references**

Reword the engine-gate comment so it names symbols instead of line numbers, e.g.:

```csharp
        // One-engine-at-a-time for the sherpa diarisation lane (design 2026-07-28 adjacent fix 3).
        // Covers BOTH directions the codebase keeps separate: a live recording is Controller.State
        // (the import-start re-check below), while another offline owner is the ExternalEngineBusy
        // func this chains over. Read live on every call, so a lane registered later (the import
        // lane's importLane.BusyReason) is included automatically. Declared here - before settingsVm
        // AND before openSplitSpeakers further down - because both construction sites need it and a
        // lambda cannot reference a local declared later in the same method.
```

Match the existing comment's exact leading whitespace and any `//` continuation style; change wording only.

- [ ] **Step 4: Verify no behaviour change and a clean build**

Run: `git diff --stat src/LocalScribe.App/App.xaml.cs` (expect only comment-line changes) and `dotnet build LocalScribe.slnx 2>&1 | tail -3`
Expected: build succeeds, 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add src/LocalScribe.App/App.xaml.cs
git commit -F - <<'EOF'
chore(app): sweep stale import-lane comments

The captured import-busy local was renamed to importLane.BusyReason, and
the engine-gate comment cited line numbers (:602/:605/:581) that have since
moved. Fix the rename reference and replace line refs with symbol names so
they stop rotting. Comment-only.

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01YNvncusnYQA95oayXbvAyB
EOF
```

---

## Final gate (after all tasks, before the whole-branch review)

- [ ] `dotnet clean LocalScribe.slnx` then `dotnet build LocalScribe.slnx` — 0 Warnings, 0 Errors.
- [ ] `dotnet test tests/LocalScribe.Core.Tests --filter "Category!=Fixture"` — expect 1012/1012 (baseline 1007 + 4 new `NameResolver` tests + 1 new `AudioImporter` test). Confirm no non-fixture regressions.
- [ ] `dotnet test tests/LocalScribe.App.Tests` — expect 819/819 (baseline 815 + 1 rename-revert + 3 auto-select). Re-run once if the known `SessionsPageViewModelTests` flake appears.
- [ ] `dotnet test tests/LocalScribe.Mcp.Tests` — 6/6.
- [ ] Whole-branch review (opus) over `69adffd..HEAD`.

## Self-review notes

- **Spec coverage:** follow-up 1 → Tasks 1-2; follow-up 2 → Task 3; follow-up 3 → Task 4; follow-up 4 → Task 5. All four covered.
- **Type consistency:** `ResolveClusterName(string, Speakers?, SessionMeta) -> string?` is defined in Task 1 and consumed identically in Task 2. `HydrateClusters(Speakers, SessionMeta, IReadOnlyDictionary<string, VoiceprintSuggestion>)` signature and call site edited together in Task 2. `_assignmentBySource` (Task 3) is the field hydration populates.
- **No placeholders:** every code step carries runnable code; Task 5's edits are found-by-content with exact target phrases and a build check.
