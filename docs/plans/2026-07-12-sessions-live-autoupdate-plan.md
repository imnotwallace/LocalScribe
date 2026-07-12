# Sessions List Live Auto-Update Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Sessions manager list update itself when a just-stopped recording's background finalize completes (and per startup-recovery completion), without a manual Refresh and without disturbing scroll position or selection.
**Architecture:** Core raises one `SessionFinalizeCompleted` event from `FinalizeInBackgroundAsync`'s `finally` and exposes `FinalizingSessionId`; the App VM gains a non-disruptive `UpsertRowAsync` that does an in-place `ObservableCollection` Replace/Insert/Remove (never Reset) instead of the `ApplyFilters` `Rows.Clear()`; `App.xaml.cs` wires the completion event and a per-recovery callback to `UpsertRowAsync`, and a pending row now shows "Finalizing…" vs "Recovering…".
**Tech Stack:** C#/.NET 10, WPF, CommunityToolkit.Mvvm, Wpf.Ui, xUnit.

## Global Constraints
- Target branch: `feat/sessions-live-autoupdate` (the design spec `docs/plans/2026-07-12-sessions-live-autoupdate-design.md` is already committed there).
- 0-warning build gate must hold.
- Tests: xUnit. Run a filtered test with: `dotnet test "<testproj.csproj>" --filter "FullyQualifiedName~<Name>" --nologo`
- IMPORTANT: the LocalScribe app may be running and LOCK its bin DLL/exe (MSB3027 copy error — NOT a compile error). When that happens, build/test to an isolated output so the lock is avoided: append `-p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\` to the dotnet test command. Never kill the user's app. (Every `dotnet test` command below already appends this flag.)
- Never use Unicode emojis in test code or scripts (project rule).
- Test projects: App = `tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj`; Core = `tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj`. App.Tests does NOT project-reference Core.Tests — it `<Compile Include>`-links `LiveTestDoubles.cs` (which contains `GatedEngineFactory`, `FakeProvider`, `LiveTestDoubles.MakeController/Options`), so those doubles compile INTO the App.Tests assembly and their `internal` members are directly usable there.
- Commit messages follow the repo style: `fix(app)`/`feat(app)`/`test(app)`/`docs(...)`. Every commit message MUST end with these two trailer lines EXACTLY:
```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW
```

---

### Task 1: Core — `SessionFinalizeCompleted` event + `FinalizingSessionId`
**Files:**
- Modify `src\LocalScribe.Core\Live\SessionController.cs` (add `FinalizingSessionId` after line 142; add the event after line 155; raise it in the `finally` at lines 975–981).
- Test `tests\LocalScribe.Core.Tests\SessionControllerTests.cs` (add two `[Fact]`s to the existing class).

**Interfaces:**
- Produces: `public event Action<string>? SessionController.SessionFinalizeCompleted;` (fires once per finalize, success or failure, passing the session id, AFTER `_finalizing` is cleared). `public string? SessionController.FinalizingSessionId => _finalizing?.Id;`.
- Consumes: existing `_finalizing` (volatile `Session?`), `FinalizeInBackgroundAsync`, `PendingFinalize`, and the `GatedEngineFactory` test double (same assembly).

Steps:
- [ ] **Write the failing tests.** Append these two facts inside `SessionControllerTests` (before the closing brace) in `tests\LocalScribe.Core.Tests\SessionControllerTests.cs`:
```csharp
    [Fact]
    public async Task SessionFinalizeCompleted_fires_once_and_FinalizingSessionId_tracks_the_inflight_id()
    {
        // GatedEngineFactory holds the worker build closed, so FinalizeInBackgroundAsync parks at
        // `await s.WorkerLoop` after Stop returns Idle - the window in which _finalizing is set.
        var gated = new GatedEngineFactory();
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        Assert.Null(c.FinalizingSessionId);

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);
        Assert.Null(c.FinalizingSessionId);                       // recording, not finalizing

        var completed = new List<string>();
        c.SessionFinalizeCompleted += cid => { lock (completed) completed.Add(cid); };

        clock.ElapsedMs = 5000;
        string? stopped = await c.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(id, stopped);
        Assert.Equal(SessionState.Idle, c.State);
        Assert.Equal(id, c.FinalizingSessionId);                  // finalize in flight (gated)
        Assert.False(c.PendingFinalize.IsCompleted);

        gated.CreateGate.Set();                                   // let the finalize drain + persist
        await c.PendingFinalize.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(new[] { id }, completed.ToArray());          // fired exactly once, with the id
        Assert.Null(c.FinalizingSessionId);                       // cleared after completion
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.NotNull(record!.EndedAtUtc);                       // clean finalize wrote EndedAtUtc
    }

    [Fact]
    public async Task SessionFinalizeCompleted_fires_once_on_a_failed_finalize()
    {
        // Force PersistFinalAsync/the writer drain to fail: make transcript.jsonl a DIRECTORY (the
        // same fault mechanism MaintenanceServiceTests uses) while the finalize is gated, so the
        // background drain throws and FinalizeInBackgroundAsync takes its FINALIZE_FAILED catch - the
        // event must STILL fire once from the finally, and EndedAtUtc is never written.
        var gated = new GatedEngineFactory();
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        var errors = new List<string>();
        c.ErrorRaised += e => { lock (errors) errors.Add(e); };
        var completed = new List<string>();
        c.SessionFinalizeCompleted += cid => { lock (completed) completed.Add(cid); };

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);
        clock.ElapsedMs = 5000;
        string? stopped = await c.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(id, stopped);
        Assert.Equal(id, c.FinalizingSessionId);

        Directory.CreateDirectory(paths.TranscriptJsonl(id!));    // a dir where a file must be -> writes throw
        gated.CreateGate.Set();
        await c.PendingFinalize.WaitAsync(TimeSpan.FromSeconds(10));   // never throws to the awaiter

        Assert.Contains("FINALIZE_FAILED", errors);
        Assert.Equal(new[] { id }, completed.ToArray());          // fires once even on the failure path
        Assert.Null(c.FinalizingSessionId);
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionFinalizeCompleted" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\` — expected: `error CS1061: 'SessionController' does not contain a definition for 'SessionFinalizeCompleted'` (and `FinalizingSessionId`).
- [ ] **Add `FinalizingSessionId`.** In `SessionController.cs`, immediately after line 142 (`public string? CurrentSessionId => _session?.Id;`) insert:
```csharp

    /// <summary>The id of the session whose transcription tail is still draining on the background
    /// finalizer (Fix 2026-07-08 / design 2026-07-12 section 1), or null when no finalize is in
    /// flight. Set at Stop before the Idle transition; cleared in FinalizeInBackgroundAsync's finally
    /// just before SessionFinalizeCompleted. A benign cross-thread read (cosmetic label only).</summary>
    public string? FinalizingSessionId => _finalizing?.Id;
```
- [ ] **Add the event.** In `SessionController.cs`, immediately after line 155 (`public event Action<string>? Notice;`) insert:
```csharp
    /// <summary>Fires exactly once when a session's background finalize settles on disk (design
    /// 2026-07-12 section 1) - on BOTH the success path (PersistFinalAsync wrote EndedAtUtc) and the
    /// failure path (FINALIZE_FAILED, EndedAtUtc never written). Passes the session id; raised AFTER
    /// _finalizing is cleared, so a handler re-reading FinalizingSessionId sees null. The Sessions
    /// list re-reads disk truth on it, so one event covers both outcomes.</summary>
    public event Action<string>? SessionFinalizeCompleted;
```
- [ ] **Raise it in the finally.** In `FinalizeInBackgroundAsync` replace the finally body (lines 977–980) so the invoke runs after `_finalizing` is cleared:
```csharp
                // The tail is drained (WriterLoop completed above, so every LineInserted has fired). Stop
                // resolving View to this session - a later idle read must return empty. Only clear our own
                // reference: a new session may already have started and set _finalizing for itself.
                if (ReferenceEquals(_finalizing, s)) _finalizing = null;
                // Sessions-list live auto-update (design 2026-07-12): the row is settled on disk now
                // (success => EndedAtUtc written; failure => still un-ended). Raise AFTER the clear above
                // so FinalizingSessionId reads null in the handler. Wrap like Fix #6: a throwing
                // subscriber must never fault the unobserved _pendingFinalize task.
                try { SessionFinalizeCompleted?.Invoke(s.Id); } catch { }
```
- [ ] **Run tests and see PASS.** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionFinalizeCompleted" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\` — expected: 2 passed. Then run the whole class to prove no regression: `--filter "FullyQualifiedName~SessionControllerTests"`.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerTests.cs
git commit -m "feat(core): SessionFinalizeCompleted event + FinalizingSessionId for live sessions-list update

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW"
```

---

### Task 2: `SessionViewModel.FinalizingSessionId` passthrough
**Files:**
- Modify `src\LocalScribe.App\ViewModels\SessionViewModel.cs` (add a property after line 82).
- Test `tests\LocalScribe.App.Tests\SessionViewModelTests.cs` (add one `[Fact]`).

**Interfaces:**
- Consumes: `SessionController.FinalizingSessionId` (Task 1).
- Produces: `public string? SessionViewModel.FinalizingSessionId => _controller.FinalizingSessionId;` — the read `SessionsPageViewModel` (Task 4/5) uses off its `_session`.

Steps:
- [ ] **Write the failing test.** Append inside `SessionViewModelTests` (before the closing brace) in `tests\LocalScribe.App.Tests\SessionViewModelTests.cs`:
```csharp
    [Fact]
    public async Task FinalizingSessionId_surfaces_the_controllers_inflight_id()
    {
        // GatedEngineFactory is linked into App.Tests via LiveTestDoubles.cs, so it is usable here.
        var gated = new GatedEngineFactory();
        var (controller, _, _, clock) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());

        Assert.Null(vm.FinalizingSessionId);
        await vm.StartCommand.ExecuteAsync(null);
        string id = controller.CurrentSessionId!;
        Assert.Null(vm.FinalizingSessionId);                 // recording

        clock.ElapsedMs = 5000;
        await vm.StopCommand.ExecuteAsync(null);             // returns Idle; finalize gated
        Assert.Equal(id, vm.FinalizingSessionId);            // mirrors the controller

        gated.CreateGate.Set();
        await controller.PendingFinalize;
        Assert.Null(vm.FinalizingSessionId);                 // cleared after completion
        vm.Dispose();
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~FinalizingSessionId_surfaces" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\` — expected: `error CS1061: 'SessionViewModel' does not contain a definition for 'FinalizingSessionId'`.
- [ ] **Implement the passthrough.** In `SessionViewModel.cs`, immediately after line 82 (`public string? CurrentSessionId => _controller.CurrentSessionId;`) insert:
```csharp
    /// <summary>The id of the session whose background finalize is still draining after Stop (design
    /// 2026-07-12 section 1): surfaces <see cref="SessionController.FinalizingSessionId"/> so the
    /// Sessions list can label the just-stopped row "Finalizing..." and upsert it in place. Null
    /// except between a clean Stop and its SessionFinalizeCompleted.</summary>
    public string? FinalizingSessionId => _controller.FinalizingSessionId;
```
- [ ] **Run tests and see PASS.** Same filter as above — expected: 1 passed.
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/SessionViewModel.cs tests/LocalScribe.App.Tests/SessionViewModelTests.cs
git commit -m "feat(app): surface FinalizingSessionId through SessionViewModel

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW"
```

---

### Task 3: `SessionRowViewModel` — `isFinalizing` param + `IsFinalizing`/`IsRecovering`
**Files:**
- Modify `src\LocalScribe.App\ViewModels\SessionRowViewModel.cs` (ctor signature at lines 51–52; add two properties near line 46; set `IsFinalizing` in the ctor body near line 77).
- Test `tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs` (add one `[Fact]` using the in-file `Rec`/`Meta` helpers).

**Interfaces:**
- Produces: 4th optional ctor param `bool isFinalizing = false`; `public bool IsFinalizing { get; }`; `public bool IsRecovering => IsPendingRecovery && !IsFinalizing;`. The new param is optional, so the 2-arg (`SessionRowSourceTests.cs:39`, `MetadataEditorViewModel.cs:302`) and 3-arg (`SessionRowMatterChipsTests.cs:37`) call sites keep compiling unchanged.
- Consumes: existing `IsPendingRecovery` (`= session.EndedAtUtc is null`).

Steps:
- [ ] **Write the failing test.** Append inside `SessionsPageViewModelTests` (before the closing brace) in `tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs`:
```csharp
    [Fact]
    public void Row_labels_split_finalizing_from_recovering()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var finalizing = new SessionRowViewModel(
            new SessionListItem("s-fin", Rec("s-fin", t, 480, ended: false), Meta("Pending")),
            TimeProvider.System, matterLookup: null, isFinalizing: true);
        Assert.True(finalizing.IsPendingRecovery);
        Assert.True(finalizing.IsFinalizing);
        Assert.False(finalizing.IsRecovering);
        Assert.Equal("", finalizing.DurationDisplay);

        var recovering = new SessionRowViewModel(
            new SessionListItem("s-rec", Rec("s-rec", t, 480, ended: false), Meta("Pending")),
            TimeProvider.System, matterLookup: null, isFinalizing: false);
        Assert.True(recovering.IsPendingRecovery);
        Assert.False(recovering.IsFinalizing);
        Assert.True(recovering.IsRecovering);

        var finalized = new SessionRowViewModel(
            new SessionListItem("s-done", Rec("s-done", t, 480), Meta("Done")),
            TimeProvider.System, matterLookup: null, isFinalizing: false);
        Assert.False(finalized.IsPendingRecovery);
        Assert.False(finalized.IsFinalizing);
        Assert.False(finalized.IsRecovering);
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~Row_labels_split" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\` — expected: `error CS1739`/`CS1061` on the `isFinalizing:` argument and `IsFinalizing`/`IsRecovering`.
- [ ] **Widen the ctor signature.** In `SessionRowViewModel.cs` replace lines 51–52:
```csharp
    public SessionRowViewModel(SessionListItem item, TimeProvider time,
        IReadOnlyDictionary<string, (string? Reference, string Name)>? matterLookup = null,
        bool isFinalizing = false)
```
- [ ] **Add the two properties.** In `SessionRowViewModel.cs`, immediately after line 46 (`public bool IsPendingRecovery { get; }`) insert:
```csharp
    /// <summary>True only for the just-stopped session whose background finalize is still in flight
    /// (design 2026-07-12 section 4): the row exists on disk with EndedAtUtc still null, but this is a
    /// normal post-Stop finalize, not a crash recovery. Drives the "Finalizing..." chip.</summary>
    public bool IsFinalizing { get; }
    /// <summary>The "Recovering..." chip condition: pending on disk AND not the in-flight finalize.
    /// Splitting it off IsPendingRecovery keeps every existing gate (archive/open/export/delete) that
    /// reads IsPendingRecovery working unchanged while the chip no longer mislabels a normal finalize.</summary>
    public bool IsRecovering => IsPendingRecovery && !IsFinalizing;
```
- [ ] **Set `IsFinalizing` in the ctor.** In `SessionRowViewModel.cs`, immediately after line 77 (`IsPendingRecovery = session.EndedAtUtc is null;`) insert:
```csharp
        IsFinalizing = isFinalizing;
```
- [ ] **Run tests and see PASS.** Same filter — expected: 1 passed. Also run `--filter "FullyQualifiedName~SessionRowMatterChipsTests|FullyQualifiedName~SessionRowSourceTests"` to confirm the untouched 2-/3-arg call sites still build+pass.
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/SessionRowViewModel.cs tests/LocalScribe.App.Tests/SessionsPageViewModelTests.cs
git commit -m "feat(app): SessionRowViewModel IsFinalizing/IsRecovering split for the pending chip

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW"
```

---

### Task 4: `SessionsPageViewModel.UpsertRowAsync` + `PassesFilters` (in-place, no Reset)
**Files:**
- Modify `src\LocalScribe.App\ViewModels\SessionsPageViewModel.cs`: refactor `ApplyFilters` (lines 177–198) to use a new `PassesFilters`; thread `isFinalizing` into the `LoadAsync` row build (lines 159–163) and the `RefreshRowAsync` row build (line 367); add `UpsertRowAsync` + helpers at the end of the class (after `RefreshRowAsync`, line 374).
- Test `tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs` (add three `[Fact]`s).

**Interfaces:**
- Consumes: `SessionViewModel.FinalizingSessionId` (Task 2) via `_session`; `SessionRowViewModel(..., isFinalizing:)`, `IsFinalizing`/`IsRecovering` (Task 3); existing `MaintenanceService.LoadSessionItemAsync`, `_dispatch`, `_all`, `Rows`, `RebuildMatterOptions`, `SelectedRow`.
- Produces: `public Task UpsertRowAsync(string sessionId)`; `private bool PassesFilters(SessionRowViewModel)`.

Steps:
- [ ] **Write the failing tests.** Append these three facts inside `SessionsPageViewModelTests` in `tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs`:
```csharp
    [Fact]
    public async Task UpsertRowAsync_replaces_existing_row_without_reset_and_preserves_selection()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-a", t, 480), Meta("Alpha"));
        await WriteSessionAsync(Rec("s-b", t.AddHours(1), 480), Meta("Bravo"));
        var (vm, _, errors, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        vm.SelectedRow = vm.Rows.Single(r => r.Id == "s-b");        // selection on the OTHER row
        var original = vm.Rows.Single(r => r.Id == "s-a");
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        await new MetadataStore(_paths.MetaJson("s-a")).SaveAsync(
            new SessionMeta { Title = "Alpha edited", Medium = Medium.Webex, MatterIds = new[] { "M-2026-777" } },
            CancellationToken.None);
        await vm.UpsertRowAsync("s-a");

        var refreshed = vm.Rows.Single(r => r.Id == "s-a");
        Assert.Equal("Alpha edited", refreshed.Title);
        Assert.NotSame(original, refreshed);
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, actions);
        Assert.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Replace, actions);
        Assert.Equal("s-b", vm.SelectedRow?.Id);                    // selection preserved
        Assert.Contains("M-2026-777", vm.MatterFilterOptions.Select(o => o.Id));  // options rebuilt
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task UpsertRowAsync_inserts_new_row_at_sorted_position_and_respects_filters()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-a", t, 480), Meta("Alpha"));
        await WriteSessionAsync(Rec("s-c", t.AddHours(2), 480), Meta("Charlie"));
        var (vm, _, _, _) = MakeVm();
        await vm.OnNavigatedToAsync();
        Assert.Equal(new[] { "s-c", "s-a" }, vm.Rows.Select(r => r.Id).ToArray());   // newest-first

        await WriteSessionAsync(Rec("s-b", t.AddHours(1), 480), Meta("Bravo"));      // lands between
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);
        await vm.UpsertRowAsync("s-b");

        Assert.Equal(new[] { "s-c", "s-b", "s-a" }, vm.Rows.Select(r => r.Id).ToArray());
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, actions);

        vm.FilterText = "Charlie";                                  // s-d will fail this filter
        Assert.Equal(new[] { "s-c" }, vm.Rows.Select(r => r.Id).ToArray());
        await WriteSessionAsync(Rec("s-d", t.AddHours(3), 480), Meta("Delta"));
        await vm.UpsertRowAsync("s-d");
        Assert.DoesNotContain("s-d", vm.Rows.Select(r => r.Id));    // cached but filtered out of Rows
        vm.FilterText = "";
        Assert.Contains("s-d", vm.Rows.Select(r => r.Id));         // reappears from _all when unfiltered
    }

    [Fact]
    public async Task UpsertRowAsync_on_a_still_pending_session_leaves_the_row_recovering()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-pending", t, 480, ended: false), Meta("Interrupted"));
        var (vm, _, errors, _) = MakeVm();
        await vm.OnNavigatedToAsync();

        await vm.UpsertRowAsync("s-pending");                       // FinalizingSessionId is null (no live session)

        var row = vm.Rows.Single(r => r.Id == "s-pending");
        Assert.True(row.IsPendingRecovery);
        Assert.False(row.IsFinalizing);
        Assert.True(row.IsRecovering);
        Assert.Equal("", row.DurationDisplay);
        Assert.Empty(errors.Reports);
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~UpsertRowAsync" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\` — expected: `error CS1061: 'SessionsPageViewModel' does not contain a definition for 'UpsertRowAsync'`.
- [ ] **Refactor `ApplyFilters` to `PassesFilters`.** In `SessionsPageViewModel.cs` replace the `ApplyFilters` doc-comment **and** body (lines 177–198 — the range starts at the old `/// <summary>` on line 177 so it is not orphaned above the new block) with:
```csharp
    /// <summary>Recomputes Rows from the cached full list (3.2: in-memory filters only). The full
    /// LoadAsync/Refresh path; UpsertRowAsync (design 2026-07-12) avoids this Clear-and-refill so
    /// scroll+selection survive.</summary>
    private void ApplyFilters()
    {
        string? keepId = SelectedRow?.Id;
        Rows.Clear();
        foreach (var row in _all)
            if (PassesFilters(row)) Rows.Add(row);
        SelectedRow = Rows.FirstOrDefault(r => r.Id == keepId);
    }

    /// <summary>The active-filter predicate (design 3.2), factored out of ApplyFilters so the
    /// in-place UpsertRowAsync path applies exactly the same rules: archived hidden unless
    /// ShowArchived, Title contains FilterText, and the single-select Matter filter (All / No matter
    /// / a specific id).</summary>
    private bool PassesFilters(SessionRowViewModel row)
    {
        if (!ShowArchived && row.IsArchived) return false;
        if (FilterText.Length > 0
            && !row.Title.Contains(FilterText, StringComparison.OrdinalIgnoreCase)) return false;
        if (MatterFilterId == NoMatterSentinel) return row.MatterIds.Count == 0;
        if (MatterFilterId is { } matterId && !row.MatterIds.Contains(matterId)) return false;
        return true;
    }
```
- [ ] **Thread `isFinalizing` into `LoadAsync`.** In the `_dispatch(() => {...})` block of `LoadAsync`, replace the `_all = result.Sessions...` assignment (lines 159–163) with:
```csharp
                string? finalizingId = _session.FinalizingSessionId;
                _all = result.Sessions
                    .OrderByDescending(s => s.Session.StartedAtUtc)
                    .ThenByDescending(s => s.Id, StringComparer.Ordinal)
                    .Select(s => new SessionRowViewModel(s, _time, MatterLookup,
                        isFinalizing: s.Id == finalizingId))
                    .ToList();
```
- [ ] **Thread `isFinalizing` into `RefreshRowAsync`.** In `RefreshRowAsync`, replace line 367 (`list[i] = new SessionRowViewModel(item, _time, MatterLookup);`) with:
```csharp
                list[i] = new SessionRowViewModel(item, _time, MatterLookup,
                    isFinalizing: sessionId == _session.FinalizingSessionId);
```
- [ ] **Add `UpsertRowAsync` + helpers.** In `SessionsPageViewModel.cs`, immediately before the class's closing brace (after `RefreshRowAsync`, line 374) insert:
```csharp

    /// <summary>Non-disruptive single-row upsert (design 2026-07-12 section 2): reloads one session
    /// from disk and reflects it into Rows WITHOUT a collection Reset, so the DataGrid keeps its
    /// scroll offset and selection. Replaces an existing row in place (ObservableCollection Replace),
    /// inserts a brand-new row at the correct newest-first position iff it passes the active filters,
    /// removes a now-filtered/deleted row in place. Rebuilds the matter-filter options afterward
    /// (touches only MatterFilterOptions, never Rows). Marshals every UI mutation through _dispatch
    /// and catches everything: the wiring (State->Idle, SessionFinalizeCompleted, per-recovery) is
    /// fire-and-forget, so a stray upsert must never escape as an unobserved exception.</summary>
    public async Task UpsertRowAsync(string sessionId)
    {
        try
        {
            var item = await _maintenance.LoadSessionItemAsync(sessionId, CancellationToken.None);
            _dispatch(() =>
            {
                if (item is null) { RemoveRowInPlace(sessionId); RebuildMatterOptions(); return; }
                var newRow = new SessionRowViewModel(item, _time, MatterLookup,
                    isFinalizing: sessionId == _session.FinalizingSessionId);
                var list = _all.ToList();
                int i = list.FindIndex(r => r.Id == sessionId);
                if (i >= 0) list[i] = newRow;
                else list.Insert(SortedInsertIndex(list, newRow), newRow);
                _all = list;
                UpsertIntoRows(newRow);
                RebuildMatterOptions();
            });
        }
        catch (Exception ex) { _errors.Report("Updating session", ex); }
    }

    /// <summary>Newest-first insert index into a sorted _all copy - identical order to LoadAsync's
    /// OrderByDescending(StartedAtUtc).ThenByDescending(Id, Ordinal).</summary>
    private static int SortedInsertIndex(List<SessionRowViewModel> sorted, SessionRowViewModel row)
    {
        int pos = sorted.FindIndex(r => CompareNewestFirst(row, r) < 0);
        return pos < 0 ? sorted.Count : pos;
    }

    private static int CompareNewestFirst(SessionRowViewModel a, SessionRowViewModel b)
    {
        int byDate = b.StartedAtUtc.CompareTo(a.StartedAtUtc);
        return byDate != 0 ? byDate : string.CompareOrdinal(b.Id, a.Id);
    }

    /// <summary>Reflects a freshly built row into the bound Rows collection in place - Replace when it
    /// is already shown and still passes filters, Remove when it no longer passes, Insert at the
    /// matching sorted position when newly visible. Never Clears (no Reset), so scroll+selection hold;
    /// re-points SelectedRow to the replacement when the upserted row is the selected one.</summary>
    private void UpsertIntoRows(SessionRowViewModel row)
    {
        bool wasSelected = SelectedRow?.Id == row.Id;
        int existing = IndexInRows(row.Id);
        bool passes = PassesFilters(row);
        if (existing >= 0)
        {
            if (passes) Rows[existing] = row;
            else Rows.RemoveAt(existing);
        }
        else if (passes)
        {
            Rows.Insert(RowsInsertPosition(row), row);
        }
        if (wasSelected) SelectedRow = Rows.FirstOrDefault(r => r.Id == row.Id);
    }

    /// <summary>Rows preserves _all's newest-first order, so the insert index is the count of
    /// currently-shown rows that sort before this one in _all (which already contains it).</summary>
    private int RowsInsertPosition(SessionRowViewModel row)
    {
        int idx = 0;
        foreach (var r in _all)
        {
            if (r.Id == row.Id) break;
            if (PassesFilters(r)) idx++;
        }
        return idx;
    }

    private int IndexInRows(string id)
    {
        for (int k = 0; k < Rows.Count; k++)
            if (Rows[k].Id == id) return k;
        return -1;
    }

    /// <summary>A vanished session (LoadSessionItemAsync returned null): drop it from the cache and
    /// the bound list in place, no Reset.</summary>
    private void RemoveRowInPlace(string id)
    {
        if (_all.Any(r => r.Id == id)) _all = _all.Where(r => r.Id != id).ToList();
        int ri = IndexInRows(id);
        if (ri >= 0) Rows.RemoveAt(ri);
    }
```
- [ ] **Run tests and see PASS.** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~UpsertRowAsync" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\` — expected: 3 passed. Then run the whole class to prove the `ApplyFilters` refactor kept existing filter/refresh tests green: `--filter "FullyQualifiedName~SessionsPageViewModelTests"`.
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs tests/LocalScribe.App.Tests/SessionsPageViewModelTests.cs
git commit -m "feat(app): SessionsPageViewModel.UpsertRowAsync in-place row upsert (no collection Reset)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW"
```

---

### Task 5: On Stop, upsert the just-stopped row instead of a full reload
**Files:**
- Modify `src\LocalScribe.App\ViewModels\SessionsPageViewModel.cs` (the ctor's `session.PropertyChanged` handler, lines 118–126).
- Test `tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs` (add one gated integration `[Fact]`).

**Interfaces:**
- Consumes: `SessionViewModel.FinalizingSessionId` (Task 2), `UpsertRowAsync` (Task 4), `RefreshCommand` (existing), `SessionRowViewModel.IsFinalizing` (Task 3).
- Produces: no new public surface — replaces the `State -> Idle => LoadAsync` trigger with `State -> Idle => UpsertRowAsync(FinalizingSessionId)` (or `LoadAsync` when null).

Steps:
- [ ] **Write the failing test.** Append inside `SessionsPageViewModelTests` in `tests\LocalScribe.App.Tests\SessionsPageViewModelTests.cs`:
```csharp
    [Fact]
    public async Task Stop_upserts_the_just_stopped_row_as_Finalizing_without_a_reset()
    {
        // Reproduces the "stuck Recovering..." bug: after Stop the background finalize has not yet
        // written EndedAtUtc, so the row is pending. The State->Idle trigger must upsert it in place
        // (no collection Reset) labeled "Finalizing...", and the SessionFinalizeCompleted upsert then
        // flips it to final. A GatedEngineFactory holds the finalize open so FinalizingSessionId is
        // observably set between Stop and completion.
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync(Rec("s-existing", t, 480), Meta("Existing"));

        var maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), TimeProvider.System);
        var gated = new GatedEngineFactory();
        var (controller, _, _, clock) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var errors = new RecordingErrors();
        var vm = new SessionsPageViewModel(maintenance, session, new WindowRegistry(), errors,
            dispatch: a => a(), TimeProvider.System, revealInExplorer: _ => { });
        await vm.OnNavigatedToAsync();
        Assert.Equal(new[] { "s-existing" }, vm.Rows.Select(r => r.Id).ToArray());

        await session.StartCommand.ExecuteAsync(null);
        string liveId = controller.CurrentSessionId!;

        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.Rows.CollectionChanged += (_, e) => actions.Add(e.Action);

        clock.ElapsedMs = 5000;
        await session.StopCommand.ExecuteAsync(null);          // State -> Idle; finalize gated
        Assert.Equal(liveId, session.FinalizingSessionId);

        Assert.True(SpinWait.SpinUntil(() => vm.Rows.Any(r => r.Id == liveId), TimeSpan.FromSeconds(5)));
        var liveRow = vm.Rows.Single(r => r.Id == liveId);
        Assert.True(liveRow.IsFinalizing);
        Assert.False(liveRow.IsRecovering);
        Assert.Equal("", liveRow.DurationDisplay);
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, actions);
        Assert.Contains(vm.Rows, r => r.Id == "s-existing");   // pre-existing row preserved

        gated.CreateGate.Set();
        await controller.PendingFinalize;                       // clean finalize writes EndedAtUtc
        await vm.UpsertRowAsync(liveId);                        // simulate the SessionFinalizeCompleted wiring

        var settled = vm.Rows.Single(r => r.Id == liveId);
        Assert.False(settled.IsFinalizing);
        Assert.False(settled.IsPendingRecovery);
        Assert.Equal("00:05", settled.DurationDisplay);
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, actions);
        Assert.Empty(errors.Reports);
        session.Dispose();
    }
```
- [ ] **Run it and see it FAIL.** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~Stop_upserts_the_just_stopped" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\` — expected FAIL: the old trigger calls `LoadAsync`, which does `Rows.Clear()` → the recorded `actions` contain a `Reset`, so `Assert.DoesNotContain(...Reset...)` fails.
- [ ] **Change the trigger.** In `SessionsPageViewModel.cs` replace the ctor handler **and its preceding `//` comment** (lines 118–126 — include the old comment block at 118–121 so it is not left stale above the new block) with:
```csharp
        // 3.1 refresh trigger, upgraded for live auto-update (design 2026-07-12 section 3): landing
        // on Idle means a finalize just began. FinalizingSessionId (set at Stop before the Idle
        // transition) names the just-stopped session, so upsert just that row in place - it appears
        // immediately labeled "Finalizing..." with no scroll jump. Null id = the rare synchronous
        // fault-path Stop that reaches Idle with no background finalize -> fall back to a full
        // LoadAsync so the audio-only row still appears. Execute is fire-and-forget; UpsertRowAsync
        // and LoadAsync both catch everything, so nothing escapes as an unobserved exception.
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SessionViewModel.State) || session.State != SessionState.Idle)
                return;
            string? id = session.FinalizingSessionId;
            if (id is not null) _ = UpsertRowAsync(id);
            else RefreshCommand.Execute(null);
        };
```
- [ ] **Run tests and see PASS.** Same filter — expected: 1 passed. Then run `--filter "FullyQualifiedName~State_reaching_idle_triggers_refresh"` to confirm the existing Idle-trigger test still passes (it drives `session.State` directly with a never-started controller, so `FinalizingSessionId` is null and the handler falls back to `RefreshCommand.Execute`/`LoadAsync`, exactly as before).
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/SessionsPageViewModel.cs tests/LocalScribe.App.Tests/SessionsPageViewModelTests.cs
git commit -m "feat(app): on Stop, upsert the just-stopped row (Finalizing) instead of a full reload

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW"
```

---

### Task 6: `MaintenanceService.RecoverAllAsync` per-id `onRecovered` callback
**Files:**
- Modify `src\LocalScribe.App\Services\MaintenanceService.cs` (signature at line 381; the `if (did)` at line 394).
- Test `tests\LocalScribe.App.Tests\MaintenanceServiceTests.cs` (add one `[Fact]` using the in-file `WriteUnendedSessionAsync`/`WriteFinalizedSessionAsync`).

**Interfaces:**
- Produces: `public async Task<RecoveryScanResult> RecoverAllAsync(CancellationToken ct, Action<string>? onRecovered = null)` — fires `onRecovered(id)` for each session that was actually recovered, before the batch result returns. Optional param keeps the sole existing production caller (`App.xaml.cs:360`) and the existing test compiling until Task 7 threads it.

Steps:
- [ ] **Write the failing test.** Append inside `MaintenanceServiceTests` in `tests\LocalScribe.App.Tests\MaintenanceServiceTests.cs`:
```csharp
    [Fact]
    public async Task RecoverAllAsync_invokes_onRecovered_per_recovered_session_only()
    {
        var (svc, paths) = MakeService();
        await WriteFinalizedSessionAsync(paths, "2026-07-03_0100_Webex_done", "Done");   // must NOT fire
        await WriteUnendedSessionAsync(paths, "2026-07-03_0200_Webex_open1");
        await WriteUnendedSessionAsync(paths, "2026-07-03_0300_Webex_open2");

        var recoveredIds = new List<string>();
        var result = await svc.RecoverAllAsync(CancellationToken.None, onRecovered: recoveredIds.Add);

        Assert.Equal(new[] { "2026-07-03_0200_Webex_open1", "2026-07-03_0300_Webex_open2" },
            recoveredIds.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal(result.RecoveredIds.OrderBy(x => x, StringComparer.Ordinal),
            recoveredIds.OrderBy(x => x, StringComparer.Ordinal));                        // callback == batch result
        Assert.DoesNotContain("2026-07-03_0100_Webex_done", recoveredIds);               // finalized never fires
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~RecoverAllAsync_invokes_onRecovered" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\` — expected: `error CS1739`/`CS1501` — no `onRecovered` parameter on `RecoverAllAsync`.
- [ ] **Add the callback param.** In `MaintenanceService.cs` replace the signature at line 381:
```csharp
    public async Task<RecoveryScanResult> RecoverAllAsync(CancellationToken ct,
        Action<string>? onRecovered = null)
```
- [ ] **Invoke it per recovered session.** In `MaintenanceService.cs` replace line 394 (`if (did) recovered.Add(id);`) with:
```csharp
                // Design 2026-07-12 section 3: notify per recovered id so a long startup scan can
                // update the Sessions list one row at a time. Fires from this scan's background
                // thread; the App-layer wiring (App.xaml.cs) marshals it through the UI dispatcher.
                if (did) { recovered.Add(id); onRecovered?.Invoke(id); }
```
- [ ] **Run tests and see PASS.** Same filter — expected: 1 passed. Then run `--filter "FullyQualifiedName~RecoverAllAsync_recovers_unended"` to confirm the original recovery test (which calls the 1-arg overload) still passes.
- [ ] **Commit.**
```
git add src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs
git commit -m "feat(app): RecoverAllAsync per-id onRecovered callback for live recovery-scan updates

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW"
```

---

### Task 7: Wire the completion event + per-recovery callback; split the status chip (UI)
**Files:**
- Modify `src\LocalScribe.App\Pages\SessionsPage.xaml` (Status column, lines 207–210): change the "Recovering…" chip's binding to `IsRecovering` and add a "Finalizing…" chip bound to `IsFinalizing`.
- Modify `src\LocalScribe.App\App.xaml.cs`: wire `comp.Controller.SessionFinalizeCompleted` after the `sessionsVm` construction (after line 139); thread `onRecovered` into the orchestrator's `recoverAll` delegate (line 360).
- No new unit test (App.xaml.cs composition + XAML rendering are not unit-tested). The "test" is: keep `XamlHygieneTests` and all existing App/Core tests green, plus a precise manual smoke.

**Interfaces:**
- Consumes: `SessionController.SessionFinalizeCompleted` (Task 1), `SessionsPageViewModel.UpsertRowAsync` (Task 4), `MaintenanceService.RecoverAllAsync(ct, onRecovered)` (Task 6), `SessionRowViewModel.IsFinalizing`/`IsRecovering` (Task 3), existing `dispatch`, `comp.Controller`, `comp.Maintenance`, `sessionsVm`.
- Produces: no new types.

Steps:
- [ ] **Split the status chip.** In `SessionsPage.xaml` replace the Recovering `<Border>` (lines 207–210) with a Finalizing chip followed by the Recovering chip:
```xml
                                <Border Style="{StaticResource Chip}"
                                        ToolTip="Finishing this recording's transcript; the row updates itself when it completes"
                                        Visibility="{Binding IsFinalizing, Converter={StaticResource BoolToVis}}">
                                    <TextBlock Text="Finalizing..." FontStyle="Italic" />
                                </Border>
                                <Border Style="{StaticResource Chip}"
                                        Visibility="{Binding IsRecovering, Converter={StaticResource BoolToVis}}">
                                    <TextBlock Text="Recovering..." FontStyle="Italic" />
                                </Border>
```
- [ ] **Wire the completion event.** In `App.xaml.cs`, immediately after the `sessionsVm = new ViewModels.SessionsPageViewModel(...)` statement ends (after line 139, before the `mattersVm` construction) insert:
```csharp
        // Sessions-list live auto-update (design 2026-07-12 section 3): a completed background
        // finalize (success OR failure) upserts just that row in place - the row flips from
        // "Finalizing..." to its final status without a manual Refresh and with no scroll jump.
        // Marshaled through dispatch like every other controller-event handler; UpsertRowAsync
        // catches its own faults, so fire-and-forget is safe.
        comp.Controller.SessionFinalizeCompleted += id => dispatch(() => _ = sessionsVm.UpsertRowAsync(id));
```
- [ ] **Thread the per-recovery callback.** In `App.xaml.cs` replace the orchestrator's `recoverAll:` argument (line 360, `recoverAll: ct => comp.Maintenance.RecoverAllAsync(ct),`) with:
```csharp
            recoverAll: ct => comp.Maintenance.RecoverAllAsync(ct,
                onRecovered: id => dispatch(() => _ = sessionsVm.UpsertRowAsync(id))),
```
(Leave the batch-end `ScanCompleted -> RefreshCommand.Execute(null)` at lines 366–370 as the final reconcile, per design section 3.)
- [ ] **Build 0-warning + full App/Core suites green.** Run:
```
dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\claude\F--LocalScribe\4c069d5c-1598-457e-ab60-c2de6a9d7c1e\scratchpad\isobin\
```
Expected: build 0 warnings; App suite all green (incl. `XamlHygieneTests` — the new chip adds no hardcoded ARGB brush, no keyless `<Style TargetType="TextBlock">`, no `TextFillColorPrimaryBrush` literal, and the page root already carries the inheritable `TextElement.Foreground` marker); Core suite green (2 known fixture fails are pre-existing and unrelated).
- [ ] **Manual smoke (WPF — the app is not unit-testable here).** Launch the app, then:
  1. Start a recording, speak briefly, press Stop. Confirm the Sessions list row appears immediately with a **"Finalizing…"** chip (not "Recovering…"), blank Duration, and that the list does NOT jump to the top / lose selection or scroll position.
  2. Within a second or two the same row flips to its final Duration with the "Finalizing…" chip gone — no manual Refresh pressed.
  3. Scroll down a long list, select a middle row, Stop a recording: confirm scroll offset and the selected row are preserved across both the "Finalizing…" appearance and the final flip.
  4. (Recovery path) Kill the app mid-recording once to leave an un-ended session, relaunch: confirm that session shows **"Recovering…"**, and that it re-lists as finalized on its own as the startup scan recovers it (no manual Refresh).
- [ ] **Commit.**
```
git add src/LocalScribe.App/Pages/SessionsPage.xaml src/LocalScribe.App/App.xaml.cs
git commit -m "feat(app): wire live sessions-list auto-update + Finalizing/Recovering chip split

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01X6u4L9ZxYKCuvxGBbrJrrW"
```

---

## Self-review

**(a) Spec coverage — every design section maps to a task:**
- Section 1 (Core completion signal: `SessionFinalizeCompleted` in the `finally`, `FinalizingSessionId`) → **Task 1** (+ surfaced through `SessionViewModel` in **Task 2**).
- Section 2 (non-disruptive `UpsertRowAsync`: Replace/Insert/Remove in place, sorted insert, filter check, rebuild matter options, `_dispatch` + catch-all) → **Task 4**.
- Section 3 (wiring: State→Idle upsert-or-load → **Task 5**; `SessionFinalizeCompleted` → upsert and per-recovery callback → **Task 7** front-end wiring over the **Task 6** `onRecovered` seam; batch-end reconcile kept).
- Section 4 ("Finalizing…" vs "Recovering…" label: `isFinalizing = id == FinalizingSessionId`, chip split) → **Task 3** (VM flags) + **Task 7** (XAML chips).
- Section 5 (testing): Core `SessionFinalizeCompleted` fires once on clean AND failure path + `FinalizingSessionId` lifecycle → **Task 1**; VM `UpsertRowAsync` Replace-not-Reset + preserves selection, sorted insert only when passing filters, rebuild options without clearing Rows, finalize-failed leaves row pending → **Task 4**; label finalizing-vs-recovering → **Task 3**; plus the reproduction integration test → **Task 5**; recovery callback → **Task 6**.
- "Out of scope" items (other-process live sessions, cross-window edits beyond existing `RefreshRowAsync`, polling/`FileSystemWatcher`) are respected: `RefreshRowAsync` is left intact (only threaded with `isFinalizing`), no timer/watcher is introduced. **No spec requirement is left unmapped.**

**(b) Placeholder scan:** every step shows real, grounded code (exact current lines read from the repo: `SessionController.cs` 142/155/975–981, `SessionsPageViewModel.cs` 122–126/159–163/178–198/367, `SessionRowViewModel.cs` 46/51–52/77, `SessionViewModel.cs` 82, `MaintenanceService.cs` 381/394, `SessionsPage.xaml` 207–210, `App.xaml.cs` 139/360). No "TBD"/"similar to"/"add error handling" placeholders.

**(c) Type consistency across tasks:** `FinalizingSessionId` is `string?` on both `SessionController` (Task 1) and `SessionViewModel` (Task 2); `SessionFinalizeCompleted` is `event Action<string>?` (Task 1) consumed as `id => dispatch(...)` (Task 7); `SessionRowViewModel`'s 4th param is `bool isFinalizing = false` (Task 3), passed by name from `LoadAsync`/`RefreshRowAsync`/`UpsertRowAsync` (Task 4) and the label test (Task 3) — the 2-/3-arg call sites (`SessionRowSourceTests`, `SessionRowMatterChipsTests`, `MetadataEditorViewModel`) stay valid via the default. `UpsertRowAsync(string): Task` (Task 4) is consumed by Tasks 5 and 7. `RecoverAllAsync(ct, Action<string>? onRecovered = null)` (Task 6) is consumed by Task 7; the pre-existing 1-arg call in `MaintenanceServiceTests` and the original test stay valid via the default. `PassesFilters` is the single filter predicate shared by `ApplyFilters` and `UpsertIntoRows`/`RowsInsertPosition`. `GatedEngineFactory`/`FakeProvider`/`LiveTestDoubles` are reachable from App.Tests because `LiveTestDoubles.cs` is `<Compile Include>`-linked into that assembly. All good.
