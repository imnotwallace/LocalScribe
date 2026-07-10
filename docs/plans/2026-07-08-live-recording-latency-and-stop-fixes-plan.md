# Live-Recording Latency & Stop Fixes — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make live recording capture from the instant Record is pressed (no lost opening, no blank lead-in), show transcription live, and make Stop return instantly while transcription finishes in the background — plus reframe the Auto-mode app selector as an optional override.

**Architecture:** Four independent fixes over the existing `SessionController` live pipeline, in priority order. (A) Take the synchronous Whisper model load off the Start critical path and stop the pre-flight probe from blocking capture, so the audio timeline begins at t≈0. (B) Turn Stop from "drain-then-finalize" into "cancel-then-finalize": stop both capture legs at the same instant, snapshot duration and finalize audio immediately, and drain the remaining transcription on a background task that re-finalizes the session when it completes. (C) Relabel the Auto-mode "Record this app" control as an optional override. (D) Instrument, then fix, the mid-stream choppiness.

**Tech Stack:** C# / .NET 10, WPF (Wpf.Ui), whisper.net, NAudio + CsWin32 WASAPI, xUnit. Core is WPF-free and unit-tested with `FakeClock`/`FakeProvider`/`FakeEngineFactory` (`tests/LocalScribe.Core.Tests/LiveTestDoubles.cs`).

## Global Constraints

- **No transcript/audio content deletion — evidentiary.** Audio must never be truncated or lost; a transcription failure keeps the raw recording (existing Fix #3). Every change here preserves that.
- **Core stays WPF-free.** All Core changes are unit-testable with the existing fakes; no `System.Windows` in `LocalScribe.Core`.
- **Never regress the existing suite:** gate is Core 393 (+2 known fixture fails) and App 377, build 0 warnings. Run `dotnet test` on both projects after every task.
- **Model resolution unchanged.** The Start-time fail-fast (`SessionController.cs:238-245`) and `BackendSelector` stay exactly as they are; the user's `settings.json` is `model:base.en`, `remote:auto`, mic pinned — do not change defaults.
- **No emojis in test scripts** (per user global instruction).
- **Commit after each task**; branch off `master` first (do not commit to `master`).

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/LocalScribe.Core/Live/SessionController.cs` | live session lifecycle | Modify: run the worker on `Task.Run` (capture starts before the model load); non-blocking pre-flight; cancel-then-finalize Stop; background finalize; gate Start on pending finalize |
| `src/LocalScribe.Core/Live/PreflightProbe.cs` | silent-source detection | Modify: add a peak accumulator that reads the real capture stream instead of a throwaway pre-capture source |
| `src/LocalScribe.Core/Live/AlignedAudioWriter.cs` | sample-aligned retained audio | Modify (Phase 4 only): optional gap-insertion diagnostic + monotonic stamping fix |
| `src/LocalScribe.Core/Audio/MicCaptureSource.cs` | mic capture + stamping | Modify (Phase 4 only): stamp frames by a running sample count within a leg |
| `src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs` | Record-console idle brains | Modify: expose Auto-aware app-selector label/placeholder |
| `src/LocalScribe.App/LiveViewWindow.xaml` | Record console + live view | Modify: relabel selector, bind placeholder |
| `tests/LocalScribe.Core.Tests/SessionControllerStartupTests.cs` | Phase 1 tests | Create |
| `tests/LocalScribe.Core.Tests/SessionControllerStopFinalizeTests.cs` | Phase 2 tests | Create |
| `tests/LocalScribe.Core.Tests/LiveTestDoubles.cs` | shared fakes | Modify: add a gated/slow engine helper |
| `tests/LocalScribe.App.Tests/RecordingConsoleAppSelectorTests.cs` | Phase 3 tests | Create |

---

## Phase 1 — Capture from t≈0 (fixes "missed beginning", "blank first ~7s", "no live transcription")

**Root cause (verified):** The Whisper model is built synchronously on the Start thread *before* capture starts. `WhisperEngineFactory.CreateAsync` returns `Task.FromResult(new WhisperNetEngine(...))`; `WhisperNetEngine`'s ctor (`WhisperFactory.FromPath` + `builder.Build()`, CUDA init) is the multi-second load. `TranscriptionWorker.RunAsync` awaits that already-completed task first (`:60`), so calling `worker.RunAsync(...)` at `SessionController.cs:380` **blocks** until the model is loaded — and capture only starts at `:416`. The clock started at `:219`, plus a ~2s blocking pre-flight probe (`:269-295`). Result: ~7s where nothing is captured (lost opening) and the first frame is stamped ~7000ms, so `AlignedAudioWriter` front-pads ~7s of silence.

### Task 1: Start capture without waiting for the engine build

**Files:**
- Modify: `src/LocalScribe.Core/Live/SessionController.cs:380` (wrap the worker start in `Task.Run`)
- Modify: `tests/LocalScribe.Core.Tests/LiveTestDoubles.cs` (add a gated engine factory helper)
- Create: `tests/LocalScribe.Core.Tests/SessionControllerStartupTests.cs`

**Interfaces:**
- Consumes: `IEngineFactory.CreateAsync(BackendPlan, string?, string?, CancellationToken) : Task<ITranscriptionEngine>` (unchanged signature).
- Produces: `GatedEngineFactory` test double with a public `ManualResetEventSlim CreateGate` that blocks `CreateAsync` **synchronously** until set (mirroring the real `WhisperEngineFactory`, which constructs `WhisperNetEngine` synchronously and returns `Task.FromResult`); `SessionController.StartAsync` returns without waiting on it.

> **Fix location (controller, not factory):** The production bug is that `worker.RunAsync(...)` at `SessionController.cs:380` is a direct call, and `RunAsync`'s first line `await CreateEngineAsync` resolves the real factory's `Task.FromResult(new WhisperNetEngine(...))` — an already-complete task whose value was built synchronously — so `RunAsync` runs its multi-second load inline on the Start thread before yielding at the queue read. Wrapping the start in `Task.Run` moves that synchronous prologue onto a pool thread, so `StartAsync` reaches `StartLeg` (capture) immediately regardless of how any factory builds. This is chosen over changing `WhisperEngineFactory` because (a) it defends the Start path against any synchronously-blocking factory, and (b) it is unit-testable in Core (Core tests never use the real `WhisperEngineFactory`, which needs a model file + native libs). The factory is left as-is; the wrap makes its synchronous construction harmless.

- [ ] **Step 1: Add a gated engine factory to the shared test doubles.**

Add to `tests/LocalScribe.Core.Tests/LiveTestDoubles.cs` (after `FakeEngineFactory`):

```csharp
/// <summary>Engine factory whose CreateAsync blocks SYNCHRONOUSLY until CreateGate is set, then
/// returns a completed task - mirrors the real WhisperEngineFactory (Task.FromResult(new
/// WhisperNetEngine(...)) where the ctor loads the model synchronously). With a direct
/// `worker.RunAsync(...)` call this blocks StartAsync exactly as production does today; wrapping the
/// worker start in Task.Run (Task 1) unblocks it. Also lets Stop-path tests hold the drain open
/// (Phase 2).</summary>
internal sealed class GatedEngineFactory : IEngineFactory
{
    public readonly ManualResetEventSlim CreateGate = new(initialState: false);
    public int CreateCalls;
    private readonly Func<BackendPlan, ITranscriptionEngine> _make;

    public GatedEngineFactory(Func<AudioSegment, TranscriptionResult>? transcribe = null)
        => _make = plan => new FakeTranscriptionEngine(plan.ModelName, transcribe ?? (s =>
            new TranscriptionResult($"{s.Source} {s.StartMs}-{s.EndMs}", "en", 0.0)));

    public Task<ITranscriptionEngine> CreateAsync(BackendPlan plan, string? language,
        string? initialPrompt, CancellationToken ct)
    {
        Interlocked.Increment(ref CreateCalls);
        CreateGate.Wait(ct);                                  // SYNCHRONOUS block, like the real model load
        return Task.FromResult<ITranscriptionEngine>(_make(plan));
    }
}
```

- [ ] **Step 2: Write the failing test** — Start must not block on the engine build.

Create `tests/LocalScribe.Core.Tests/SessionControllerStartupTests.cs`:

```csharp
using System.IO;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionControllerStartupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-startup-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Start_does_not_block_on_the_engine_build()
    {
        var gated = new GatedEngineFactory();                 // CreateAsync blocks synchronously until the gate is set
        var (c, provider, _, _) = LiveTestDoubles.MakeController(_root, engineFactory: gated);

        // Start must return (capture legs created, State == Recording) even though the engine
        // build is still blocked - i.e. capture is live before the model has loaded.
        var start = c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        string? id = await start.WaitAsync(TimeSpan.FromSeconds(5));   // today this TIMES OUT (RunAsync blocks inline at :380)

        Assert.NotNull(id);
        Assert.Equal(SessionState.Recording, c.State);
        Assert.True(provider.MicCreates > 0 && provider.RemoteCreates > 0);   // capture legs really started

        gated.CreateGate.Set();                               // let the (now background) engine build finish
        await c.StopAsync(CancellationToken.None);
    }
}
```

- [ ] **Step 3: Run the test to verify it fails.**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter Start_does_not_block_on_the_engine_build`
Expected: FAIL — `TimeoutException` from `WaitAsync`. `worker.RunAsync(feedCts.Token)` at `SessionController.cs:380` is a direct call; the gated factory blocks synchronously inside `RunAsync`'s first `await CreateEngineAsync`, so `StartAsync` never reaches `StartLeg` and never returns.

- [ ] **Step 4: Wrap the worker start in `Task.Run`.**

In `src/LocalScribe.Core/Live/SessionController.cs`, change line 380 from:

```csharp
                workerLoop = worker.RunAsync(feedCts.Token);
```

to:

```csharp
                // Fix (2026-07-08): run the worker on a pool thread. RunAsync's first statement is
                // `await CreateEngineAsync`, which for the real WhisperEngineFactory resolves a
                // Task.FromResult whose value (the WhisperNetEngine ctor: WhisperFactory.FromPath +
                // builder.Build(), a multi-second synchronous model/CUDA load) is built inline - so a
                // direct call would block StartAsync here, BEFORE StartLeg starts capture (lost
                // opening + blank lead-in). Task.Run moves that synchronous prologue off the Start
                // thread so capture starts immediately; the model loads concurrently and the bounded
                // worker queue absorbs the backlog until it is ready.
                workerLoop = Task.Run(() => worker.RunAsync(feedCts.Token), CancellationToken.None);
```

Then verify the C1 fault-guard comment block just below (`~:434-462`): the `workerLoop.ContinueWith(OnlyOnFaulted, ExecuteSynchronously)` still fires when a build/worker fault occurs. With `Task.Run`, `worker.RunAsync` can no longer complete synchronously before this method yields, so the "runs this continuation INLINE at attach-time" case the comment guards against no longer happens — the continuation simply fires when the pool task faults (still after `_session`/`State` are set). Leave the attach point where it is; if you touch the comment, note the sync-inline case is now unreachable. Do NOT change the continuation logic.

- [ ] **Step 5: Run the test to verify it passes.**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter Start_does_not_block_on_the_engine_build`
Expected: PASS.

- [ ] **Step 6: Run the full Core suite to confirm no regression** (the C1 fault-guard and the existing fault test must still pass with the worker now on `Task.Run`).

Run: `dotnet test tests/LocalScribe.Core.Tests`
Expected: PASS (393 +2 known). Pay attention to `SessionControllerTranscriptionFaultTests` — a factory that throws now faults the pool task; the `OnlyOnFaulted` continuation must still set `TranscriptionFailed` + write the marker, and `StopAsync` must still finalize cleanly (not throw).

- [ ] **Step 7: Commit.**

```bash
git add src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/LiveTestDoubles.cs tests/LocalScribe.Core.Tests/SessionControllerStartupTests.cs
git commit -m "fix(core): run the worker on a pool thread so capture starts before the model load"
```

### Task 2: Stop the pre-flight probe from blocking capture

**Files:**
- Modify: `src/LocalScribe.Core/Live/PreflightProbe.cs`
- Modify: `src/LocalScribe.Core/Live/SessionController.cs:269-295` (remove the pre-capture throwaway probe) and `:399-402` (fold peak sampling into the real leg's `PeakObserved`)
- Modify: `tests/LocalScribe.Core.Tests/SessionControllerStartupTests.cs` (add a silent-source test)

**Interfaces:**
- Consumes: `SessionController.PeakObserved` (already raised per captured frame from the real legs).
- Produces: `PreflightProbe.StartPeakWindow(int graceMs)` accumulator (pure, testable) that, fed per-frame peaks with timestamps, reports whether the first `graceMs` window stayed below `SilencePeakThreshold`. `SessionController` raises `SILENT_SOURCE` from the real stream instead of a throwaway source, so capture is never delayed by the probe.

> **Rationale:** After Task 1 the model load is off the path; the ~2s sequential throwaway probe (`Start()`+`Task.Delay(1s)` ×2) becomes the dominant remaining pre-capture delay AND double-opens the same endpoints. Measuring the peak from the *real* capture stream's first second removes the delay and the double-open. The warning is warn-only, so raising it ~1s into the recording (from real data) is equivalent or better; capture, audio retention, and transcription are already running during that window, so nothing is lost.

- [ ] **Step 1: Write the failing test** — a silent real leg still raises SILENT_SOURCE, and Start is not delayed by a probe.

Add to `SessionControllerStartupTests.cs`:

```csharp
    [Fact]
    public async Task Silent_real_leg_raises_SILENT_SOURCE_without_a_pre_capture_probe()
    {
        var (c, provider, _, clock) = LiveTestDoubles.MakeController(_root);
        provider.LocalFrames = () => new float[][] { new float[512], new float[512], new float[512] };  // all-zero mic
        var errors = new List<string>();
        c.ErrorRaised += e => errors.Add(e);

        // Preflight ON, but it must derive from the real stream (no throwaway sources): MicCreates
        // counts ONLY the real leg (1), not an extra probe source.
        string? id = await c.StartAsync(
            LiveTestDoubles.Options() with { RunPreflightProbe = true, ProbeWindow = TimeSpan.FromMilliseconds(50) },
            CancellationToken.None);
        Assert.NotNull(id);
        Assert.Equal(1, provider.MicCreates);                 // real leg only; no pre-capture throwaway probe
        await c.StopAsync(CancellationToken.None);
        Assert.Contains("SILENT_SOURCE", errors);
    }
```

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter Silent_real_leg_raises_SILENT_SOURCE_without_a_pre_capture_probe`
Expected: FAIL — `provider.MicCreates == 2` (throwaway probe source + real leg), because the current probe creates a separate source at `:271-272`.

- [ ] **Step 3: Add the real-stream peak accumulator to `PreflightProbe`.**

Add to `src/LocalScribe.Core/Live/PreflightProbe.cs` (keep `SilencePeakThreshold` and the existing `MeasurePeakAsync` for any remaining callers; add):

```csharp
    /// <summary>Accumulates the peak of a leg's first <paramref name="graceMs"/> of REAL captured
    /// audio (fed from SessionController's per-frame PeakObserved), so a dead/all-zeros endpoint is
    /// caught without a pre-capture throwaway probe delaying capture. Not thread-safe on its own;
    /// SessionController serializes feeds under its silent-gate. Returns true from Feed exactly once,
    /// when the window first closes, iff the window stayed below SilencePeakThreshold.</summary>
    public sealed class StartPeakWindow
    {
        private readonly int _graceMs;
        private long _startMs = -1;
        private float _peak;
        private bool _decided;

        public StartPeakWindow(int graceMs) => _graceMs = graceMs;

        /// <summary>Feed one frame's peak at session-clock nowMs. Returns true the first time the
        /// grace window elapses AND the accumulated peak never reached speech level (silent leg).</summary>
        public bool Feed(float peak, long nowMs)
        {
            if (_decided) return false;
            if (_startMs < 0) _startMs = nowMs;
            if (peak > _peak) _peak = peak;
            if (nowMs - _startMs < _graceMs) return false;
            _decided = true;
            return _peak < SilencePeakThreshold;
        }
    }
```

- [ ] **Step 4: Rewire `SessionController` to use the real-stream probe.**

In `src/LocalScribe.Core/Live/SessionController.cs`:

1. Delete the throwaway pre-flight block at `:269-295` (the `if (options.RunPreflightProbe) { ... CreateMic/CreateRemote probe ... }`).
2. Add two fields near the silent-monitor fields (`~:142`):

```csharp
    // Fix (2026-07-08): the Start-time silent-source check now reads the REAL capture stream's
    // first ProbeWindow instead of a pre-capture throwaway source, so the probe never delays
    // capture. Null when RunPreflightProbe is false. Guarded by _silentGate (fed from the capture
    // thread via PeakObserved).
    private PreflightProbe.StartPeakWindow? _localStartPeak;
    private PreflightProbe.StartPeakWindow? _remoteStartPeak;
```

3. Where the real legs are created (`~:395-402`), initialize the windows and fold the feed into the existing `PeakObserved` handlers:

```csharp
                _localStartPeak = options.RunPreflightProbe
                    ? new PreflightProbe.StartPeakWindow((int)options.ProbeWindow.TotalMilliseconds) : null;
                _remoteStartPeak = options.RunPreflightProbe
                    ? new PreflightProbe.StartPeakWindow((int)options.ProbeWindow.TotalMilliseconds) : null;

                local.PeakObserved += (s, p) =>
                {
                    PeakObserved?.Invoke(s, p);
                    CheckSilentLeg(s, localSilentMonitor, remoteSilentMonitor, clock.ElapsedMs);
                    FeedStartPeak(s, p, clock.ElapsedMs);
                };
                remote.PeakObserved += (s, p) =>
                {
                    PeakObserved?.Invoke(s, p);
                    CheckSilentLeg(s, localSilentMonitor, remoteSilentMonitor, clock.ElapsedMs);
                    FeedStartPeak(s, p, clock.ElapsedMs);
                };
```

4. Add the feed helper near `CheckSilentLeg` (`~:206`):

```csharp
    /// <summary>Fix (2026-07-08): raises SILENT_SOURCE once if a leg's first ProbeWindow of REAL
    /// audio stayed below the silence floor (dead/all-zeros endpoint), replacing the pre-capture
    /// throwaway probe. Serialized on _silentGate; each window decides at most once.</summary>
    private void FeedStartPeak(SourceKind kind, float peak, long nowMs)
    {
        var window = kind == SourceKind.Local ? _localStartPeak : _remoteStartPeak;
        if (window is null) return;
        bool silent;
        lock (_silentGate) { silent = window.Feed(peak, nowMs); }
        if (!silent) return;
        ErrorRaised?.Invoke("SILENT_SOURCE");
        Notice?.Invoke(kind == SourceKind.Local
            ? "Microphone level is near zero - check mute/input device before relying on this recording."
            : "Remote audio level is near zero - is meeting audio actually playing?");
    }
```

5. Null both windows in `StopAsync`'s teardown `finally` (alongside `_session = null`, `~:648`): `_localStartPeak = _remoteStartPeak = null;`

- [ ] **Step 5: Run the new test + the existing probe tests.**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "Silent_real_leg_raises_SILENT_SOURCE_without_a_pre_capture_probe|Preflight|SilentSource"`
Expected: PASS. Update any existing test that asserted the old two-source probe behavior (e.g. a test expecting `MicCreates == 2` under preflight) to expect `MicCreates == 1` and a real-stream SILENT_SOURCE.

- [ ] **Step 6: Full Core suite.**

Run: `dotnet test tests/LocalScribe.Core.Tests`
Expected: PASS (393 +2 known).

- [ ] **Step 7: Commit.**

```bash
git add src/LocalScribe.Core/Live/PreflightProbe.cs src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerStartupTests.cs
git commit -m "fix(core): derive SILENT_SOURCE from the real capture stream so the probe never delays capture"
```

---

## Phase 2 — Instant Stop + background transcription finalize (fixes "15s to finalise" + "sound keeps recording")

**Root cause (verified):** `StopAsync` is drain-then-finalize. It settles legs **sequentially** (`:603-604`), so the remote loopback leg literally keeps capturing real audio while the local leg's flush blocks on the slow transcriber; then `await s.WorkerLoop` (`:612`) drains the whole backlog; and only after that is `durationMs = s.Clock.ElapsedMs` read (`:637`) and the audio padded to it — so the drain's wall-time becomes trailing silence and inflated duration.

**Chosen behavior (user):** Stop halts both legs immediately, saves audio at the true stop instant (sub-second), session appears done, and the remaining transcription tail finishes on a background task that re-finalizes the session when complete.

### Task 3: Cancel both capture legs at the same instant and snapshot duration before any drain

**Files:**
- Modify: `src/LocalScribe.Core/Live/SessionController.cs` `StopAsync` (`:577-680`)
- Create: `tests/LocalScribe.Core.Tests/SessionControllerStopFinalizeTests.cs`

**Interfaces:**
- Produces: audio files padded to the true stop instant (`clock.ElapsedMs` captured *before* any worker drain), and both legs' capture stopped together (cancel `CaptureCts` up front).

- [ ] **Step 1: Write the failing test** — duration reflects the stop instant, not the drain.

Create `tests/LocalScribe.Core.Tests/SessionControllerStopFinalizeTests.cs`:

```csharp
using System.IO;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionControllerStopFinalizeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-stopfin-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Stop_records_duration_at_the_stop_instant_not_after_the_drain()
    {
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root);
        clock.ElapsedMs = 0;
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);

        clock.ElapsedMs = 34_000;                              // user talks 34s, then clicks Stop
        string? stopped = await c.StopAsync(CancellationToken.None);
        clock.ElapsedMs = 49_000;                              // clock would keep ticking during a drain

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(34_000, record!.DurationMs);              // NOT 49_000 (today it reads the post-drain clock)
    }
}
```

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter Stop_records_duration_at_the_stop_instant_not_after_the_drain`
Expected: With a `FakeClock` this may already pass on the fast fake path; the real failure it guards is the *ordering*. Keep it as a regression pin. If it passes, still apply Step 3 (the ordering is what fixes the real, slow-engine case exercised in Task 4).

- [ ] **Step 3: Snapshot duration up front and cancel capture together.**

In `StopAsync`, restructure the body so the sequence is: set `Finalizing` → **cancel `s.CaptureCts` once** (both audio loops exit together) → **snapshot `durationMs = s.Clock.ElapsedMs`** → settle both legs (now fast) → pad audio to that snapshot → close sinks. Replace the try/finally block (`:591-650`) with:

```csharp
            bool faulted = false;
            long durationMs = s.Clock.ElapsedMs;      // the true stop instant, BEFORE any drain
            if (!wasPaused) s.CaptureCts.Cancel();    // both legs' audio loops exit together (remote no longer
                                                      // keeps recording while the local leg drains)
            try
            {
                Exception? legFault = null;
                if (!wasPaused)
                {
                    legFault = await SettleLegAsync(s.Local, s.FeedCts);
                    Exception? remoteFault = await SettleLegAsync(s.Remote, s.FeedCts);
                    legFault ??= remoteFault;
                }
                if (legFault is not null)
                    ExceptionDispatchInfo.Capture(legFault).Throw();
            }
            catch
            {
                faulted = true;
                throw;
            }
            finally
            {
                try
                {
                    if (!faulted)
                        foreach (var w in s.AudioWriters) w.PadToMs(durationMs);
                }
                finally
                {
                    foreach (var w in s.AudioWriters) w.Dispose();
                    s.CaptureCts.Dispose();
                    _localStartPeak = _remoteStartPeak = null;
                }
            }
```

> Note: `s.Worker.Complete()`, `await s.WorkerLoop`, the writer-loop drain, `s.FeedCts.Dispose()`, `_session = null`, and the `SessionStore.SaveAsync`/`RegenerateProjectionsAsync` move into Task 4's background finalizer. After this task, compile-fix by temporarily calling a synchronous inline finalize (Task 4 replaces it). To keep this task self-contained and green, keep a **temporary** synchronous finalize immediately after the block above:

```csharp
            // TEMPORARY (replaced in Task 4 by the background finalizer): drain + persist inline so
            // this task stays green on its own.
            s.Worker.Complete();
            try { await s.WorkerLoop; } catch when (s.TranscriptionFailed) { }
            s.Outbox.Writer.TryComplete();
            await s.WriterLoop;
            s.FeedCts.Dispose();
            await PersistFinalAsync(s, durationMs, ct);   // extracted helper (Task 4 Step 3)
            _session = null;
            SetState(SessionState.Idle);
            return s.Id;
```

Extract the persistence into a helper so Task 4 can reuse it (add near `StopAsync`):

```csharp
    private async Task PersistFinalAsync(Session s, long durationMs, CancellationToken ct)
    {
        await new SessionStore(_paths.SessionJson(s.Id)).SaveAsync(s.LiveRecord with
        {
            EndedAtUtc = _time.GetUtcNow(),
            DurationMs = durationMs,
            SegmentCount = s.Merger.View.Count(l => l.Kind == TranscriptKind.Segment),
            MarkerCount = s.Merger.View.Count(l => l.Kind == TranscriptKind.Marker),
            Model = s.LastModel.Value ?? s.Plan.ModelName,
            Backend = s.Plan.Backend.ToString().ToUpperInvariant(),
            Language = s.Language.Locked ?? s.Settings.Language,
            RetainedAudioSources = s.Retained,
        }, ct);
        await new SessionWriter(_paths, s.Settings, _time).RegenerateProjectionsAsync(s.Id, ct);
    }
```

- [ ] **Step 4: Run the new test + full Core suite.**

Run: `dotnet test tests/LocalScribe.Core.Tests`
Expected: PASS (393 +2 known). Existing `SessionControllerTests`/`PauseTests`/`TranscriptionFaultTests` still green (padding target and clean-stop semantics unchanged; only the *timing of the snapshot* and *when capture stops* changed).

- [ ] **Step 5: Commit.**

```bash
git add src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerStopFinalizeTests.cs
git commit -m "fix(core): Stop snapshots duration at the stop instant and stops both legs together"
```

### Task 4: Drain the transcription tail on a background task; Stop returns Idle immediately

**Files:**
- Modify: `src/LocalScribe.Core/Live/SessionController.cs` (`StopAsync`, add `_pendingFinalize` field + `FinalizeInBackgroundAsync`)
- Modify: `tests/LocalScribe.Core.Tests/SessionControllerStopFinalizeTests.cs`

**Interfaces:**
- Produces: `Task SessionController.PendingFinalize` (never-null; `Task.CompletedTask` when idle) that completes when a background transcription drain + re-finalize has finished. `StopAsync` returns after audio is finalized and `State == Idle`, without awaiting the drain.

- [ ] **Step 1: Write the failing test** — Stop returns before the (gated) engine drains; the tail lands after.

Add to `SessionControllerStopFinalizeTests.cs`:

```csharp
    [Fact]
    public async Task Stop_returns_before_transcription_finishes_then_backfills_the_transcript()
    {
        var gated = new GatedEngineFactory();                 // engine build blocked = transcription cannot drain
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);
        clock.ElapsedMs = 5_000;

        // Stop must return promptly and go Idle even though the engine build (and thus the drain) is blocked.
        string? stopped = await c.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(id, stopped);
        Assert.Equal(SessionState.Idle, c.State);
        Assert.False(c.PendingFinalize.IsCompleted);          // tail still draining in the background

        var flac = new FileInfo(paths.AudioFile(id!, SourceKind.Local, AudioFormat.Flac));
        Assert.True(flac.Length > 0);                         // audio already finalized at Stop

        gated.CreateGate.Set();                               // let the background drain + re-finalize complete
        await c.PendingFinalize.WaitAsync(TimeSpan.FromSeconds(10));
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.NotNull(record!.EndedAtUtc);                   // session finalized after the tail landed
    }
```

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter Stop_returns_before_transcription_finishes_then_backfills_the_transcript`
Expected: FAIL — `StopAsync` times out (the Task 3 TEMPORARY inline finalize awaits `s.WorkerLoop`, which is blocked on the gated engine).

- [ ] **Step 3: Replace the temporary inline finalize with a background finalizer.**

Add the field near `_session` (`~:67`):

```csharp
    // Fix (2026-07-08): a completed Stop kicks the remaining transcription drain + re-finalize onto
    // this task and returns Idle immediately. Task.CompletedTask when no finalize is in flight. A
    // new StartAsync awaits this first so the old engine is disposed before a new one is created.
    private Task _pendingFinalize = Task.CompletedTask;
    public Task PendingFinalize => _pendingFinalize;
```

Replace the TEMPORARY block from Task 3 Step 3 with:

```csharp
            _pendingFinalize = FinalizeInBackgroundAsync(s, durationMs);
            _session = null;
            SetState(SessionState.Idle);
            return s.Id;
```

Add the background finalizer:

```csharp
    /// <summary>Fix (2026-07-08): drains the remaining transcription backlog and persists the final
    /// session.json + projections AFTER Stop has already finalized audio and returned Idle. Audio is
    /// complete and closed before this runs (Task 3), so a slow/failed drain never affects the raw
    /// recording. Swallows a worker fault the same way the synchronous path did (already surfaced
    /// mid-session via TRANSCRIPTION_FAILED). Never throws to an unobserved task.</summary>
    private async Task FinalizeInBackgroundAsync(Session s, long durationMs)
    {
        try
        {
            s.Worker.Complete();
            try { await s.WorkerLoop; } catch when (s.TranscriptionFailed) { /* audio-only finalize */ }
            s.Outbox.Writer.TryComplete();
            await s.WriterLoop;
            s.FeedCts.Dispose();
            await PersistFinalAsync(s, durationMs, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The session folder + a live session.json already exist; the launch-time recovery scan
            // (SessionWriter.RecoverIfNeededAsync) will finalize it as Recovered if this never
            // completed. Surface for diagnostics, never crash the app.
            ErrorRaised?.Invoke("FINALIZE_FAILED");
            Notice?.Invoke("Finalizing the transcript failed - the recording is safe; re-open the session to retry.");
            _ = ex;
        }
    }
```

- [ ] **Step 4: Run the new test + full Core suite.**

Run: `dotnet test tests/LocalScribe.Core.Tests`
Expected: PASS (393 +2 known). `SessionControllerTranscriptionFaultTests` still green (fault path now finalizes in the background; if that test asserts on `session.json` right after `StopAsync`, update it to `await c.PendingFinalize` first).

- [ ] **Step 5: Commit.**

```bash
git add src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerStopFinalizeTests.cs
git commit -m "feat(core): Stop finalizes audio instantly and drains transcription in the background"
```

### Task 5: Gate a new Start on the pending finalize (avoid two engines at once)

**Files:**
- Modify: `src/LocalScribe.Core/Live/SessionController.cs` `StartAsync` (`:208-217`)
- Modify: `tests/LocalScribe.Core.Tests/SessionControllerStopFinalizeTests.cs`

**Interfaces:**
- Consumes: `_pendingFinalize`.
- Produces: `StartAsync` awaits any in-flight background finalize before opening a new engine, so at most one Whisper engine is resident at a time (no VRAM double-load).

- [ ] **Step 1: Write the failing test** — a new Start waits for the prior finalize.

Add to `SessionControllerStopFinalizeTests.cs`:

```csharp
    [Fact]
    public async Task Start_waits_for_a_prior_background_finalize()
    {
        var gated = new GatedEngineFactory();
        var (c, _, _, clock) = LiveTestDoubles.MakeController(_root, engineFactory: gated);
        string? id1 = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        gated.CreateGate.Set();                                // first session's engine builds
        clock.ElapsedMs = 3_000;
        await c.StopAsync(CancellationToken.None);             // finalize runs in background

        gated.CreateGate.Reset();                             // block the SECOND session's engine build
        var start2 = c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        // Start must not deadlock waiting on the prior finalize (which is already done here) and must
        // reach Recording once its own gate opens.
        gated.CreateGate.Set();
        string? id2 = await start2.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(id2);
        Assert.NotEqual(id1, id2);
        await c.StopAsync(CancellationToken.None);
    }
```

- [ ] **Step 2: Run to verify it fails/hangs** (before the guard, an overlapping finalize + new engine can race; the test pins the awaited ordering).

Run: `dotnet test tests/LocalScribe.Core.Tests --filter Start_waits_for_a_prior_background_finalize`
Expected: FAIL or flaky without the guard.

- [ ] **Step 3: Await the pending finalize at the top of `StartAsync`.**

In `StartAsync`, immediately after `if (State != SessionState.Idle) { ... }` passes (`~:217`), before creating the clock:

```csharp
            // Fix (2026-07-08): a previous session's transcription tail may still be draining on a
            // background task (holds the old whisper engine). Wait for it so we never hold two
            // engines at once (VRAM double-load / OOM). Fast when already complete. Swallows a prior
            // finalize fault - it was surfaced by FinalizeInBackgroundAsync already.
            if (!_pendingFinalize.IsCompleted)
            {
                Notice?.Invoke("Finishing the previous recording's transcript...");
                try { await _pendingFinalize; } catch { }
            }
            _pendingFinalize = Task.CompletedTask;
```

- [ ] **Step 4: Run the new test + full Core suite.**

Run: `dotnet test tests/LocalScribe.Core.Tests`
Expected: PASS (393 +2 known).

- [ ] **Step 5: Commit.**

```bash
git add src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerStopFinalizeTests.cs
git commit -m "fix(core): a new Start waits for the prior background finalize (one engine at a time)"
```

---

## Phase 3 — Reframe the Auto-mode app selector as an optional override (Issue 4, UX-only)

**Root cause (verified):** Not a bug. `settings.json` is `remote:auto`, which auto-detects Webex (`RemoteCapturePlanner.Plan`, `:48-55`); "Webex — per-app" in the session list is auto-detect *working*. But `ShowAppSelector` was widened to show the picker in Auto too (`RecordingConsoleViewModel.cs:47`) with no "blank = auto-detect" affordance, so it reads as a mandatory prospective pick. Do **not** change the planner/override logic.

### Task 6: Auto-aware label + placeholder for the app selector

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs`
- Modify: `src/LocalScribe.App/LiveViewWindow.xaml:29-39`
- Create: `tests/LocalScribe.App.Tests/RecordingConsoleAppSelectorTests.cs`

**Interfaces:**
- Produces: `RecordingConsoleViewModel.AppSelectorLabel` and `.AppSelectorPlaceholder` (string, notify on settings change), Auto vs PerProcess aware.

- [ ] **Step 1: Write the failing test.**

Create `tests/LocalScribe.App.Tests/RecordingConsoleAppSelectorTests.cs`:

```csharp
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class RecordingConsoleAppSelectorTests
{
    [Fact]
    public void Auto_mode_labels_the_selector_as_an_optional_override()
    {
        var vm = RecordingConsoleTestHarness.Make(new Settings { Remote = new RemoteSetting { Mode = RemoteMode.Auto } });
        Assert.Equal("Override app (optional)", vm.AppSelectorLabel);
        Assert.Contains("Auto-detect", vm.AppSelectorPlaceholder);   // "blank = auto-detect" affordance
        Assert.True(vm.ShowAppSelector);
    }

    [Fact]
    public void PerProcess_mode_labels_the_selector_as_the_target_app()
    {
        var vm = RecordingConsoleTestHarness.Make(new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Webex" } });
        Assert.Equal("Record this app", vm.AppSelectorLabel);
    }
}
```

> If no `RecordingConsoleTestHarness` exists, reuse the construction pattern from the existing `RecordingConsoleViewModel` tests in `tests/LocalScribe.App.Tests` (same ctor: `ISettingsService`, `SessionViewModel`, `RemoteAppOverride`, `MaintenanceService`, `MatterSelectionOverride`, `ICaptureDeviceEnumerator`, `MicOverride`, `Action<Action>`), constructing the fakes those tests already use.

- [ ] **Step 2: Run to verify it fails.**

Run: `dotnet test tests/LocalScribe.App.Tests --filter RecordingConsoleAppSelectorTests`
Expected: FAIL — `AppSelectorLabel`/`AppSelectorPlaceholder` do not exist.

- [ ] **Step 3: Add the properties to `RecordingConsoleViewModel`.**

After `ShowAppSelector` (`:47`):

```csharp
    /// <summary>Auto mode auto-detects the call app (Webex/Zoom); the selector is an OPTIONAL
    /// override there, so it is labelled and place-held to say so. PerProcess mode is the app you
    /// pinned, so it keeps the imperative label. Notifies on settings change alongside ShowAppSelector.</summary>
    public string AppSelectorLabel => _settings.Current.Remote.Mode == RemoteMode.PerProcess
        ? "Record this app" : "Override app (optional)";

    public string AppSelectorPlaceholder => _settings.Current.Remote.Mode == RemoteMode.PerProcess
        ? "App to record" : "Auto-detecting the call app (Webex/Zoom) - leave blank";
```

In `OnSettingsChanged`'s dispatch body (near `OnPropertyChanged(nameof(ShowAppSelector))`, `:271`), add:

```csharp
            OnPropertyChanged(nameof(AppSelectorLabel));
            OnPropertyChanged(nameof(AppSelectorPlaceholder));
```

- [ ] **Step 4: Bind them in XAML.**

In `src/LocalScribe.App/LiveViewWindow.xaml:29-39`, replace the label `TextBlock` text and add a placeholder to the editable `ComboBox`:

```xml
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,0,0,4"
                            Visibility="{Binding Console.ShowAppSelector, Converter={StaticResource BoolToVis}}">
                    <TextBlock Text="{Binding Console.AppSelectorLabel}" VerticalAlignment="Center" Margin="0,0,8,0" />
                    <ComboBox IsEditable="True" IsTextSearchEnabled="False" MinWidth="220"
                              ItemsSource="{Binding Console.AppSuggestions}"
                              Text="{Binding Console.SessionTargetApp, UpdateSourceTrigger=PropertyChanged}"
                              ToolTip="{Binding Console.AppSelectorPlaceholder}" />
                </StackPanel>
                <TextBlock Text="{Binding Console.AppSelectorPlaceholder}"
                           Style="{StaticResource MutedText}" TextWrapping="Wrap" TextAlignment="Center"
                           HorizontalAlignment="Center" Margin="0,2,0,16"
                           Visibility="{Binding Console.ShowAppSelector, Converter={StaticResource BoolToVis}}" />
```

(Keep the existing "Applies to this recording only..." note directly below, or fold it into the muted line above.)

- [ ] **Step 5: Run the test + full App suite.**

Run: `dotnet test tests/LocalScribe.App.Tests`
Expected: PASS (377 + 2 new).

- [ ] **Step 6: Commit.**

```bash
git add src/LocalScribe.App/ViewModels/RecordingConsoleViewModel.cs src/LocalScribe.App/LiveViewWindow.xaml tests/LocalScribe.App.Tests/RecordingConsoleAppSelectorTests.cs
git commit -m "fix(app): frame the Auto-mode app selector as an optional override (blank = auto-detect)"
```

---

## Phase 4 — Choppy mid-stream audio (Issue 3B: instrument, confirm, then fix)

**Leading hypothesis (needs runtime confirmation):** `AlignedAudioWriter.Write` positions each frame by its wall-clock delivery timestamp (`frame.StartMs`), and `MicCaptureSource` stamps frames with `_clock.ElapsedMs` at callback time. Under transcription CPU/GPU + GC load the capture callback is delayed, the next frame's stamp jumps ahead, and `AlignedAudioWriter` injects silence to "catch up" → stutter that ramps up as load builds. This is the one symptom that cannot be proven statically — instrument first (systematic-debugging Phase 1), then fix only if confirmed.

### Task 7: Instrument silence-gap insertions and reproduce

**Files:**
- Modify: `src/LocalScribe.Core/Live/AlignedAudioWriter.cs`

- [ ] **Step 1: Add a mid-stream gap diagnostic (no behavior change).**

In `AlignedAudioWriter`, add an optional callback and raise it when `Write` inserts a gap after the first frame:

```csharp
    /// <summary>Diagnostic (2026-07-08): raised when Write inserts a MID-STREAM silence gap (a gap
    /// after the first real frame), with (sourceKind, gapSamples, frameStartMs). Used to confirm the
    /// choppiness root cause on real hardware; no behavior change. Null in production unless wired.</summary>
    public Action<SourceKind, long, long>? MidStreamGapInserted;
```

In `Write`, capture whether this is the first frame and raise on a post-first gap:

```csharp
    public void Write(AudioFrame frame)
    {
        long expectedStart = frame.StartMs * _sampleRate / 1000;
        long gap = expectedStart - SamplesWritten;
        if (gap > 0 && SamplesWritten > 0)
            MidStreamGapInserted?.Invoke(frame.Source, gap, frame.StartMs);   // diagnostic only
        while (gap > 0) { /* unchanged */ }
        // ... unchanged
    }
```

Wire it in `SessionController` where `AlignedAudioWriter` is created (`:386-391`) to `Diag`/`Notice` under a debug flag, or log via `System.Diagnostics.Debug.WriteLine`. Build a Release, record a ~60s Webex call, and inspect the log.

- [ ] **Step 2: Reproduce and record evidence.**

Run the app, record ~60s with speech throughout, stop. Confirm whether mid-stream gaps are logged and whether they cluster when transcription is busy. **Decision gate:** if gaps are frequent → proceed to Task 8. If not, the choppiness is elsewhere (e.g. loopback `DATA_DISCONTINUITY`, which `ProcessLoopbackCapture` already logs via `Diag`) — re-scope with the new evidence before writing a fix.

- [ ] **Step 3: Commit the instrumentation (kept; it is cheap and diagnostic).**

```bash
git add src/LocalScribe.Core/Live/AlignedAudioWriter.cs src/LocalScribe.Core/Live/SessionController.cs
git commit -m "chore(core): diagnostic for mid-stream silence-gap insertions (choppiness triage)"
```

### Task 8: Stamp mic frames by a monotonic sample count within a leg (apply only if Task 7 confirms)

**Files:**
- Modify: `src/LocalScribe.Core/Audio/MicCaptureSource.cs`
- Create: a unit test asserting contiguous frames get contiguous stamps regardless of clock jitter

**Interfaces:**
- Produces: `MicCaptureSource` frames stamped from a running sample count anchored at the leg's first callback, so a delayed callback no longer opens a phantom silence gap. (Cross-leg alignment still holds because both legs anchor at their own first frame near t≈0 after Phase 1.)

- [ ] **Step 1: Write the failing test** — two contiguous 100ms frames delivered with a late second callback still stamp contiguously.

```csharp
// tests/LocalScribe.Core.Tests/MicCaptureStampingTests.cs
// Drive MicCaptureSource's stamping logic via an extracted pure helper:
//   long StampMs(int emittedSamples) => firstStampMs + emittedSamples * 1000 / 16000;
// Assert frame2.StartMs == frame1.StartMs + 100 even when the clock jumped +300ms between callbacks.
```

- [ ] **Step 2: Run to verify it fails.**

- [ ] **Step 3: Implement monotonic stamping.**

In `MicCaptureSource`, track `_emitted16k` and stamp from an anchor captured on the first frame:

```csharp
    private long _anchorMs = -1;
    private long _emitted16k;

    // in OnData, replacing the FrameAvailable stamp:
    if (mono16k.Length > 0)
    {
        if (_anchorMs < 0) _anchorMs = _clock.ElapsedMs;                 // anchor at first frame (~t0 after Phase 1)
        long startMs = _anchorMs + _emitted16k * 1000 / 16000;          // contiguous within the leg
        _emitted16k += mono16k.Length;
        FrameAvailable?.Invoke(new AudioFrame(Source, startMs, mono16k));
    }
```

Reset `_anchorMs = -1; _emitted16k = 0;` in `Stop()` so a Resume leg re-anchors.

- [ ] **Step 4: Run the test + full Core suite; then re-record and confirm the choppiness is gone.**

- [ ] **Step 5: Commit.**

```bash
git commit -am "fix(core): stamp mic frames by a monotonic sample count so clock jitter cannot inject gaps"
```

> **Caveat to verify on hardware:** monotonic stamping trades "aligned to wall-clock" for "no phantom gaps." If the mic hardware genuinely drops samples (overrun), this hides it as drift rather than silence — acceptable for continuous speech, but confirm against the retained file length vs the session clock after a long recording.

---

## Post-implementation

- [ ] **Whole-branch review** (`superpowers:requesting-code-review` / opus): the live-recording path is evidentiary and concurrency-heavy — focus the review on the Stop reordering (audio finalized before the background drain; no lost samples), the C1 fault-guard interaction with the now-async engine build, and the Start-waits-for-finalize gate.
- [ ] **GUI smoke runbook** (user): record a real Webex call; verify (1) speech from t=0 is captured with no blank lead-in and live text appears within a couple seconds, (2) Stop returns immediately and the transcript backfills, (3) the Auto console reads as auto-detect with an optional override, (4) audio is not choppy (Phase 4).
- [ ] **Update `docs/specs/localscribe-specs.md`** §2.1 (Stop = instant audio finalize + background transcription finalize) and §12.3 (pre-flight probe now reads the real stream).

## Self-Review notes

- **Spec coverage:** Issue 1a/1b/3a → Phase 1 (Tasks 1-2); Issue 2 → Phase 2 (Tasks 3-5); Issue 4 → Phase 3 (Task 6); Issue 3b → Phase 4 (Tasks 7-8, evidence-gated).
- **Type consistency:** `PendingFinalize`/`_pendingFinalize`, `FinalizeInBackgroundAsync`, `PersistFinalAsync`, `StartPeakWindow.Feed`, `AppSelectorLabel`/`AppSelectorPlaceholder`, `GatedEngineFactory.CreateGate` are used consistently across tasks.
- **Ordering risk:** Task 3 introduces a TEMPORARY inline finalize that Task 4 replaces — Task 3 is independently green, Task 4 swaps it for the background path. Do not skip Task 4.
