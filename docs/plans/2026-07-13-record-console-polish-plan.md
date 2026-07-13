# Record-Console Polish Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement design §5 (Record-console polish, all five sub-items) of `docs/plans/2026-07-13-meetily-round-design.md`: (1) an empty-state hint while recording with no transcript lines yet, (2) a prominent red-accented Recording/Paused status header with the elapsed timer, (3) in-window Local/Remote mini level meters reusing the overlay pill's exact level source, (4) an engine chip ("base.en · CPU") on the ready card and while recording plus a live keep-up chip ("Keeping up OK" / red "Lagging x1.4") that absorbs the old one-shot "transcription lagging" text, and (5) a pre-flight target-detected line on the ready card ("Webex detected …" / "No call app playing audio - will record system mix") replacing the two redundant grey summary lines, refreshed by the console's existing 2 s visible-only poll.
**Architecture:** Core changes are strictly read-only surface over existing signals: `TranscriptionWorker` exposes its existing rolling RTF window as `RecentRtf` (Volatile double, NaN sentinel), and `SessionController` exposes `PreviewEnginePlan` (the same `BackendSelector.Select` over the same injected seams `StartAsync` resolves), `ActiveEnginePlan`, `ActiveModelName` (the same `LastModel ?? Plan.ModelName` resolution `PersistFinalAsync` writes to session.json), and `RecentTranscriptionRtf`. The App layer polls them on the existing ~150 ms `SessionViewModel.TimerTick` (no new events/threads) into `EngineChipText`/`KeepUpText`/`KeepUpLagging`; `RecordingConsoleViewModel` computes `PreflightSummary` (pure `PreflightLine` over `RemoteCapturePlanner.Plan`) and `EngineSummary` inside the existing `RefreshRemoteTargetsAsync` (already driven by `LiveViewWindow`'s 2 s visible-only `_remoteTargetPoll` + on-show + DropDownOpened — no new timer); `TranscriptLinesViewModel` gains `ShowListeningHint`. One final XAML task restructures `LiveViewWindow.xaml`.
**Tech Stack:** C#/.NET 10, WPF, CommunityToolkit.Mvvm, Wpf.Ui, xUnit.

## Global Constraints

- **Target branch:** `feat/record-console-polish`, created off master @ 7d6c88d. The design spec `docs/plans/2026-07-13-meetily-round-design.md` is already ON master; only THIS plan (`docs/plans/2026-07-13-record-console-polish-plan.md`) needs adding to the branch (a `docs(plans): ...` commit) if it is not there yet.
- **Merge order for the round:** record-console-polish merges FIRST (before retranscription-versions, audio-import, session-search).
- **Purely additive/visual (design §5 + §7):** capture and Start/Stop/Pause semantics must NOT change. The pre-flight line is informational only — it must NEVER gate or delay Start (verified Meetily anti-pattern: blocking start on model checks). No task below touches `StartAsync`'s control flow, capture legs, or command CanExecute gates.
- **Verbatim display (locked evidentiary rule):** no filtering/cleanup of transcript text anywhere.
- **Empty-state rule (design §5.1):** the hint appears ONLY while recording with zero transcript lines; removed at the first line (segment or marker).
- **No global hotkeys** (locked project decision) — nothing here adds any.
- **No new Core telemetry beyond read-only properties on existing types** (design §5 closing note): Tasks 1–2 add get-only properties only; no new events, no new types, no behavior change.
- 0-warning build gate must hold.
- Tests: xUnit. Filtered run: `dotnet test "<testproj>" --filter "FullyQualifiedName~<Name>" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\record-console-polish\`
- IMPORTANT: the LocalScribe app may be running and LOCK bin DLLs (MSB3027 copy error — NOT a compile error). Always use the isolated BaseOutputPath above (every command below already appends it); NEVER kill the user's running app.
- Never use Unicode emojis in test code or scripts (project rule). All new UI strings in this plan are ASCII (plus `·` written as a C# escape, matching the read-view footer precedent in `ReadViewViewModel.cs:198`); the design's "✓" glyphs are rendered as plain text ("Keeping up OK", "Webex detected") so test assertions stay ASCII.
- Test projects: App = `tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj`; Core = `tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj`. App.Tests does NOT project-reference Core.Tests — it `<Compile Include>`-links `LiveTestDoubles.cs` (with `GatedEngineFactory`, `FakeEngineFactory`, `FakeProvider`, `LiveTestDoubles.MakeController/Options`) plus `FakeTranscriptionEngine.cs`, so those doubles compile INTO App.Tests and are directly usable there. There is NO `InternalsVisibleTo` anywhere in this repo (verified) — new members that tests call directly must be `public`.
- Commit style: `feat(core)`/`feat(app)`/`test(...)`/`docs(...)`; every commit message ends with exactly this trailer line:
```
Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
```
- Line anchors are grounded @ master 7d6c88d (which merged feat/cpu-threads-quantized-weights: `BackendPlan` is now `record BackendPlan(Backend Backend, string ModelName, int? CpuThreads = null)` with `EffectiveThreads`; `BackendSelector.Select` canonicalizes explicit model names via `ModelFileResolver.CanonicalName` and attaches `AutoCpuThreads(fastCores)` to EVERY plan it returns; `SessionController` gained `LastWeightsFile` beside `LastModel`); re-verify each anchor's quoted context before editing — if drifted, locate by the quoted code, not the number.
- Core suite has 2 known pre-existing fixture failures (unrelated); App suite must be fully green.

---

### Task 1: Core — `TranscriptionWorker.RecentRtf` (rolling realtime factor)
**Files:**
- Modify `src\LocalScribe.Core\Transcription\TranscriptionWorker.cs` (field after line 31 `private string? _pendingWeightsMarker;   // deferred until it can sit on the right side of segments`; property after line 35 `public event Action<string>? ErrorRaised;`; write in `TrackRtf` lines 150–155; reset inside the one-shot lagging block after line 101 `_rtfWindow.Clear();`).
- Test `tests\LocalScribe.Core.Tests\TranscriptionWorkerTests.cs` (add two `[Fact]`s before the closing brace, reusing the in-file `Seg`/`Worker` helpers and `FakeEngineFactory`/`FakeTranscriptionEngine`/`FakeClock`).

**Interfaces:**
- Produces: `public double? TranscriptionWorker.RecentRtf { get; }` — the mean of the existing `_rtfWindow` (per-segment processing-ms / audio-ms over the last `LaggingWindow` segments); `null` before the first tracked segment and again right after the one-shot lagging downgrade clears the window. Written on the single-consumer worker-loop thread, safe to read from any thread (`Volatile` pair over a `double` with a NaN sentinel).
- Consumes: existing `_rtfWindow`, `TrackRtf`, the lagging one-shot block (`RunAsync` lines 92–102). No behavior change to the RTF_LAGGING marker/downgrade path, and none to the 7d6c88d weights-provenance path (`Adopt`/`FlushPendingWeightsMarker`/`_weightsFile` untouched).

Steps:
- [ ] **Write the failing tests.** Append inside `TranscriptionWorkerTests` (before the closing brace) in `tests\LocalScribe.Core.Tests\TranscriptionWorkerTests.cs`:
```csharp
    [Fact]
    public async Task RecentRtf_reports_the_rolling_window_average()
    {
        // Design 2026-07-13 section 5 item 4 (keep-up chip): the worker's EXISTING per-segment RTF
        // window is the lag data - expose its mean read-only, no new telemetry. FakeClock deltas
        // are exact, so the expected averages are exact doubles (no tolerance needed).
        var clock = new FakeClock();
        var script = new Queue<long>(new long[] { 2, 1 });     // per-segment clock multiplier
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName, s =>
        {
            clock.ElapsedMs += script.Dequeue() * (s.EndMs - s.StartMs);   // RTF 2.0 then 1.0
            return new TranscriptionResult("ok", "en", 0.0);
        }));
        var worker = Worker(factory, clock, new TranscriptionWorkerOptions { LaggingWindow = 3 });
        Assert.Null(worker.RecentRtf);                          // nothing tracked yet

        var run = worker.RunAsync(default);
        await worker.EnqueueAsync(Seg(0), default);             // RTF 2.0
        await worker.EnqueueAsync(Seg(1000), default);          // RTF 1.0
        worker.Complete();
        await run;

        Assert.Equal(1.5, worker.RecentRtf);                    // (2.0 + 1.0) / 2; window (3) never tripped
    }

    [Fact]
    public async Task RecentRtf_resets_when_the_lagging_downgrade_clears_the_window()
    {
        // The one-shot RTF_LAGGING downgrade clears _rtfWindow (existing behavior, untouched).
        // RecentRtf must reset with it: the pre-downgrade engine's stale >1.0 average must not
        // keep the keep-up chip red after the downgrade already replaced that engine.
        var clock = new FakeClock();
        var factory = new FakeEngineFactory(plan => new FakeTranscriptionEngine(plan.ModelName, s =>
        {
            clock.ElapsedMs += 2 * (s.EndMs - s.StartMs);       // RTF = 2 on every segment
            return new TranscriptionResult("slow", "en", 0.0);
        }));
        var worker = Worker(factory, clock, new TranscriptionWorkerOptions { LaggingWindow = 3 });

        var run = worker.RunAsync(default);
        for (int i = 0; i < 3; i++) await worker.EnqueueAsync(Seg(i * 1000), default);   // 3rd trips the one-shot
        worker.Complete();
        await run;

        Assert.Equal(2, factory.Created.Count);                 // the downgrade really fired
        Assert.Null(worker.RecentRtf);                          // window cleared -> sentinel, not a stale 2.0
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~RecentRtf" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\record-console-polish\` — expected: `error CS1061: 'TranscriptionWorker' does not contain a definition for 'RecentRtf'`.
- [ ] **Add the field.** In `TranscriptionWorker.cs`, immediately after line 31 (`private string? _pendingWeightsMarker;   // deferred until it can sit on the right side of segments`) insert:
```csharp
    // Rolling mean of _rtfWindow, mirrored here so the UI can read it cross-thread without
    // touching the (single-consumer) Queue. NaN = "no data" sentinel; see RecentRtf.
    private double _recentRtf = double.NaN;
```
- [ ] **Add the property.** In `TranscriptionWorker.cs`, immediately after line 35 (`public event Action<string>? ErrorRaised;`) insert:
```csharp

    /// <summary>Rolling realtime factor over the last LaggingWindow transcribed segments
    /// (processing-ms / audio-ms; above 1.0 = falling behind live audio), or null before the first
    /// tracked segment and again right after the one-shot lagging downgrade clears the window
    /// (design 2026-07-13 section 5 item 4: the console's keep-up chip). Read-only surface over the
    /// EXISTING _rtfWindow lag data - no new telemetry. Written only on the single-consumer worker
    /// loop; the Volatile pair keeps the cross-thread double read un-torn on every platform.</summary>
    public double? RecentRtf
    {
        get
        {
            double v = Volatile.Read(ref _recentRtf);
            return double.IsNaN(v) ? null : v;
        }
    }
```
- [ ] **Write it in `TrackRtf`.** Replace the method at lines 150–155:
```csharp
    private void TrackRtf(long processingMs, long audioMs)
    {
        if (audioMs <= 0) return;
        _rtfWindow.Enqueue(processingMs / (double)audioMs);
        while (_rtfWindow.Count > _o.LaggingWindow) _rtfWindow.Dequeue();
    }
```
with:
```csharp
    private void TrackRtf(long processingMs, long audioMs)
    {
        if (audioMs <= 0) return;
        _rtfWindow.Enqueue(processingMs / (double)audioMs);
        while (_rtfWindow.Count > _o.LaggingWindow) _rtfWindow.Dequeue();
        Volatile.Write(ref _recentRtf, _rtfWindow.Average());   // keep-up chip source (section 5 item 4)
    }
```
- [ ] **Reset on the one-shot downgrade.** In `RunAsync`, the lagging block currently reads (lines 96–101):
```csharp
                    // one-shot in 2b: marker + a single downgrade step (spec 3/8.1)
                    _laggingRaised = true;
                    MarkerRaised?.Invoke(Markers.TranscriptionLagging);
                    ErrorRaised?.Invoke("RTF_LAGGING");
                    engine = await DowngradeAsync(engine, ct);
                    _rtfWindow.Clear();
```
Append one line after `_rtfWindow.Clear();` (inside the same block):
```csharp
                    // Fresh window: the pre-downgrade engine's average must not keep the keep-up
                    // chip red after the ladder step already replaced that engine.
                    Volatile.Write(ref _recentRtf, double.NaN);
```
- [ ] **Run tests and see PASS.** Same filter as above — expected: 2 passed. Then run the whole class to prove no regression: `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~TranscriptionWorkerTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\record-console-polish\`.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Transcription/TranscriptionWorker.cs tests/LocalScribe.Core.Tests/TranscriptionWorkerTests.cs
git commit -m "feat(core): TranscriptionWorker.RecentRtf rolling realtime factor for the keep-up chip

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Core — read-only engine/keep-up surface on `SessionController`
**Files:**
- Modify `src\LocalScribe.Core\Live\SessionController.cs` (insert four get-only properties immediately after line 154, `public string? FinalizingSessionId => _finalizing?.Id;`, and before the `View` doc comment at line 156).
- Test `tests\LocalScribe.Core.Tests\SessionControllerTests.cs` (add two `[Fact]`s inside the existing class, which already has `_root` and `using LocalScribe.Core.Transcription;`).

**Interfaces:**
- Produces:
  - `public BackendPlan SessionController.PreviewEnginePlan { get; }` — `BackendSelector.Select(_hardware.Probe(), _settingsProvider(), _availableModels()).Plan`: the plan Start WOULD bind right now, off the SAME injected seams `StartAsync` resolves (single source of truth — the chip can never drift from Start). Since 7d6c88d the returned plan always carries `CpuThreads` (`AutoCpuThreads(fastCores)`) and its `ModelName` is CANONICAL (`ModelFileResolver.CanonicalName` strips quant file suffixes — a persisted "small.en-q8_0" previews as "small.en", exactly the display the chip wants; the quant choice is a file detail, surfaced elsewhere by the weights marker/session.json `WeightsFile`). Side-effect-free on the controller; first call may probe hardware (nvidia-smi ≤5 s timeout, cached by `LiveHardwareProbe`), so UI callers read it off the UI thread.
  - `public BackendPlan? SessionController.ActiveEnginePlan { get; }` — the running session's Start-time plan (`_session?.Plan`); null when Idle.
  - `public string? SessionController.ActiveModelName { get; }` — `LastModel.Value ?? Plan.ModelName` for the running session (tracks mid-session ladder downgrades via `TranscribedSegment.ModelName`, exactly the resolution `PersistFinalAsync` writes to session.json `Model` at line 1199 — NOT the new `LastWeightsFile`, which is file-level provenance the chip does not display); null when Idle.
  - `public double? SessionController.RecentTranscriptionRtf { get; }` — `_session?.Worker.RecentRtf` (Task 1).
- Consumes: `_hardware`, `_settingsProvider`, `_availableModels`, `_session` (volatile), `Session.Plan`/`Session.LastModel`/`Session.Worker` (existing private fields), `BackendSelector.Select`, `TranscriptionWorker.RecentRtf` (Task 1).

Steps:
- [ ] **Write the failing tests.** Append inside `SessionControllerTests` (before the closing brace) in `tests\LocalScribe.Core.Tests\SessionControllerTests.cs`:
```csharp
    [Fact]
    public async Task Engine_surface_previews_idle_and_tracks_the_active_session()
    {
        // Design 2026-07-13 section 5 item 4. MakeController wires StaticHardwareProbe(false,0,false,4)
        // (no CUDA/Vulkan, 4 fast cores -> Cpu, ceiling base.en) over available models {base.en, tiny.en}
        // and default Settings (Model=auto, Backend=Auto, Language=auto) -> plan (Cpu, "base.en",
        // CpuThreads 4): since 7d6c88d Select attaches AutoCpuThreads(fastCores) to every plan, and
        // AutoCpuThreads(4) = Clamp(Max(Min(4, 8), 2), 2, 8) = 4 - record equality needs all three.
        var (c, _, _, clock) = LiveTestDoubles.MakeController(_root);
        Assert.Equal(new BackendPlan(Backend.Cpu, "base.en", 4), c.PreviewEnginePlan);
        Assert.Null(c.ActiveEnginePlan);
        Assert.Null(c.ActiveModelName);
        Assert.Null(c.RecentTranscriptionRtf);

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);
        Assert.Equal(new BackendPlan(Backend.Cpu, "base.en", 4), c.ActiveEnginePlan);
        Assert.Equal("base.en", c.ActiveModelName);   // LastModel (fake engine echoes the plan model) or the Start plan - both "base.en"

        clock.ElapsedMs = 5000;
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        Assert.Null(c.ActiveEnginePlan);              // Idle again: the whole active surface clears
        Assert.Null(c.ActiveModelName);
        Assert.Null(c.RecentTranscriptionRtf);
    }

    [Fact]
    public async Task Engine_surface_RecentTranscriptionRtf_follows_the_workers_rolling_rtf()
    {
        // A transcribe fake that advances the SESSION clock by 2x each segment's audio duration ->
        // the worker's rolling RTF is exactly 2.0 once the first segment lands. clk is assigned
        // before StartAsync, and the engine only transcribes after Start, so the closure is safe.
        FakeClock? clk = null;
        var factory = new FakeEngineFactory(s =>
        {
            clk!.ElapsedMs += 2 * (s.EndMs - s.StartMs);
            return new TranscriptionResult("slow", "en", 0.0);
        });
        var (c, _, _, clock) = LiveTestDoubles.MakeController(_root, engineFactory: factory);
        clk = clock;

        await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        // The fakes' speech-then-silence frames close a VAD segment during live capture (the same
        // mid-recording path Peaks/SilentLeg tests rely on); bound the wait on the observable effect.
        Assert.True(SpinWait.SpinUntil(() => c.RecentTranscriptionRtf is > 1.0, TimeSpan.FromSeconds(5)),
            "worker never reported a lagging realtime factor");
        Assert.Equal(2.0, c.RecentTranscriptionRtf);   // exact: every tracked segment is exactly 2x

        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        Assert.Null(c.RecentTranscriptionRtf);
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~Engine_surface" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\record-console-polish\` — expected: `error CS1061: 'SessionController' does not contain a definition for 'PreviewEnginePlan'` (plus `ActiveEnginePlan`/`ActiveModelName`/`RecentTranscriptionRtf`).
- [ ] **Add the four properties.** In `SessionController.cs`, immediately after line 154 (`public string? FinalizingSessionId => _finalizing?.Id;`) and before the `View` doc comment (line 156), insert:
```csharp

    /// <summary>The engine plan Start WOULD bind right now (design 2026-07-13 section 5 item 4:
    /// the ready card's engine chip): the same BackendSelector.Select over the same injected
    /// hardware/settings/available-models seams StartAsync itself resolves, so the chip can never
    /// drift from what Start actually does. ModelName comes back CANONICAL (Select routes explicit
    /// picks through ModelFileResolver.CanonicalName, so a persisted "small.en-q8_0" previews as
    /// "small.en" - the quant choice is a file detail the chip does not show), and CpuThreads rides
    /// along per Select's contract. Read-only and side-effect-free on the controller; the
    /// FIRST call may probe hardware (nvidia-smi, cached thereafter by LiveHardwareProbe), so UI
    /// callers read it off the UI thread (the console reads it inside its Task.Run refresh).
    /// Informational only - it never gates or delays Start (locked anti-pattern, design section 7).</summary>
    public BackendPlan PreviewEnginePlan
        => BackendSelector.Select(_hardware.Probe(), _settingsProvider(), _availableModels()).Plan;

    /// <summary>The running session's Start-time engine plan, or null when Idle (design 2026-07-13
    /// section 5 item 4: the while-recording engine chip). Benign cross-thread read (cosmetic
    /// label only, same contract as FinalizingSessionId above).</summary>
    public BackendPlan? ActiveEnginePlan => _session?.Plan;

    /// <summary>The model that most recently produced a transcribed segment for the running session
    /// (tracks mid-session ladder downgrades via TranscribedSegment.ModelName), falling back to the
    /// Start plan's model before the first segment; null when Idle. The SAME LastModel-then-plan
    /// resolution PersistFinalAsync writes to session.json's Model field, so the live chip and the
    /// read-view footer agree by construction. (Deliberately NOT LastWeightsFile - that is
    /// file-level provenance for the evidentiary record, not a display name.)</summary>
    public string? ActiveModelName
        => _session is { } s ? (s.LastModel.Value ?? s.Plan.ModelName) : null;

    /// <summary>The running session's rolling transcription realtime factor (see
    /// <see cref="TranscriptionWorker.RecentRtf"/>), or null when Idle or before the first tracked
    /// segment. Drives the console's keep-up chip (design 2026-07-13 section 5 item 4).</summary>
    public double? RecentTranscriptionRtf => _session?.Worker.RecentRtf;
```
- [ ] **Run tests and see PASS.** Same filter — expected: 2 passed. Then run the whole class to prove no regression: `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~SessionControllerTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\record-console-polish\`.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerTests.cs
git commit -m "feat(core): read-only engine-plan + keep-up surface on SessionController

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: App — engine + keep-up chips on `SessionViewModel` (absorbs the one-shot lag warning)
**Files:**
- Modify `src\LocalScribe.App\ViewModels\SessionViewModel.cs`: add `using LocalScribe.Core.Transcription;`; replace `_isLagging` (line 50) with three new observable props; remove the `RTF_LAGGING` `ErrorRaised` subscription (line 147); add `PreviewEnginePlan` after line 87; reset the chip in `StartAsync` (line 242) and refresh it at Start/Stop/TimerTick; add `RefreshEngineChips` + two public static helpers before the closing brace.
- Test `tests\LocalScribe.App.Tests\SessionViewModelTests.cs` (add `using LocalScribe.Core.Transcription;` + three `[Fact]`s reusing the in-file `MakeVm` and the linked `LiveTestDoubles`/`FakeEngineFactory`).

**Interfaces:**
- Consumes: `SessionController.PreviewEnginePlan` / `ActiveEnginePlan` / `ActiveModelName` / `RecentTranscriptionRtf` (Task 2); existing `TimerTick` (driven by the app-lifetime 150 ms DispatcherTimer in `App.xaml.cs:336-339` — no new timer), `StartAsync`, `StopAsync`.
- Produces:
  - `public BackendPlan SessionViewModel.PreviewEnginePlan => _controller.PreviewEnginePlan;` (consumed by Task 4).
  - `public string EngineChipText { get; }` (`[ObservableProperty]`, `""` when Idle) — "base.en · CPU" while a session runs.
  - `public string KeepUpText { get; }` (`[ObservableProperty]`, default `"Keeping up OK"`); `public bool KeepUpLagging { get; }` (`[ObservableProperty]`).
  - `public static string SessionViewModel.FormatEngineChip(BackendPlan plan, string? modelName = null)` — `"{model} · {BACKEND}"`, the read-view footer shape (consumed by Task 4).
  - `public static (string Text, bool Lagging) SessionViewModel.KeepUpChip(double? rtf)` — pure mapping: null or ≤1.0 → `("Keeping up OK", false)`; >1.0 → `("Lagging x{rtf:0.0}", true)`, invariant culture.
- **Removes** (design §5.4 "absorbs the current lag warning text"): `IsLagging` (`[ObservableProperty] bool _isLagging`, its `ErrorRaised` subscription, its `StartAsync` reset). Verified: no test references `IsLagging`; the only other reference is the `LiveViewWindow.xaml:95-97` TextBlock, which Task 6 removes (until then it is a dead runtime binding — harmless, logged to debug output only, same branch).

Steps:
- [ ] **Write the failing tests.** In `tests\LocalScribe.App.Tests\SessionViewModelTests.cs`, add the using: the file's using block currently ends with
```csharp
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
```
— insert `using LocalScribe.Core.Transcription;` between those two lines. Then append inside the class (before the closing brace):
```csharp
    [Fact]
    public void KeepUpChip_maps_rtf_to_text_and_lag_state()
    {
        // Pure mapping (design 2026-07-13 section 5 item 4). ASCII on purpose (project rule).
        Assert.Equal(("Keeping up OK", false), SessionViewModel.KeepUpChip(null));    // no data yet
        Assert.Equal(("Keeping up OK", false), SessionViewModel.KeepUpChip(0.4));
        Assert.Equal(("Keeping up OK", false), SessionViewModel.KeepUpChip(1.0));     // at threshold: OK
        Assert.Equal(("Lagging x1.4", true), SessionViewModel.KeepUpChip(1.42));      // one decimal
        Assert.Equal(("Lagging x2.0", true), SessionViewModel.KeepUpChip(1.96));      // rounded
    }

    [Fact]
    public async Task Chips_populate_on_start_and_clear_on_stop()
    {
        // MakeVm's controller: StaticHardwareProbe -> Cpu, auto over {base.en,tiny.en} -> base.en;
        // Select attaches AutoCpuThreads(4) = 4 to every plan (7d6c88d), so record equality is 3-arg.
        var (vm, _) = MakeVm();
        Assert.Equal(new BackendPlan(Backend.Cpu, "base.en", 4), vm.PreviewEnginePlan);  // ready-card source
        Assert.Equal("", vm.EngineChipText);
        Assert.Equal("Keeping up OK", vm.KeepUpText);
        Assert.False(vm.KeepUpLagging);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("base.en \u00B7 CPU", vm.EngineChipText);   // renders as a middle dot; escape keeps source ASCII

        await vm.StopCommand.ExecuteAsync(null);
        Assert.Equal("", vm.EngineChipText);                     // cleared eagerly at Stop
        Assert.Equal("Keeping up OK", vm.KeepUpText);
        Assert.False(vm.KeepUpLagging);
        vm.Dispose();
    }

    [Fact]
    public async Task Chips_show_a_live_lagging_factor_from_the_worker()
    {
        // Same 2x-clock transcribe fake as the Core test: the worker's rolling RTF is exactly 2.0.
        // TimerTick (production: the existing 150 ms DispatcherTimer) maps it onto the chip.
        FakeClock? clk = null;
        var factory = new FakeEngineFactory(s =>
        {
            clk!.ElapsedMs += 2 * (s.EndMs - s.StartMs);
            return new TranscriptionResult("slow", "en", 0.0);
        });
        var (controller, _, _, clock) = LiveTestDoubles.MakeController(_root, engineFactory: factory);
        clk = clock;
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => controller.RecentTranscriptionRtf is > 1.0, TimeSpan.FromSeconds(5)),
            "worker never reported a lagging realtime factor");
        vm.TimerTick();
        Assert.True(vm.KeepUpLagging);
        Assert.Equal("Lagging x2.0", vm.KeepUpText);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.False(vm.KeepUpLagging);                          // Idle: surface reads null -> OK
        Assert.Equal("Keeping up OK", vm.KeepUpText);
        vm.Dispose();
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~Chip" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\record-console-polish\` — expected: `error CS0117: 'SessionViewModel' does not contain a definition for 'KeepUpChip'` (plus CS1061 on `PreviewEnginePlan`/`EngineChipText`/`KeepUpText`/`KeepUpLagging`).
- [ ] **Add the using.** In `SessionViewModel.cs` the using block currently reads:
```csharp
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
namespace LocalScribe.App.ViewModels;
```
Insert `using LocalScribe.Core.Transcription;` between `using LocalScribe.Core.Model;` and the namespace line.
- [ ] **Swap the observable properties.** Replace lines 49–50:
```csharp
    [ObservableProperty] private string? _lastNotice;
    [ObservableProperty] private bool _isLagging;
```
with:
```csharp
    [ObservableProperty] private string? _lastNotice;
    /// <summary>While-recording engine chip (design 2026-07-13 section 5 item 4): the
    /// model-middledot-BACKEND read-view-footer shape (rendered "base.en (middot) CPU"), built from the
    /// ACTIVE session's plan + the model that actually produced the latest segment
    /// (tracks mid-session ladder downgrades). "" when Idle. Refreshed eagerly at Start/Stop and
    /// on every existing ~150 ms TimerTick - polled, no new events or threads.</summary>
    [ObservableProperty] private string _engineChipText = "";
    /// <summary>Keep-up chip text ("Keeping up OK" / "Lagging x1.4", design 2026-07-13 section 5
    /// item 4). Absorbs the old one-shot boolean "transcription lagging" warning: derived LIVE from
    /// the worker's rolling realtime factor, so it recovers to OK when a downgrade fixes the lag
    /// (the transcript's one-shot "transcription lagging" marker still records that it happened).</summary>
    [ObservableProperty] private string _keepUpText = "Keeping up OK";
    /// <summary>True while the rolling realtime factor is above 1.0 - the chip's red state.</summary>
    [ObservableProperty] private bool _keepUpLagging;
```
- [ ] **Remove the one-shot lag subscription.** The ctor currently contains (lines 146–148):
```csharp
        controller.Notice += n => _dispatch(() => { LastNotice = n; NoticeRaised?.Invoke(n); });
        controller.ErrorRaised += e => _dispatch(() => { if (e == "RTF_LAGGING") IsLagging = true; });
        controller.PeakObserved += (source, peak) => _dispatch(() =>
```
Replace those three lines with:
```csharp
        controller.Notice += n => _dispatch(() => { LastNotice = n; NoticeRaised?.Invoke(n); });
        // The old one-shot RTF_LAGGING -> IsLagging subscription is gone (design 2026-07-13
        // section 5 item 4): the keep-up chip now derives lag LIVE from RecentTranscriptionRtf on
        // the existing TimerTick poll, and recovers when the worker's downgrade catches up.
        controller.PeakObserved += (source, peak) => _dispatch(() =>
```
- [ ] **Add the ready-card passthrough.** Immediately after line 87 (`public string? FinalizingSessionId => _controller.FinalizingSessionId;`) insert:
```csharp
    /// <summary>Ready-card engine chip source (design 2026-07-13 section 5 item 4): the plan Start
    /// WOULD bind right now, straight off the controller's own selector seams. The FIRST call may
    /// probe hardware - the console reads it inside its off-UI-thread refresh (Task.Run), never
    /// synchronously on the UI thread.</summary>
    public BackendPlan PreviewEnginePlan => _controller.PreviewEnginePlan;
```
- [ ] **Reset the chip at Start.** `StartAsync` currently opens (lines 240–242):
```csharp
    private async Task StartAsync()
    {
        IsLagging = false;
```
Replace with:
```csharp
    private async Task StartAsync()
    {
        // Keep-up chip: fresh-session default (design 2026-07-13 section 5 item 4); TimerTick
        // re-derives it from the new session's worker as segments arrive.
        KeepUpText = "Keeping up OK";
        KeepUpLagging = false;
```
- [ ] **Refresh eagerly at Start.** `StartAsync` currently ends (lines 269–270):
```csharp
        string? id = await Task.Run(() => _controller.StartAsync(options, CancellationToken.None));
        if (id is not null) _startedAt = _time.GetUtcNow();
```
Replace with:
```csharp
        string? id = await Task.Run(() => _controller.StartAsync(options, CancellationToken.None));
        if (id is not null) _startedAt = _time.GetUtcNow();
        // Eager chip refresh so the header never shows a blank engine chip for the first ~150 ms
        // tick of a new session (and deterministic for tests, which do not run the timer).
        RefreshEngineChips();
```
- [ ] **Refresh eagerly at Stop.** Replace `StopAsync` (lines 299–305):
```csharp
    private async Task StopAsync()
    {
        await Task.Run(() => _controller.StopAsync(CancellationToken.None));
        _startedAt = null;
        Elapsed = "00:00";
        LocalLevel.Tick(); RemoteLevel.Tick();
    }
```
with:
```csharp
    private async Task StopAsync()
    {
        await Task.Run(() => _controller.StopAsync(CancellationToken.None));
        _startedAt = null;
        Elapsed = "00:00";
        LocalLevel.Tick(); RemoteLevel.Tick();
        RefreshEngineChips();                    // Idle: chip clears, keep-up returns to OK
    }
```
- [ ] **Poll on the existing tick + add the helpers.** Replace `TimerTick` (lines 307–319):
```csharp
    /// <summary>Driven by a ~150 ms DispatcherTimer in production; tests call it directly.
    /// The elapsed clock keeps ticking through Pause (spec 2.1).</summary>
    public void TimerTick()
    {
        if (_startedAt is { } started)
        {
            var span = _time.GetUtcNow() - started;
            Elapsed = span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss",
                System.Globalization.CultureInfo.InvariantCulture);
        }
        LocalLevel.Tick();
        RemoteLevel.Tick();
    }
```
with:
```csharp
    /// <summary>Driven by a ~150 ms DispatcherTimer in production; tests call it directly.
    /// The elapsed clock keeps ticking through Pause (spec 2.1).</summary>
    public void TimerTick()
    {
        if (_startedAt is { } started)
        {
            var span = _time.GetUtcNow() - started;
            Elapsed = span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss",
                System.Globalization.CultureInfo.InvariantCulture);
        }
        LocalLevel.Tick();
        RemoteLevel.Tick();
        // Engine + keep-up chips (design 2026-07-13 section 5 item 4): polled on the same tick
        // that already drives Elapsed and the level decay - no new events, no new threads. The
        // [ObservableProperty] setters no-op on equal values, so idle ticks raise nothing.
        RefreshEngineChips();
    }

    /// <summary>Projects the controller's read-only engine surface onto the two chips. Cheap:
    /// ActiveEnginePlan/ActiveModelName/RecentTranscriptionRtf are plain field reads (no probe;
    /// only PreviewEnginePlan probes, and only the console's off-UI-thread refresh reads that).</summary>
    private void RefreshEngineChips()
    {
        EngineChipText = _controller.ActiveEnginePlan is { } plan
            ? FormatEngineChip(plan, _controller.ActiveModelName)
            : "";
        (KeepUpText, KeepUpLagging) = KeepUpChip(_controller.RecentTranscriptionRtf);
    }

    /// <summary>Chip formatting shared by the ready card (RecordingConsoleViewModel.EngineSummary)
    /// and the live header: the same model-middledot-BACKEND shape the read-view footer renders
    /// from session.json (ReadViewViewModel line 198, backend uppercased by PersistFinalAsync).
    /// plan.ModelName is already the CANONICAL name (BackendSelector strips quant file suffixes
    /// via ModelFileResolver.CanonicalName), so the chip never shows "-q8_0" file details.
    /// The middle dot is written as the \u00B7 escape so this source file stays ASCII.
    /// Public: no InternalsVisibleTo exists in this repo, and tests call it.</summary>
    public static string FormatEngineChip(BackendPlan plan, string? modelName = null)
        => $"{modelName ?? plan.ModelName} \u00B7 {plan.Backend.ToString().ToUpperInvariant()}";

    /// <summary>Pure keep-up mapping (design 2026-07-13 section 5 item 4): null (no data yet) or a
    /// factor at/below 1.0 reads "Keeping up OK"; above 1.0 reads "Lagging x{factor}" with one
    /// decimal, invariant culture. ASCII on purpose (project rule: no Unicode symbols in tests).</summary>
    public static (string Text, bool Lagging) KeepUpChip(double? rtf)
        => rtf is { } r && r > 1.0
            ? (string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Lagging x{r:0.0}"), true)
            : ("Keeping up OK", false);
```
- [ ] **Run tests and see PASS.** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~Chip" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\record-console-polish\` — expected: 3 passed. Then run the whole class to prove no regression (`Commands_gate_on_state`, `Elapsed_formats_and_resets`, mute/banner tests all touch this VM): `--filter "FullyQualifiedName~SessionViewModelTests"`.
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/SessionViewModel.cs tests/LocalScribe.App.Tests/SessionViewModelTests.cs
git commit -m "feat(app): engine + keep-up chips on SessionViewModel (absorbs the one-shot lag warning)

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: App — pre-flight target line + engine summary on `RecordingConsoleViewModel`
**Files:**
- Modify `src\LocalScribe.App\ViewModels\RecordingConsoleViewModel.cs`: add `using LocalScribe.Core.Transcription;`; two `[ObservableProperty]` strings after line 110; extend `RefreshRemoteTargetsAsync` (lines 216–229); add the pure `PreflightLine` helper after `OptionFor` (ends line 200).
- Test `tests\LocalScribe.App.Tests\RecordingConsoleViewModelTests.cs` (add two `[Fact]`s reusing the in-file `MakeConsole`/`Auto` helpers and `FakeScanner _scanner`).

**Interfaces:**
- Consumes: `SessionViewModel.PreviewEnginePlan` + `SessionViewModel.FormatEngineChip` (Task 3) via the existing `Session` property; existing `_scanner` (`IAudioSessionScanner`), `_remoteOverride.Apply(_settings.Current).Remote` (the APPLIED per-session remote setting, same source as `RemoteSummary`), `RemoteCapturePlanner.Plan/IsFullMix`, `AppKindResolver.FriendlyName`. Refresh cadence: the EXISTING `LiveViewWindow.xaml.cs` seams — `_remoteTargetPoll` (2 s, visible-only, `LiveViewWindow.xaml.cs:26,46-51`), on-show refresh, and `DropDownOpened` — no new timer, no window-code change.
- Produces:
  - `public string RecordingConsoleViewModel.PreflightSummary { get; }` (`[ObservableProperty]`, `""` until the first refresh).
  - `public string RecordingConsoleViewModel.EngineSummary { get; }` (`[ObservableProperty]`, `""` until the first refresh).
  - `public static string RecordingConsoleViewModel.PreflightLine(IReadOnlyList<AudioSessionInfo> active, RemoteSetting remote)` — pure planner-truthful mapping (public: tests call it; no InternalsVisibleTo in this repo).
- NO ctor change (the console ctor signature is pinned by `RecordingConsoleViewModelTests`/`RecordingConsoleAppSelectorTests` — both keep compiling untouched).

Steps:
- [ ] **Write the failing tests.** Append inside `RecordingConsoleViewModelTests` (before the closing brace) in `tests\LocalScribe.App.Tests\RecordingConsoleViewModelTests.cs`:
```csharp
    [Fact]
    public void PreflightLine_maps_planner_outcomes_to_ready_card_text()
    {
        // Design 2026-07-13 section 5 item 5: the line is derived from the SAME pure
        // RemoteCapturePlanner Start resolves through, so it never lies about the plan.
        var auto = new RemoteSetting { Mode = RemoteMode.Auto };
        var none = new List<AudioSessionInfo>();

        Assert.Equal("Webex detected - remote audio will be captured from it.",
            RecordingConsoleViewModel.PreflightLine(
                new List<AudioSessionInfo> { new(1, "CiscoCollabHost") }, auto));

        Assert.Equal("No call app playing audio - will record system mix.",
            RecordingConsoleViewModel.PreflightLine(none, auto));

        // A LIVE full-mix app (Teams) is detected but honestly reported as system-mix capture.
        Assert.Equal("Teams detected - will record system mix (shared-audio app).",
            RecordingConsoleViewModel.PreflightLine(
                new List<AudioSessionInfo> { new(2, "ms-teams") }, auto));

        // A pinned full-mix app that IS live reports the same honest degrade...
        Assert.Equal("Browser detected - will record system mix (shared-audio app).",
            RecordingConsoleViewModel.PreflightLine(
                new List<AudioSessionInfo> { new(3, "chrome") },
                new RemoteSetting { Mode = RemoteMode.PerProcess, App = "chrome" }));

        // ...but a pinned app that is NOT live must not claim detection (planner fallback keeps
        // plan.App = the requested image, so the helper checks live-ness before saying "detected").
        Assert.Equal("No call app playing audio - will record system mix.",
            RecordingConsoleViewModel.PreflightLine(none,
                new RemoteSetting { Mode = RemoteMode.PerProcess, App = "chrome" }));

        Assert.Equal("System mix - all system audio will be recorded.",
            RecordingConsoleViewModel.PreflightLine(none, new RemoteSetting { Mode = RemoteMode.SystemMix }));
    }

    [Fact]
    public async Task Preflight_and_engine_chip_populate_on_refresh_and_follow_the_picker()
    {
        var (console, _, _, _, _, _, _) = MakeConsole(Auto(null));
        Assert.Equal("", console.PreflightSummary);
        Assert.Equal("", console.EngineSummary);

        _scanner.Active.Add(new AudioSessionInfo(1, "CiscoCollabHost"));
        await console.RefreshRemoteTargetsAsync();
        Assert.Equal("Webex detected - remote audio will be captured from it.", console.PreflightSummary);
        // MakeConsole's controller: StaticHardwareProbe -> Cpu; Model=auto over {base.en,tiny.en}.
        Assert.Equal("base.en \u00B7 CPU", console.EngineSummary);

        // The line follows the per-session picker (the APPLIED remote setting, not raw settings).
        _scanner.Active.Add(new AudioSessionInfo(2, "Zoom"));
        var zoom = console.RemoteTargetOptions.First(o => o.Setting.App == "Zoom");
        console.SelectedRemoteTarget = zoom;
        await console.RefreshRemoteTargetsAsync();
        Assert.Equal("Zoom detected - remote audio will be captured from it.", console.PreflightSummary);

        _scanner.Active.Clear();
        await console.RefreshRemoteTargetsAsync();       // pinned Zoom no longer live -> honest fallback
        Assert.Equal("No call app playing audio - will record system mix.", console.PreflightSummary);
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~Preflight" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\record-console-polish\` — expected: `error CS0117: 'RecordingConsoleViewModel' does not contain a definition for 'PreflightLine'` (plus CS1061 on `PreflightSummary`/`EngineSummary`).
- [ ] **Add the using.** In `RecordingConsoleViewModel.cs` the using block currently ends:
```csharp
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
namespace LocalScribe.App.ViewModels;
```
Insert `using LocalScribe.Core.Transcription;` between `using LocalScribe.Core.Model;` and the namespace line.
- [ ] **Add the observable properties.** Immediately after line 110 (`public IAsyncRelayCommand<RemoteTargetOption> ChangeRemoteTargetCommand { get; }`) insert:
```csharp

    /// <summary>Ready-card pre-flight line (design 2026-07-13 section 5 item 5): what the remote
    /// leg WOULD capture right now, from the same WASAPI scan the target picker refreshes on and
    /// the same pure RemoteCapturePlanner Start resolves through. Replaces the two grey summary
    /// lines that duplicated the pickers. Informational ONLY - it NEVER gates or delays Start
    /// (locked anti-pattern, design section 7). "" until the first visible-refresh lands.</summary>
    [ObservableProperty] private string _preflightSummary = "";
    /// <summary>Ready-card engine chip (design 2026-07-13 section 5 item 4): the model+backend
    /// Start WOULD bind (settings + BackendSelector via SessionViewModel.PreviewEnginePlan),
    /// in the read-view footer's model-middledot-BACKEND shape (rendered "base.en (middot) CPU").
    /// "" until the first refresh.</summary>
    [ObservableProperty] private string _engineSummary = "";
```
- [ ] **Add the pure helper.** `OptionFor` currently ends (lines 199–200):
```csharp
        return RemoteTargetOptions.First(o => o.Setting.Mode == RemoteMode.Auto);
    }
```
Immediately after that closing brace (before the `SeedSelectedFromSettings` doc comment) insert:
```csharp

    /// <summary>Pure mapping from (active render sessions, the APPLIED remote setting) to the
    /// ready card's pre-flight line (design 2026-07-13 section 5 item 5). Planner-truthful: a
    /// per-process plan reads "detected"; a LIVE full-mix image (Teams/browsers - forced to system
    /// mix by the planner) reads detected-but-system-mix; anything else (nothing playing, or a
    /// pinned app that is not live - the planner's fallback keeps plan.App = the requested image,
    /// hence the explicit live-ness check) reads the honest system-mix fallback. Explicit system
    /// mix is stated as such. Public static: tests drive every branch directly (no
    /// InternalsVisibleTo in this repo), and it holds no console state.</summary>
    public static string PreflightLine(IReadOnlyList<AudioSessionInfo> active, RemoteSetting remote)
    {
        if (remote.Mode == RemoteMode.SystemMix)
            return "System mix - all system audio will be recorded.";
        var plan = RemoteCapturePlanner.Plan(active, remote);
        if (plan.Mode == RemoteMode.PerProcess)
            return $"{AppKindResolver.FriendlyName(plan.App) ?? plan.App} detected - remote audio will be captured from it.";
        if (plan.App is { } image && RemoteCapturePlanner.IsFullMix(image)
            && active.Any(s => s.ProcessName.Contains(image, StringComparison.OrdinalIgnoreCase)))
            return $"{AppKindResolver.FriendlyName(image) ?? image} detected - will record system mix (shared-audio app).";
        return "No call app playing audio - will record system mix.";
    }
```
- [ ] **Extend the refresh.** Replace `RefreshRemoteTargetsAsync` and its doc comment (lines 216–229):
```csharp
    /// <summary>Off-UI-thread scan (WasapiSessionScanner enumerates COM endpoints), then rebuild on
    /// the resumed context. Best-effort - a scan hiccup must never disturb the console.</summary>
    public async Task RefreshRemoteTargetsAsync()
    {
        try
        {
            var active = await Task.Run(() => _scanner.Scan());
            RebuildRemoteTargetOptions(active);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RefreshRemoteTargetsAsync failed: {ex}");
        }
    }
```
with:
```csharp
    /// <summary>Off-UI-thread scan (WasapiSessionScanner enumerates COM endpoints) + engine-plan
    /// preview (the FIRST PreviewEnginePlan call may shell out to nvidia-smi - cached after; that is
    /// why both run inside Task.Run), then rebuild on the resumed context. Driven by LiveViewWindow's
    /// EXISTING 2 s visible-only poll + on-show + DropDownOpened refreshes, so the ready card's
    /// pre-flight line and engine chip stay fresh until Start with no new timer (design 2026-07-13
    /// section 5 items 4-5). Informational only - never gates Start. Best-effort - a scan hiccup
    /// must never disturb the console.</summary>
    public async Task RefreshRemoteTargetsAsync()
    {
        try
        {
            var (active, plan) = await Task.Run(() => (_scanner.Scan(), Session.PreviewEnginePlan));
            RebuildRemoteTargetOptions(active);
            PreflightSummary = PreflightLine(active, _remoteOverride.Apply(_settings.Current).Remote);
            EngineSummary = SessionViewModel.FormatEngineChip(plan);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RefreshRemoteTargetsAsync failed: {ex}");
        }
    }
```
- [ ] **Run tests and see PASS.** Same filter — expected: 2 passed. Then run the whole class plus the existing refresh coverage: `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~RecordingConsole" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\record-console-polish\` (covers `RecordingConsoleViewModelTests` + `RecordingConsoleAppSelectorTests` — the ctor is unchanged, so both must stay green).
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs tests/LocalScribe.App.Tests/RecordingConsoleViewModelTests.cs
git commit -m "feat(app): ready-card pre-flight target line + engine summary on the Record console VM

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 5: App — `TranscriptLinesViewModel.ShowListeningHint` (empty state)
**Files:**
- Modify `src\LocalScribe.App\ViewModels\TranscriptLinesViewModel.cs`: add `using CommunityToolkit.Mvvm.ComponentModel;`; inherit `ObservableObject`; add `ShowListeningHint`; raise it from the `StateChanged` handler, `Clear`, and `RebuildFrom`.
- Test `tests\LocalScribe.App.Tests\TranscriptLinesViewModelTests.cs` (add one `[Fact]`; `GatedEngineFactory` is linked into App.Tests via `LiveTestDoubles.cs`, and the file already has `using LocalScribe.Core.Tests;`).

**Interfaces:**
- Produces: `public bool TranscriptLinesViewModel.ShowListeningHint => _lastState == SessionState.Recording && Lines.Count == 0;` with `PropertyChanged` raised on every state flip and every list rebuild/clear. The class becomes `: ObservableObject` (it previously implemented no INPC; `Lines` alone notified). Design §5.1 rule encoded exactly: only while Recording with zero lines; the FIRST line (segment or marker) removes it; Paused/Idle never show it.
- Consumes: existing `_lastState` tracking, `Lines`, the ctor's `StateChanged`/`LineInserted` subscriptions (unchanged wiring).

Steps:
- [ ] **Write the failing test.** Append inside `TranscriptLinesViewModelTests` (before the closing brace) in `tests\LocalScribe.App.Tests\TranscriptLinesViewModelTests.cs`:
```csharp
    [Fact]
    public async Task Listening_hint_shows_only_while_recording_with_no_lines()
    {
        // Design 2026-07-13 section 5 item 1. GatedEngineFactory holds the engine build closed, so
        // no transcript line can land while the gate is shut - the hint window is observable and
        // deterministic (markers would also clear it, but a clean per-process fake Start writes none).
        var gated = new GatedEngineFactory();
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        var vm = new TranscriptLinesViewModel(controller, new FakeSettingsService(), a => a());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(vm.ShowListeningHint);                       // Idle: never shown

        await controller.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.True(vm.ShowListeningHint);                        // Recording + zero lines
        Assert.Contains(nameof(TranscriptLinesViewModel.ShowListeningHint), raised);

        gated.CreateGate.Set();                                   // release transcription
        Assert.True(SpinWait.SpinUntil(() => vm.Lines.Count > 0, TimeSpan.FromSeconds(5)),
            "no transcript line ever arrived");
        Assert.False(vm.ShowListeningHint);                       // dropped at the FIRST line

        await controller.StopAsync(CancellationToken.None);
        await controller.PendingFinalize;
        Assert.False(vm.ShowListeningHint);                       // Idle again (and lines present)
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~Listening_hint" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\record-console-polish\` — expected: `error CS1061: 'TranscriptLinesViewModel' does not contain a definition for 'ShowListeningHint'` (and CS1061 on `PropertyChanged` until the base type lands).
- [ ] **Make the VM observable and add the property.** In `TranscriptLinesViewModel.cs`:
  1. Add `using CommunityToolkit.Mvvm.ComponentModel;` as the first using (before `using System.Collections.ObjectModel;`).
  2. Replace the class declaration (line 16):
```csharp
public sealed class TranscriptLinesViewModel
```
with:
```csharp
public sealed class TranscriptLinesViewModel : ObservableObject
```
  3. Immediately after line 22 (`public ObservableCollection<TranscriptLineViewModel> Lines { get; } = [];`) insert:
```csharp

    /// <summary>Empty-state hint (design 2026-07-13 section 5 item 1): true ONLY while the session
    /// is Recording and the live list has no lines yet. The XAML overlays "Listening - transcript
    /// appears a few seconds after speech." on the list and drops it at the FIRST line (segment or
    /// marker) or on Pause/Stop. Raised on every state flip and every list rebuild/clear.</summary>
    public bool ShowListeningHint => _lastState == SessionState.Recording && Lines.Count == 0;
```
- [ ] **Raise on state flips.** Replace the `StateChanged` subscription in the ctor (lines 34–38):
```csharp
        controller.StateChanged += s => _dispatch(() =>
        {
            if (s == SessionState.Recording && _lastState == SessionState.Idle) Clear();
            _lastState = s;
        });
```
with:
```csharp
        controller.StateChanged += s => _dispatch(() =>
        {
            if (s == SessionState.Recording && _lastState == SessionState.Idle) Clear();
            _lastState = s;
            OnPropertyChanged(nameof(ShowListeningHint));   // Recording gained/lost (section 5 item 1)
        });
```
- [ ] **Raise on clear.** Replace line 41:
```csharp
    public void Clear() => Lines.Clear();
```
with:
```csharp
    public void Clear()
    {
        Lines.Clear();
        OnPropertyChanged(nameof(ShowListeningHint));
    }
```
- [ ] **Raise on rebuild.** `RebuildFrom` currently ends (lines 54–57):
```csharp
        Lines.Clear();
        foreach (var r in SectionGrouper.Group(pre, gapMs))
            Lines.Add(MapRow(r));
    }
```
Replace with:
```csharp
        Lines.Clear();
        foreach (var r in SectionGrouper.Group(pre, gapMs))
            Lines.Add(MapRow(r));
        OnPropertyChanged(nameof(ShowListeningHint));   // the first line drops the hint
    }
```
(Note: `Lines.Clear()` here calls the collection's own Clear, not the VM method above — no double-raise, and the raise lands after the refill so the hint reflects the final count.)
- [ ] **Run tests and see PASS.** Same filter — expected: 1 passed. Then run the whole class: `--filter "FullyQualifiedName~TranscriptLinesViewModelTests"`.
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/TranscriptLinesViewModel.cs tests/LocalScribe.App.Tests/TranscriptLinesViewModelTests.cs
git commit -m "feat(app): listening-hint empty state on TranscriptLinesViewModel

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 6: XAML — `LiveViewWindow` polish (status header, meters, chips, pre-flight line, empty state)
**Files:**
- Modify `src\LocalScribe.App\LiveViewWindow.xaml` only (four edits below; anchors @ 7d6c88d — the file is byte-identical to 2ce2a5f, the cpu-threads merge touched no App XAML). No code-behind change: the pre-flight/engine refresh rides the existing `_remoteTargetPoll` + on-show wiring in `LiveViewWindow.xaml.cs` untouched, and the chips ride the existing 150 ms timer in `App.xaml.cs` untouched.
- No new unit test (XAML rendering is not unit-tested here). The gate is: 0-warning build + full App + Core suites green (incl. `XamlHygieneTests`) + the precise manual smoke below.

**Interfaces:**
- Consumes: `Console.PreflightSummary`/`Console.EngineSummary` (Task 4), `Session.EngineChipText`/`Session.KeepUpText`/`Session.KeepUpLagging` (Task 3), `Lines.ShowListeningHint` (Task 5), `Session.LocalLevel.Value`/`Session.RemoteLevel.Value` (existing — the overlay pill's exact level source, `OverlayWindow.xaml:20-26`, fed by `SessionController.PeakObserved` and decayed by the existing `TimerTick`), existing `BoolToVis` converter, `MutedText` style, Fluent theme brushes already used in this window (`SystemFillColorCriticalBrush`/`SystemFillColorCriticalBackgroundBrush` at lines 132–133, `SystemFillColorCautionBrush` via `WarningText`, `ControlFillColorSecondaryBrush`).
- Produces: no new types. Removes the `Session.IsLagging` binding (the VM property was removed in Task 3) and the `Console.RemoteSummary`/`Console.MicSummary` ready-card lines (the VM properties REMAIN — they are pinned by `RecordingConsoleViewModelTests` and still feed test coverage of the applied-plan derivation).

Steps:
- [ ] **Edit 1 — ready card: pre-flight line + engine chip replace the two grey summary lines.** In `LiveViewWindow.xaml` replace lines 26–29:
```xml
                <TextBlock Text="{Binding Console.RemoteSummary}" Style="{StaticResource MutedText}"
                           TextWrapping="Wrap" HorizontalAlignment="Center" />
                <TextBlock Text="{Binding Console.MicSummary}" Style="{StaticResource MutedText}"
                           TextWrapping="Wrap" HorizontalAlignment="Center" Margin="0,2,0,12" />
```
with:
```xml
                <!-- Record-console polish (design 2026-07-13 section 5 item 5): the pre-flight
                     target line replaces the two grey summary lines that duplicated the pickers
                     below. Informational ONLY - it never gates or delays Start. Refreshed by the
                     existing 2 s visible-only poll in LiveViewWindow.xaml.cs. -->
                <TextBlock Text="{Binding Console.PreflightSummary}" Style="{StaticResource MutedText}"
                           TextWrapping="Wrap" TextAlignment="Center" HorizontalAlignment="Center" />
                <!-- Item 4: the engine chip - the model+backend Start WILL bind (settings +
                     BackendSelector via SessionController.PreviewEnginePlan). Hidden until the
                     first refresh fills it so no empty pill flashes. -->
                <Border Background="{DynamicResource ControlFillColorSecondaryBrush}" CornerRadius="10"
                        Padding="10,3" HorizontalAlignment="Center" Margin="0,6,0,12">
                    <Border.Style>
                        <Style TargetType="Border">
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding Console.EngineSummary}" Value="">
                                    <Setter Property="Visibility" Value="Collapsed" />
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Border.Style>
                    <TextBlock Text="{Binding Console.EngineSummary}" Style="{StaticResource MutedText}" />
                </Border>
```
- [ ] **Edit 2 — recording header: prominent status + meters + chips.** Replace lines 92–97:
```xml
                <StackPanel DockPanel.Dock="Top" Orientation="Horizontal" Margin="0,0,0,8">
                    <TextBlock Text="{Binding Session.State}" FontWeight="SemiBold" Margin="0,0,12,0" />
                    <TextBlock Text="{Binding Session.Elapsed}" Margin="0,0,12,0" />
                    <TextBlock Text="transcription lagging"
                               Visibility="{Binding Session.IsLagging, Converter={StaticResource BoolToVis}}"
                               Style="{StaticResource WarningText}" Margin="0,0,12,0" />
```
with (the pill buttons at the old lines 98–181 stay exactly where they are, now inside the trailing inner `<StackPanel Orientation="Horizontal">`):
```xml
                <StackPanel DockPanel.Dock="Top" Margin="0,0,0,8">
                    <!-- Item 2: the recording state is the app's most safety-critical status -
                         promoted to a large red-accented header; Paused (and the brief Finalizing)
                         render equally large in the caution color. Item 3: Local/Remote mini
                         meters reuse the overlay pill's EXACT level source (SessionViewModel
                         LocalLevel/RemoteLevel fed by SessionController.PeakObserved, decayed by
                         the existing 150 ms TimerTick) - a silent leg visibly flatlines. -->
                    <StackPanel Orientation="Horizontal" Margin="0,0,0,6">
                        <Ellipse Width="12" Height="12" VerticalAlignment="Center" Margin="0,0,8,0">
                            <Ellipse.Style>
                                <Style TargetType="Ellipse">
                                    <Setter Property="Fill" Value="{DynamicResource SystemFillColorCautionBrush}" />
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Session.State}" Value="Recording">
                                            <Setter Property="Fill" Value="{DynamicResource SystemFillColorCriticalBrush}" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </Ellipse.Style>
                        </Ellipse>
                        <TextBlock Text="{Binding Session.State}" FontSize="22" FontWeight="SemiBold"
                                   VerticalAlignment="Center" Margin="0,0,12,0">
                            <TextBlock.Style>
                                <Style TargetType="TextBlock">
                                    <Setter Property="Foreground" Value="{DynamicResource SystemFillColorCautionBrush}" />
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Session.State}" Value="Recording">
                                            <Setter Property="Foreground" Value="{DynamicResource SystemFillColorCriticalBrush}" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </TextBlock.Style>
                        </TextBlock>
                        <TextBlock Text="{Binding Session.Elapsed}" FontSize="22" FontFamily="Consolas"
                                   VerticalAlignment="Center" Margin="0,0,16,0" />
                        <StackPanel VerticalAlignment="Center">
                            <StackPanel Orientation="Horizontal" Margin="0,1">
                                <TextBlock Text="Local" Style="{StaticResource MutedText}" FontSize="10"
                                           Width="42" VerticalAlignment="Center" />
                                <ProgressBar Width="64" Height="4" Maximum="1"
                                             Value="{Binding Session.LocalLevel.Value, Mode=OneWay}" />
                            </StackPanel>
                            <StackPanel Orientation="Horizontal" Margin="0,1">
                                <TextBlock Text="Remote" Style="{StaticResource MutedText}" FontSize="10"
                                           Width="42" VerticalAlignment="Center" />
                                <ProgressBar Width="64" Height="4" Maximum="1"
                                             Value="{Binding Session.RemoteLevel.Value, Mode=OneWay}" />
                            </StackPanel>
                        </StackPanel>
                    </StackPanel>
                    <!-- Item 4: engine chip + LIVE keep-up chip. The keep-up chip absorbs the old
                         one-shot "transcription lagging" TextBlock (removed above): green-path
                         "Keeping up OK", red "Lagging x1.4" while the rolling realtime factor is
                         above 1.0, recovering when a ladder downgrade catches up. -->
                    <StackPanel Orientation="Horizontal" Margin="0,0,0,6">
                        <Border Background="{DynamicResource ControlFillColorSecondaryBrush}"
                                CornerRadius="10" Padding="10,3" Margin="0,0,8,0" VerticalAlignment="Center">
                            <Border.Style>
                                <Style TargetType="Border">
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Session.EngineChipText}" Value="">
                                            <Setter Property="Visibility" Value="Collapsed" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </Border.Style>
                            <TextBlock Text="{Binding Session.EngineChipText}" Style="{StaticResource MutedText}" />
                        </Border>
                        <Border CornerRadius="10" Padding="10,3" VerticalAlignment="Center">
                            <Border.Style>
                                <Style TargetType="Border">
                                    <Setter Property="Background" Value="{DynamicResource ControlFillColorSecondaryBrush}" />
                                    <Style.Triggers>
                                        <DataTrigger Binding="{Binding Session.KeepUpLagging}" Value="True">
                                            <Setter Property="Background" Value="{DynamicResource SystemFillColorCriticalBackgroundBrush}" />
                                        </DataTrigger>
                                    </Style.Triggers>
                                </Style>
                            </Border.Style>
                            <TextBlock Text="{Binding Session.KeepUpText}">
                                <TextBlock.Style>
                                    <Style TargetType="TextBlock" BasedOn="{StaticResource MutedText}">
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding Session.KeepUpLagging}" Value="True">
                                                <Setter Property="Foreground" Value="{DynamicResource SystemFillColorCriticalBrush}" />
                                                <Setter Property="FontWeight" Value="SemiBold" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </TextBlock.Style>
                            </TextBlock>
                        </Border>
                    </StackPanel>
                    <StackPanel Orientation="Horizontal">
```
- [ ] **Edit 3 — close the new nesting after the pill buttons.** The mute button's close + the old header close currently read (lines 181–182 — this `</Button>` + `</StackPanel>` pair is unique; the other `</Button>`s are followed by `<Button ...>`):
```xml
                    </Button>
                </StackPanel>
```
Replace with (the `</Button>` keeps its original indent — minimal diff; the two closes end the new inner buttons row and the new outer header StackPanel opened in Edit 2):
```xml
                    </Button>
                    </StackPanel>
                </StackPanel>
```
- [ ] **Edit 4 — empty state overlay on the transcript list.** Replace the ListView opening (line 239):
```xml
                <ListView x:Name="LineList" ItemsSource="{Binding Lines.Lines}"
```
with:
```xml
                <Grid>
                <ListView x:Name="LineList" ItemsSource="{Binding Lines.Lines}"
```
and replace the ListView close + DockPanel close (lines 281–282):
```xml
                </ListView>
            </DockPanel>
```
with:
```xml
                </ListView>
                <!-- Item 1: empty state - visible ONLY while Recording with zero transcript lines
                     (Lines.ShowListeningHint); the FIRST line (segment or marker) removes it.
                     IsHitTestVisible False so it can never intercept list interaction. -->
                <TextBlock Text="Listening - transcript appears a few seconds after speech."
                           Style="{StaticResource MutedText}" TextWrapping="Wrap" TextAlignment="Center"
                           HorizontalAlignment="Center" VerticalAlignment="Center" Margin="24"
                           IsHitTestVisible="False"
                           Visibility="{Binding Lines.ShowListeningHint, Converter={StaticResource BoolToVis}}" />
                </Grid>
            </DockPanel>
```
(The Grid is the DockPanel's last child, so it takes the fill slot the ListView had; the ListView body's indentation is deliberately left as-is — XAML is indentation-insensitive and the smaller diff is easier to review.)
- [ ] **Build 0-warning + full App/Core suites green.** Run:
```
dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\record-console-polish\
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\record-console-polish\
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\record-console-polish\
```
Expected: build 0 warnings; App suite all green — including `XamlHygieneTests`: the new markup adds NO hardcoded `#AARRGGBB` brush (theme resources only), NO keyless `<Style TargetType="TextBlock">` in `Fluent.Shared.xaml` (that file is untouched; inline element styles in windows are allowed and already precedented at `LiveViewWindow.xaml:115`), NO `TextFillColorPrimaryBrush` literal beyond the existing root marker at line 17 (untouched — `PageAndWindowRoots_SetInheritableForeground` keeps passing); Core suite green (2 known fixture fails are pre-existing and unrelated).
- [ ] **Manual smoke (WPF — not unit-testable).** Launch the app, open the Record console, then:
  1. **Ready card:** shows ONE pre-flight line plus an engine chip (e.g. "base.en · CUDA" on this box) — the two old grey "Remote audio:/Microphone:" summary lines are gone. With Webex (CiscoCollabHost) playing audio the line reads "Webex detected - remote audio will be captured from it."; quit/mute Webex → within ~2–4 s it flips to "No call app playing audio - will record system mix" while still idle.
  2. **Start is never gated:** press Start immediately after the console opens (before any poll tick lands) — recording starts instantly; the pre-flight line never blocks or delays Start.
  3. **Status header:** while recording — red dot + large red "Recording" + large elapsed timer. Pause → equally large caution-colored "Paused"; Resume → red again. Confirm it is unmissable at a glance from across the room.
  4. **Meters:** speak → the Local bar moves; play remote/meeting audio → the Remote bar moves; press "Mute my side" → Local flatlines within ~1 s (decay) while Remote keeps moving.
  5. **Chips while recording:** engine chip shows the bound model+backend and matches what the read-view footer later shows for the same session; keep-up chip reads "Keeping up OK". Optional lag check: Settings > Transcription → model small.en + Backend CPU, speak continuously → watch for a red "Lagging x1.x" that recovers (or a "transcription lagging" marker if the one-shot downgrade fires).
  6. **Empty state:** Start and stay silent — "Listening - transcript appears a few seconds after speech." sits centered over the empty list; say one sentence → the hint disappears when the first line lands and never comes back (including after Pause/Resume).
  7. **Themes:** flip Windows light/dark — chips, meters, status colors, and the hint stay readable in both.
  8. **Narrow window:** resize to MinWidth (420): the status row, chip row, and pill-button row each stay usable without clipping the buttons off-screen.
- [ ] **Commit.**
```
git add src/LocalScribe.App/LiveViewWindow.xaml
git commit -m "feat(app): Record-console polish XAML - prominent status, meters, chips, pre-flight line, empty state

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Self-review

**(a) Spec coverage — every design §5 sub-item maps to tasks:**
- §5.1 empty state ("Listening — transcript appears a few seconds after speech.", only while recording with zero lines, removed at the first line) → **Task 5** (`ShowListeningHint`, tested via `GatedEngineFactory` so the zero-line window is deterministic) + **Task 6 Edit 4** (overlay TextBlock). The recording-only condition is in the VM predicate itself, not just panel visibility.
- §5.2 prominent status (large red-accented "Recording" + elapsed; Paused equally visible) → **Task 6 Edit 2** (22 px state + timer, critical brush for Recording, caution for Paused/Finalizing, status dot).
- §5.3 in-window level meters reusing the overlay pill's level sources → **Task 6 Edit 2** binds the SAME `Session.LocalLevel.Value`/`Session.RemoteLevel.Value` the overlay pill binds (`OverlayWindow.xaml:23,25`); no new level plumbing — flatline behavior comes from the existing `LevelMeter.Tick` decay on the existing timer.
- §5.4 engine chip + live keep-up indicator → **Task 1** (worker `RecentRtf` over the EXISTING `_rtfWindow` — no new telemetry, read-only property, per the design's Core constraint), **Task 2** (`PreviewEnginePlan` = the exact `BackendSelector.Select` seam Start uses → ready-card chip shows what WILL be used; `ActiveEnginePlan`+`ActiveModelName` = the exact `LastModel ?? Plan` resolution session.json gets → recording chip shows what the engine actually bound, tracking mid-session downgrades), **Task 3** (chip strings, lag threshold 1.0 = the worker's own `LaggingRtfThreshold`, absorbs and removes the old `IsLagging` one-shot text), **Task 4** (`EngineSummary` on the ready card), **Task 6 Edit 1/2** (both chips rendered). Rationale honored: backend + keep-up are visible before AND during the call (silent-CPU-fallback class).
- §5.5 pre-flight target check (Auto/per-app: "Webex detected" / "No call app playing audio — will record system mix", via `PreflightProbe`/`WasapiSessionScanner`, refreshed every few seconds until Start, replaces the two redundant grey summary lines) → **Task 4** (pure `PreflightLine` over `RemoteCapturePlanner.Plan` + the live `WasapiSessionScanner` scan already injected as `_scanner`; refresh rides the console's EXISTING 2 s visible-only poll — the "existing polling seam" the design prefers over a new DispatcherTimer) + **Task 6 Edit 1** (renders it; the two grey lines removed). Note: detection uses the WASAPI render-session scan (the same signal `PreflightProbe`'s Start-time check family belongs to); `PreflightProbe` itself is the Start-time PEAK probe and stays untouched — no pre-capture probe is added, so Start is never delayed (§7 anti-pattern).
- §5 testing note ("view-model states: empty→first-line transition, lag chip states, probe states, meter wiring; no new Core surface beyond exposing existing signals") → Tasks 5, 3, 4 tests respectively; meter wiring is pre-existing (`Peaks_light_levels_and_notices_surface` already covers `PeakObserved`→`LevelMeter`) so Task 6 only binds it; Core adds read-only properties only (Tasks 1–2).
- Hard constraints re-checked: no task touches capture, Start/Stop/Pause semantics, or command gating; no transcript text filtering; no global hotkeys; the pre-flight line and engine preview are read-only and best-effort (failures logged, console undisturbed).

**(b) Placeholder scan:** no TBD / "add error handling" / "similar to Task N" anywhere — every step carries full test code, full implementation code, and quotes the exact current code being replaced (re-grounded @ 7d6c88d after the base moved from 2ce2a5f: `TranscriptionWorker.cs` 26–35/96–101/150–155 — the cpu-threads merge added `_weightsFile`/`_pendingWeightsMarker` fields and the `Adopt`/weights-marker path, shifting these anchors, though every quoted code block itself is textually unchanged; `SessionController.cs` 154 (was 153; `LastWeightsFile` added to the Session record); `SessionViewModel.cs` 49–50/87/146–148/240–242/269–270/299–319, `RecordingConsoleViewModel.cs` 110/199–200/216–229 + usings, `TranscriptLinesViewModel.cs` 16/22/34–41/54–57, and `LiveViewWindow.xaml` 26–29/92–97/181–182/239/281–282 are all byte-identical between the two masters — anchors unchanged). Every run command names its exact filter, isolated BaseOutputPath, and the expected failure/pass output.

**(c) Type consistency across tasks:** `TranscriptionWorker.RecentRtf` is `double?` (Task 1) → `SessionController.RecentTranscriptionRtf` is `double?` (Task 2) → consumed by `SessionViewModel.KeepUpChip(double?)` returning `(string Text, bool Lagging)` deconstructed onto `string KeepUpText` / `bool KeepUpLagging` (Task 3) → bound as string/bool in XAML (Task 6). `BackendPlan` is now (7d6c88d) `record BackendPlan(Backend, string ModelName, int? CpuThreads = null)` — the plan's 2-arg constructions in Task 1's `Worker` helper reuse compile unchanged via the optional param, while the Task 2/3 whole-record equality asserts pass `CpuThreads: 4` explicitly because `Select` always attaches `AutoCpuThreads(fastCores 4) = 4`; `FormatEngineChip` reads only `ModelName`+`Backend`, so `CpuThreads`/`EffectiveThreads` never leak into chip text, and `Select`'s canonical ModelName means chips display "small.en", never a "-q8_0" file suffix. `BackendPlan` (record — value equality used in the Task 2/3 asserts) flows `SessionController.PreviewEnginePlan : BackendPlan` / `ActiveEnginePlan : BackendPlan?` → `SessionViewModel.PreviewEnginePlan : BackendPlan` → `SessionViewModel.FormatEngineChip(BackendPlan, string?) : string` → `EngineChipText`/`EngineSummary` strings. `ActiveModelName : string?` feeds `FormatEngineChip`'s `modelName` fallback. `PreflightLine(IReadOnlyList<AudioSessionInfo>, RemoteSetting) : string` (Task 4) matches `IAudioSessionScanner.Scan()`'s return and `RemoteTargetOverride.Apply(...).Remote`'s type; `FakeScanner.Active : List<AudioSessionInfo>` satisfies the parameter. All new members tests touch are `public` (no InternalsVisibleTo in this repo — verified); the console ctor is unchanged so `RecordingConsoleViewModelTests`/`RecordingConsoleAppSelectorTests` call sites keep compiling; the removed `IsLagging` has zero test references and its only XAML consumer is deleted in Task 6 (dead runtime binding in the 3→6 window only, same branch). `GatedEngineFactory`/`FakeEngineFactory`/`FakeTranscriptionEngine`/`LiveTestDoubles` are reachable from App.Tests via the `<Compile Include>` links in `LocalScribe.App.Tests.csproj:30-31`. New usings added exactly where needed (`LocalScribe.Core.Transcription` in `SessionViewModel.cs`, `RecordingConsoleViewModel.cs`, `SessionViewModelTests.cs`; `CommunityToolkit.Mvvm.ComponentModel` in `TranscriptLinesViewModel.cs`); `Volatile`/`.Average()` ride the project's implicit usings (already relied on by `_rtfWindow.All(...)`). All good.
