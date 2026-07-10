# Mute Controls & Echo Dedup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the user a reliable "Mute my side" control in LocalScribe (in-app mute of Webex/Teams never stops LocalScribe — it records the raw mic by design), surface device-level mic mute instantly, extend the echo dedup to hide the user's own voice echoed back on the remote leg, and spike the UIA app-mute probe.

**Architecture:** Four phases per the committed design (`docs/plans/2026-07-10-mute-controls-and-echo-dedup-design.md` — READ IT FIRST; it carries the research facts and the user-locked semantics). Phase A: `SessionController.SetLocalMuteAsync` — muted = the local leg is not captured at all (one-sided Pause; privilege protection), bracketed by evidentiary markers; console toggle button. Phase B: `MicCaptureSource` watches its endpoint's master mute (`IAudioEndpointVolume` via NAudio) and the controller surfaces it as markers + a console banner. Phase C: `PhantomBleedDedup` becomes bidirectional with a containment text metric (threshold VALUES unchanged). Phase D: a UIA probe tool + user runbook — the advisory app-mute watcher is planned only AFTER the spike findings.

**Tech Stack:** C# / .NET 10, WPF (Wpf.Ui), NAudio 2.2.1 (endpoint volume), xUnit. Core is WPF-free; tests use `tests/LocalScribe.Core.Tests/LiveTestDoubles.cs` (`MakeController`, `FakeProvider`, `FakeCaptureSource`, `FakeClock`).

## Context for a fresh session

- Branch **`mute-controls-echo-dedup`** already exists @ `e3a808c` (design committed) — check it out; do NOT work on `master`.
- This builds on the merged live-recording fixes (master @ `821a572`): `StopAsync` is halt-then-finalize with a background `PendingFinalize`; **tests that read persisted state after `StopAsync` must `await c.PendingFinalize` first** (existing tests show the pattern).
- Baselines: **Core 417 passing + 2 KNOWN fixture failures** (`Der_within_baseline_plus_epsilon`, `Golden_pair_wer_stays_at_baseline` — pre-existing, NOT regressions), **App 381 passing**, build 0 warnings. (A running `LocalScribe.App.exe` locks Core.dll → MSB3027 copy error, not a compile error — report, don't kill processes.)
- Start a FRESH SDD ledger at `.superpowers/sdd/progress.md` (the current content is from the completed live-recording run).
- `SessionController.cs` facts you'll rely on: all lifecycle methods serialize on `_gate`; markers are written via `s.Outbox.Writer.TryWrite(new MarkerAt(Markers.X, s.Clock.ElapsedMs))` (`MarkerAt` is a private nested record); the per-leg `SilentLegMonitor`s are guarded by `_silentGate` and `Reset(nowMs)` returns whether the leg was flagged (raise `SilentLegCleared` for symmetry — see `ResumeAsync` for the exact pattern); `_localStartPeak` is the Start-only silent-source probe window (abandoned on Resume); `_session` and `_finalizing` are `volatile`. Locate all edit points by CONTENT, not line numbers.

## Global Constraints

- **Evidentiary:** transcript/audio content is never deleted or truncated. Mute = *not capturing* (like Pause) — never removing captured content. Dedup is render-only (JSONL keeps both copies). Markers are written only from exact signals (LocalScribe's own mute, device mute) — NEVER from the UIA advisory signal.
- **`PhantomBleedOptions` threshold VALUES unchanged** (`NearWindowMs=750, MinSimilarity=0.85, MinRmsGapDb=3.0, TextOnlyMinSimilarity=0.975`) — golden-corpus-gated; this plan adds mechanism only.
- **Resume never silently unmutes** (user-locked): a muted local leg stays muted across Pause/Resume until explicitly unmuted.
- Core stays WPF-free; no UIA/FlaUI references outside `tools/`. No Unicode emojis in tests. Suite gate: no NEW failures beyond the 2 known Core fixtures; build 0 warnings. Run `dotnet test tests/LocalScribe.Core.Tests` and `dotnet test tests/LocalScribe.App.Tests` after every task. Commit after each task.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `src/LocalScribe.Core/Model/Markers.cs` | canonical marker strings | Modify: +4 markers |
| `src/LocalScribe.Core/Live/SessionController.cs` | lifecycle | Modify: `SetLocalMuteAsync`, `LocalMuted`/`LocalMuteChanged`, Resume honors mute, device-mute hook + `MicDeviceMuteChanged` |
| `src/LocalScribe.Core/Audio/IEndpointMuteObservable.cs` | device-mute seam | Create |
| `src/LocalScribe.Core/Audio/MicCaptureSource.cs` | mic capture | Modify: retain MMDevice, endpoint-mute events |
| `src/LocalScribe.Core/Audio/FakeCaptureSource.cs` | test double | Modify: implement the seam |
| `src/LocalScribe.Core/Projection/TextDistance.cs` | text metrics | Modify: +`ContainmentSimilarity` |
| `src/LocalScribe.Core/Projection/PhantomBleedDedup.cs` | echo dedup | Modify: bidirectional |
| `src/LocalScribe.App/ViewModels/SessionViewModel.cs` | session VM | Modify: mute command/state, device-mute flag |
| `src/LocalScribe.App/LiveViewWindow.xaml` | Record console | Modify: mute button, state line, device banner |
| `tools/UiaProbe/` | Phase-D spike | Create (console tool) |
| `tests/LocalScribe.Core.Tests/SessionControllerMuteTests.cs` | Phase A+B tests | Create |
| `tests/LocalScribe.Core.Tests/LiveTestDoubles.cs` | fakes | Modify: device-mute knobs |
| `tests/LocalScribe.Core.Tests/TextDistanceTests.cs` (or existing) | metric tests | Modify/Create |
| `tests/LocalScribe.Core.Tests/PhantomBleedDedupTests.cs` | dedup tests | Modify: new-direction facts |
| `tests/LocalScribe.App.Tests/SessionViewModelTests.cs` | VM tests | Modify: mute + banner facts |

---

## Phase A — "Mute my side" (the reliable core)

### Task 1: Core mute semantics — `SetLocalMuteAsync`

**Files:**
- Modify: `src/LocalScribe.Core/Model/Markers.cs`
- Modify: `src/LocalScribe.Core/Live/SessionController.cs`
- Create: `tests/LocalScribe.Core.Tests/SessionControllerMuteTests.cs`

**Interfaces:**
- Produces: `Task SessionController.SetLocalMuteAsync(bool muted, CancellationToken ct)`; `bool SessionController.LocalMuted` (false when Idle); `event Action<bool>? LocalMuteChanged` (fires from worker threads like every controller event — handlers marshal); markers `Markers.LocalMuted = "microphone muted by user"`, `Markers.LocalUnmuted = "microphone unmuted"`. `ResumeAsync` restarts the local leg only when `!LocalMuted`.

- [ ] **Step 1: Write the failing tests.**

Create `tests/LocalScribe.Core.Tests/SessionControllerMuteTests.cs`:

```csharp
using System.IO;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using NAudio.Wave;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionControllerMuteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-mute-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Mute_and_unmute_write_markers_and_restart_a_fresh_local_leg()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.False(c.LocalMuted);

        clock.ElapsedMs = 2000;
        await c.SetLocalMuteAsync(true, CancellationToken.None);
        Assert.True(c.LocalMuted);
        Assert.Equal(SessionState.Recording, c.State);          // mute is not Pause: session keeps recording
        Assert.Equal(1, provider.MicCreates);                   // leg stopped, no new source

        clock.ElapsedMs = 5000;
        await c.SetLocalMuteAsync(false, CancellationToken.None);
        Assert.False(c.LocalMuted);
        Assert.Equal(2, provider.MicCreates);                   // fresh local leg, like Resume

        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.LocalMuted && l.StartMs == 2000);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.LocalUnmuted && l.StartMs == 5000);
    }

    [Fact]
    public async Task Mute_is_idempotent_no_duplicate_markers()
    {
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root);
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 1000;
        await c.SetLocalMuteAsync(true, CancellationToken.None);
        await c.SetLocalMuteAsync(true, CancellationToken.None);   // second call: no-op
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Single(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.LocalMuted);
    }

    [Fact]
    public async Task Resume_honors_mute_and_never_silently_unmutes()
    {
        var (c, provider, _, clock) = LiveTestDoubles.MakeController(_root);
        await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 1000;
        await c.SetLocalMuteAsync(true, CancellationToken.None);
        clock.ElapsedMs = 2000;
        await c.PauseAsync(CancellationToken.None);
        clock.ElapsedMs = 3000;
        await c.ResumeAsync(CancellationToken.None);

        Assert.True(c.LocalMuted);                              // still muted after Resume
        Assert.Equal(1, provider.MicCreates);                   // local leg NOT restarted
        Assert.Equal(2, provider.RemoteCreates);                // remote restarted normally

        await c.SetLocalMuteAsync(false, CancellationToken.None);
        Assert.Equal(2, provider.MicCreates);                   // explicit unmute restarts it
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
    }

    [Fact]
    public async Task Mute_while_paused_flips_state_and_marker_only()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 1000;
        await c.PauseAsync(CancellationToken.None);
        clock.ElapsedMs = 2000;
        await c.SetLocalMuteAsync(true, CancellationToken.None); // no legs run while paused
        Assert.True(c.LocalMuted);
        Assert.Equal(1, provider.MicCreates);
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.LocalMuted && l.StartMs == 2000);
    }

    [Fact]
    public async Task Stop_while_muted_finalizes_with_audio_padded_to_the_stop_instant()
    {
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(
            _root, new Model.Settings { AudioFormat = Model.AudioFormat.Wav });
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 1000;
        await c.SetLocalMuteAsync(true, CancellationToken.None);
        clock.ElapsedMs = 6000;
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;

        using var r = new WaveFileReader(paths.AudioFile(id!, Audio.SourceKind.Local, Model.AudioFormat.Wav));
        Assert.Equal(96000, r.Length / r.WaveFormat.BlockAlign);   // 6000 ms * 16 samples/ms — muted tail is silence
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal(6000, record!.DurationMs);
    }

    [Fact]
    public async Task Mute_when_idle_is_a_noop_with_notice()
    {
        var (c, _, _, _) = LiveTestDoubles.MakeController(_root);
        var notices = new List<string>();
        c.Notice += notices.Add;
        await c.SetLocalMuteAsync(true, CancellationToken.None);
        Assert.False(c.LocalMuted);
        Assert.Contains(notices, n => n.Contains("mute", StringComparison.OrdinalIgnoreCase));
    }
}
```

(Adjust namespaces to the existing test-file conventions — e.g. `using LocalScribe.Core.Audio;` / `Settings`/`AudioFormat` usings as the pad tests in `SessionControllerTests.cs` do; mirror them.)

- [ ] **Step 2: Run to verify they fail.**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter SessionControllerMuteTests`
Expected: FAIL — compile errors (`SetLocalMuteAsync`, `LocalMuted`, `Markers.LocalMuted` do not exist).

- [ ] **Step 3: Add the markers.**

In `src/LocalScribe.Core/Model/Markers.cs` (match the existing const style):

```csharp
    public const string LocalMuted = "microphone muted by user";
    public const string LocalUnmuted = "microphone unmuted";
```

- [ ] **Step 4: Implement in `SessionController`.**

1. Add to the `Session` nested class: `public bool LocalMuted;` (mutated only under `_gate`; read by the VM via the event payload).
2. Add near the other public state/events:

```csharp
    /// <summary>True while the user has muted their own side via LocalScribe (design 2026-07-10
    /// section 1): the LOCAL leg is not captured at all while muted — privileged asides never enter
    /// the evidentiary record. Independent of Pause; false when Idle.</summary>
    public bool LocalMuted => _session?.LocalMuted ?? false;
    public event Action<bool>? LocalMuteChanged;
```

3. Add the lifecycle method (mirror `PauseAsync`'s structure):

```csharp
    /// <summary>Mutes/unmutes the user's own side (design 2026-07-10 section 1). Muted = the local
    /// leg is stopped and captures NOTHING (one-sided Pause; the muted span is silence in the
    /// retained file, bracketed by markers). Unmute starts a fresh local leg exactly like Resume's
    /// local half. Valid while Recording or Paused; Resume honors the muted state (never silently
    /// unmutes — see ResumeAsync). Idempotent: setting the current state again is a no-op.</summary>
    public async Task SetLocalMuteAsync(bool muted, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State is not (SessionState.Recording or SessionState.Paused) || _session is null)
            {
                Notice?.Invoke("Nothing to mute - not recording.");
                return;
            }
            var s = _session;
            if (s.LocalMuted == muted) return;                     // idempotent: no duplicate markers

            if (muted)
            {
                if (State == SessionState.Recording)
                    await s.Local.StopLegAndFlushAsync();           // Pause's clean flush: trailing words kept
                s.LocalMuted = true;
                s.Outbox.Writer.TryWrite(new MarkerAt(Markers.LocalMuted, s.Clock.ElapsedMs));
                _localStartPeak = null;                             // Start-only probe: abandoned like Resume does
                // A stale "no speech" banner must not outlive the leg it described - the muted
                // state line supersedes it (same reset-and-clear symmetry as ResumeAsync).
                bool wasFlagged;
                lock (_silentGate) { wasFlagged = s.LocalSilentMonitor.Reset(s.Clock.ElapsedMs); }
                if (wasFlagged) SilentLegCleared?.Invoke(SourceKind.Local);
            }
            else
            {
                s.LocalMuted = false;
                s.Outbox.Writer.TryWrite(new MarkerAt(Markers.LocalUnmuted, s.Clock.ElapsedMs));
                if (State == SessionState.Recording)
                {
                    var (micSource, _) = _captureProvider.CreateMic(s.Clock);   // fresh leg: re-resolves device (like Resume)
                    bool wasFlagged;
                    lock (_silentGate) { wasFlagged = s.LocalSilentMonitor.Reset(s.Clock.ElapsedMs); }
                    if (wasFlagged) SilentLegCleared?.Invoke(SourceKind.Local);
                    s.Local.StartLeg(micSource, s.CaptureCts.Token, s.FeedCts.Token);
                }
                // While Paused: state+marker only; Resume starts the leg (it honors LocalMuted).
            }
            LocalMuteChanged?.Invoke(muted);
        }
        finally
        {
            _gate.Release();
        }
    }
```

4. In `ResumeAsync`, guard the local leg (locate the `CreateMic` + `s.Local.StartLeg` pair):

```csharp
            // Resume honors mute (design 2026-07-10 section 1): a user who muted for a privileged
            // aside and then paused must never be silently unmuted by Resume - the local leg
            // restarts only on an explicit SetLocalMuteAsync(false).
            if (!s.LocalMuted)
            {
                var (micSource, _) = _captureProvider.CreateMic(s.Clock);
                s.Local.StartLeg(micSource, s.CaptureCts.Token, s.FeedCts.Token);
            }
```

(The remote leg's create/start and the rest of ResumeAsync are unchanged; keep the silent-monitor resets exactly as they are.)

- [ ] **Step 5: Run the new tests, then both full suites.**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter SessionControllerMuteTests` → PASS (6/6).
Then: `dotnet test tests/LocalScribe.Core.Tests` (417+6 new = 423 + 2 known) and `dotnet test tests/LocalScribe.App.Tests` (381). Expected: no NEW failures. Watch `SessionControllerPauseTests` — Resume's restructured local-leg block must keep them green.

- [ ] **Step 6: Commit.**

```bash
git add src/LocalScribe.Core/Model/Markers.cs src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerMuteTests.cs
git commit -m "feat(core): Mute my side - local leg stops capturing while muted, markers bracket the gap"
```

### Task 2: Record-console mute button + state line

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SessionViewModel.cs`
- Modify: `src/LocalScribe.App/LiveViewWindow.xaml`
- Modify: `tests/LocalScribe.App.Tests/SessionViewModelTests.cs`

**Interfaces:**
- Consumes: `SessionController.SetLocalMuteAsync`, `.LocalMuted`, `.LocalMuteChanged` (Task 1).
- Produces: `SessionViewModel.IsLocalMuted` (observable), `MuteLocalCommand` (`IAsyncRelayCommand`, executable while Recording/Paused).

- [ ] **Step 1: Write the failing VM test** (mirror the existing `SessionViewModelTests` harness — `dispatch: a => a()`, `LiveTestDoubles.MakeController`):

```csharp
    [Fact]
    public async Task Mute_command_toggles_through_the_real_controller()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsLocalMuted);
        await vm.MuteLocalCommand.ExecuteAsync(null);
        Assert.True(vm.IsLocalMuted);
        Assert.True(controller.LocalMuted);
        await vm.MuteLocalCommand.ExecuteAsync(null);           // toggle back
        Assert.False(vm.IsLocalMuted);

        await vm.StopCommand.ExecuteAsync(null);
        await controller.PendingFinalize;
        vm.Dispose();
    }
```

- [ ] **Step 2: Run to verify it fails** (compile error: `MuteLocalCommand`).

- [ ] **Step 3: Implement in `SessionViewModel`** (mirror the silent-leg pattern exactly — named handler field, dispatch marshal, Dispose detach):

```csharp
    [ObservableProperty] private bool _isLocalMuted;
    public IAsyncRelayCommand MuteLocalCommand { get; }
    private readonly Action<bool> _onLocalMuteChanged;

    // ctor additions:
    MuteLocalCommand = new AsyncRelayCommand(ToggleMuteAsync,
        () => State is SessionState.Recording or SessionState.Paused);
    _onLocalMuteChanged = muted => _dispatch(() => IsLocalMuted = muted);
    controller.LocalMuteChanged += _onLocalMuteChanged;
    // in the StateChanged dispatch body, alongside the other NotifyCanExecuteChanged calls:
    MuteLocalCommand.NotifyCanExecuteChanged();
    // in StartAsync, alongside MicSilent/RemoteSilent reset:
    IsLocalMuted = false;
    // in Dispose, alongside the silent-leg detaches:
    _controller.LocalMuteChanged -= _onLocalMuteChanged;

    private Task ToggleMuteAsync()
        => Task.Run(() => _controller.SetLocalMuteAsync(!_controller.LocalMuted, CancellationToken.None));
```

- [ ] **Step 4: XAML.** In `LiveViewWindow.xaml`'s recording controls row (the `Pause/Resume` + `Stop` buttons), add after Stop:

```xml
                    <Button Margin="8,0,0,0" Command="{Binding Session.MuteLocalCommand}">
                        <Button.Style>
                            <Style TargetType="Button">
                                <Setter Property="Content" Value="Mute my side" />
                                <Style.Triggers>
                                    <DataTrigger Binding="{Binding Session.IsLocalMuted}" Value="True">
                                        <Setter Property="Content" Value="Unmute" />
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </Button.Style>
                    </Button>
```

And in the warning-rows StackPanel (silent-leg banners), add FIRST (state, not warning — SemiBold, no WarningText style):

```xml
                    <TextBlock Text="Your side is muted - not being recorded."
                               FontWeight="SemiBold"
                               Visibility="{Binding Session.IsLocalMuted, Converter={StaticResource BoolToVis}}" />
```

- [ ] **Step 5: Run the new test + full App suite** (`dotnet test tests/LocalScribe.App.Tests`) → PASS, and build the solution (XAML compiles, 0 warnings).

- [ ] **Step 6: Commit.**

```bash
git add src/LocalScribe.App/ViewModels/SessionViewModel.cs src/LocalScribe.App/LiveViewWindow.xaml tests/LocalScribe.App.Tests/SessionViewModelTests.cs
git commit -m "feat(app): Mute my side toggle + muted state line on the Record console"
```

---

## Phase B — device-level mute awareness

### Task 3: `IEndpointMuteObservable` + `MicCaptureSource` endpoint watch

**Files:**
- Create: `src/LocalScribe.Core/Audio/IEndpointMuteObservable.cs`
- Modify: `src/LocalScribe.Core/Audio/MicCaptureSource.cs`
- Modify: `src/LocalScribe.Core/Audio/FakeCaptureSource.cs`
- Modify: `tests/LocalScribe.Core.Tests/LiveTestDoubles.cs` (`DisposalTrackingSource` forwards; `FakeProvider` knobs)

**Interfaces:**
- Produces:

```csharp
namespace LocalScribe.Core.Audio;

/// <summary>Optional capability of a capture source whose ENDPOINT (device master) mute is
/// observable (design 2026-07-10 section 2). Device mute silences every client of the endpoint -
/// including LocalScribe's own stream - so the user must learn of it instantly, not after the
/// 15s silent-leg grace. Events may fire on arbitrary (COM callback) threads; consumers marshal.</summary>
public interface IEndpointMuteObservable
{
    bool DeviceMuted { get; }
    event Action<bool>? DeviceMuteChanged;
}
```

- `MicCaptureSource : ICaptureSource, IEndpointMuteObservable`; `FakeCaptureSource` implements it with `public void RaiseDeviceMute(bool muted)`; `FakeProvider` gains `public bool NextMicDeviceMuted` (seeds the fake's initial state) and `public FakeCaptureSource? LastMicFake` (the unwrapped fake, so tests can raise).

- [ ] **Step 1: Write the failing test** (in `SessionControllerMuteTests.cs` — it needs Task 4's controller wiring to pass fully, but write the SOURCE-level test now):

```csharp
    [Fact]
    public void Fake_capture_source_raises_device_mute_events()
    {
        var fake = new Audio.FakeCaptureSource(Audio.SourceKind.Local, new[] { new float[512] });
        var seen = new List<bool>();
        ((Audio.IEndpointMuteObservable)fake).DeviceMuteChanged += seen.Add;
        fake.RaiseDeviceMute(true);
        fake.RaiseDeviceMute(false);
        Assert.Equal(new[] { true, false }, seen);
        Assert.False(((Audio.IEndpointMuteObservable)fake).DeviceMuted);
    }
```

- [ ] **Step 2: Run to verify it fails** (compile error).

- [ ] **Step 3: Implement.**

`FakeCaptureSource` additions:

```csharp
    // Device-mute test surface (design 2026-07-10 section 2): tests drive endpoint-mute
    // transitions directly; the real MicCaptureSource raises these from IAudioEndpointVolume.
    public bool DeviceMuted { get; set; }
    public event Action<bool>? DeviceMuteChanged;
    public void RaiseDeviceMute(bool muted) { DeviceMuted = muted; DeviceMuteChanged?.Invoke(muted); }
```

`MicCaptureSource` — retain the device and subscribe (in the private `MicCaptureSource(IClock, MMDevice)` ctor):

```csharp
    private readonly MMDevice _device;
    private bool _lastDeviceMuted;

    public bool DeviceMuted
    {
        get { try { return _device.AudioEndpointVolume.Mute; } catch { return false; } }
    }
    public event Action<bool>? DeviceMuteChanged;

    // in the private ctor, after _capture is created (fail-open: an endpoint without a volume
    // interface simply has no mute awareness - capture must never fail because of it):
    _device = device;
    try
    {
        _lastDeviceMuted = _device.AudioEndpointVolume.Mute;
        _device.AudioEndpointVolume.OnVolumeNotification += OnEndpointVolume;
    }
    catch { /* no endpoint-volume interface: DeviceMuted stays false, no events */ }

    private void OnEndpointVolume(AudioVolumeNotificationData data)
    {
        if (data.Muted == _lastDeviceMuted) return;             // volume-only change: not ours
        _lastDeviceMuted = data.Muted;
        DeviceMuteChanged?.Invoke(data.Muted);                  // COM callback thread; consumers marshal
    }

    // in Dispose(), before _capture.Dispose():
    try { _device.AudioEndpointVolume.OnVolumeNotification -= OnEndpointVolume; } catch { }
    DeviceMuteChanged = null;
    // ...and after _capture.Dispose():
    _device.Dispose();
```

`DisposalTrackingSource` (in `LiveTestDoubles.cs`) — forward the capability:

```csharp
internal sealed class DisposalTrackingSource : ICaptureSource, IEndpointMuteObservable
{
    // existing members unchanged; add:
    public bool DeviceMuted => (_inner as IEndpointMuteObservable)?.DeviceMuted ?? false;
    public event Action<bool>? DeviceMuteChanged
    {
        add { if (_inner is IEndpointMuteObservable m) m.DeviceMuteChanged += value; }
        remove { if (_inner is IEndpointMuteObservable m) m.DeviceMuteChanged -= value; }
    }
}
```

`FakeProvider.CreateMic` — keep the unwrapped fake + seed the knob:

```csharp
    public bool NextMicDeviceMuted;
    public FakeCaptureSource? LastMicFake;
    // in CreateMic, replace the fake construction:
    var fake = new FakeCaptureSource(SourceKind.Local, LocalFrames()) { DeviceMuted = NextMicDeviceMuted };
    LastMicFake = fake;
    ICaptureSource src = fake;
    if (ThrowOnLocalStop) src = new StopThrowingSource(src);
    LastMic = new DisposalTrackingSource(src);
    return (LastMic, MicSnapshot);
```

(Note: `StopThrowingSource` does not forward the capability — the tests that use it don't exercise device mute; the controller type-tests and treats a non-observable source as "no awareness", which is exactly the fail-open contract.)

- [ ] **Step 4: Run the new test + full Core suite** → PASS, no NEW failures. (The real `MicCaptureSource` endpoint path is smoke-only — like device hot-unplug — and is exercised in the runbook, not unit tests.)

- [ ] **Step 5: Commit.**

```bash
git add src/LocalScribe.Core/Audio/IEndpointMuteObservable.cs src/LocalScribe.Core/Audio/MicCaptureSource.cs src/LocalScribe.Core/Audio/FakeCaptureSource.cs tests/LocalScribe.Core.Tests/LiveTestDoubles.cs tests/LocalScribe.Core.Tests/SessionControllerMuteTests.cs
git commit -m "feat(core): capture sources expose endpoint (device) mute observability"
```

### Task 4: Controller wiring — device-mute markers + event

**Files:**
- Modify: `src/LocalScribe.Core/Model/Markers.cs` (+2)
- Modify: `src/LocalScribe.Core/Live/SessionController.cs`
- Modify: `tests/LocalScribe.Core.Tests/SessionControllerMuteTests.cs`

**Interfaces:**
- Produces: markers `Markers.MicDeviceMuted = "microphone device muted"`, `MicDeviceUnmuted = "microphone device unmuted"`; `event Action<bool>? SessionController.MicDeviceMuteChanged`.

- [ ] **Step 1: Write the failing tests:**

```csharp
    [Fact]
    public async Task Device_mute_change_writes_marker_and_raises_event()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        var events = new List<bool>();
        c.MicDeviceMuteChanged += events.Add;
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);

        clock.ElapsedMs = 3000;
        provider.LastMicFake!.RaiseDeviceMute(true);
        clock.ElapsedMs = 4000;
        provider.LastMicFake!.RaiseDeviceMute(false);

        Assert.Equal(new[] { true, false }, events);
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.MicDeviceMuted && l.StartMs == 3000);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.MicDeviceUnmuted && l.StartMs == 4000);
    }

    [Fact]
    public async Task Device_already_muted_at_start_is_surfaced_immediately()
    {
        var (c, provider, paths, _) = LiveTestDoubles.MakeController(_root);
        provider.NextMicDeviceMuted = true;
        var events = new List<bool>();
        c.MicDeviceMuteChanged += events.Add;
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.Equal(new[] { true }, events);                    // no waiting for a change
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.MicDeviceMuted);
    }

    [Fact]
    public async Task Device_mute_is_suppressed_while_locally_muted()
    {
        var (c, provider, paths, clock) = LiveTestDoubles.MakeController(_root);
        var events = new List<bool>();
        c.MicDeviceMuteChanged += events.Add;
        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        clock.ElapsedMs = 1000;
        await c.SetLocalMuteAsync(true, CancellationToken.None); // user muted deliberately
        provider.LastMicFake!.RaiseDeviceMute(true);             // device mute while our leg is stopped
        Assert.Empty(events);                                    // nothing to warn about
        await c.StopAsync(CancellationToken.None);
        await c.PendingFinalize;
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.DoesNotContain(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.MicDeviceMuted);
    }
```

- [ ] **Step 2: Run to verify they fail** (compile errors, then behavior).

- [ ] **Step 3: Implement.** Markers (+2, same style). Controller:

```csharp
    /// <summary>Device-level (endpoint) mic mute observed on the local leg's capture device
    /// (design 2026-07-10 section 2) - the mute that silences LocalScribe's own stream too, so it
    /// must surface instantly, not after the silent-leg grace. Distinct from LocalMuted (the
    /// user's deliberate in-LocalScribe mute).</summary>
    public event Action<bool>? MicDeviceMuteChanged;

    /// <summary>Hooks a local leg's endpoint-mute capability (no-op for sources without it).
    /// Surfaces an already-muted device at leg start immediately. The handler is guarded by
    /// session identity + LocalMuted (a deliberately muted user needs no device warning) and only
    /// reports while Recording. Marker writes go through the outbox (thread-safe channel); the
    /// event fires on the COM callback thread - consumers marshal (same contract as PeakObserved).</summary>
    private void HookDeviceMute(ICaptureSource micSource, Session session)
    {
        if (micSource is not IEndpointMuteObservable m) return;
        if (m.DeviceMuted) OnDeviceMuteChanged(session, true);
        m.DeviceMuteChanged += muted => OnDeviceMuteChanged(session, muted);
    }

    private void OnDeviceMuteChanged(Session session, bool muted)
    {
        if (!ReferenceEquals(_session, session)) return;         // stale leg after Stop/new session
        if (session.LocalMuted) return;                          // deliberate mute: no warning
        if (State != SessionState.Recording) return;
        session.Outbox.Writer.TryWrite(new MarkerAt(
            muted ? Markers.MicDeviceMuted : Markers.MicDeviceUnmuted, session.Clock.ElapsedMs));
        MicDeviceMuteChanged?.Invoke(muted);
    }
```

Call `HookDeviceMute` at every local-leg start: (a) in `StartAsync` immediately AFTER `_session = new Session { ... }` / `SetState(Recording)` (the mic source local is still in scope; hooking after `_session` exists is what makes the identity guard work — the microseconds of delay are immaterial because the hook does an initial read); (b) in `ResumeAsync` after `s.Local.StartLeg(...)` inside the `!s.LocalMuted` branch; (c) in `SetLocalMuteAsync`'s unmute branch after `StartLeg`.

- [ ] **Step 4: Run the three tests + both full suites** → PASS, no NEW failures.

- [ ] **Step 5: Commit.**

```bash
git add src/LocalScribe.Core/Model/Markers.cs src/LocalScribe.Core/Live/SessionController.cs tests/LocalScribe.Core.Tests/SessionControllerMuteTests.cs
git commit -m "feat(core): device-level mic mute surfaces instantly as markers + event"
```

### Task 5: Device-mute banner on the console

**Files:**
- Modify: `src/LocalScribe.App/ViewModels/SessionViewModel.cs`
- Modify: `src/LocalScribe.App/LiveViewWindow.xaml`
- Modify: `tests/LocalScribe.App.Tests/SessionViewModelTests.cs`

- [ ] **Step 1: Failing VM test:** start a session via the VM, `provider.LastMicFake.RaiseDeviceMute(true)` → `vm.MicDeviceMuted` true; `false` → false; reset on next Start. (The harness exposes the provider from `MakeController` — same tuple the Core tests use.)
- [ ] **Step 2: Verify it fails.**
- [ ] **Step 3: Implement:** `[ObservableProperty] private bool _micDeviceMuted;` + named handler `_onMicDeviceMuteChanged = muted => _dispatch(() => MicDeviceMuted = muted);`, subscribe in ctor, detach in Dispose, reset `MicDeviceMuted = false` in `StartAsync` alongside the silent-leg resets.
- [ ] **Step 4: XAML** — add to the warning rows (WarningText style, like the silent-leg banners):

```xml
                    <TextBlock Text="Your microphone device is muted - nothing is being recorded from it."
                               Visibility="{Binding Session.MicDeviceMuted, Converter={StaticResource BoolToVis}}"
                               Style="{StaticResource WarningText}" TextWrapping="Wrap" />
```

- [ ] **Step 5: Run App suite + build** → PASS/0 warnings.
- [ ] **Step 6: Commit:** `git commit -m "feat(app): device-muted warning banner on the Record console"`

---

## Phase C — echo dedup (bidirectional + containment)

### Task 6: `TextDistance.ContainmentSimilarity`

**Files:**
- Modify: `src/LocalScribe.Core/Projection/TextDistance.cs`
- Modify/Create: `tests/LocalScribe.Core.Tests/TextDistanceTests.cs` (extend it if it exists; create matching the repo test style otherwise)

**Interfaces:**
- Produces: `static double TextDistance.ContainmentSimilarity(string a, string b)` — best token-boundary-window similarity of the shorter normalized text against the longer; returns 0 when the shorter text is below the length guard (normalized length < 12 chars OR < 3 tokens).

- [ ] **Step 1: Failing tests** (values pre-measured with the implemented metric — the pair-1 window is an exact match):

```csharp
    [Fact]
    public void Containment_finds_a_perfect_prefix_echo()
    {
        // The observed 2026-07-10 pair 1: whole-string similarity is only 0.50, but the shorter
        // text is contained verbatim - containment must catch it.
        double sim = TextDistance.ContainmentSimilarity(
            "So I'm gonna be testing sound.",
            "Okay, so I'm gonna be testing sound. I'm testing, I'm testing.");
        Assert.True(sim >= 0.99, $"expected ~1.0, got {sim}");
    }

    [Fact]
    public void Containment_refuses_short_interjections()
    {
        // "yeah"/"okay" are contained in nearly everything - the length guard must zero them out.
        Assert.Equal(0.0, TextDistance.ContainmentSimilarity("Yeah.", "yeah I think we should file it"));
        Assert.Equal(0.0, TextDistance.ContainmentSimilarity("okay so", "okay so let us begin the hearing"));
    }

    [Fact]
    public void Containment_does_not_rescue_a_garbled_echo()
    {
        // The observed pair 3: whisper garbled the two copies differently - no safe text gate
        // passes this, and the design accepts it stays visible (documented limitation).
        double sim = TextDistance.ContainmentSimilarity("hold on to my name", "Hold on my mind.");
        Assert.True(sim < 0.85, $"expected < 0.85, got {sim}");
    }

    [Fact]
    public void Containment_equals_whole_string_similarity_when_lengths_match()
    {
        // Equal token counts degenerate to a single window == the whole string, so containment
        // can never LOWER the effective gate for classic same-length pairs.
        string a = "I pushed the auth changes last night", b = "I pushed the auth change last night";
        Assert.Equal(TextDistance.NormalizedSimilarity(a, b),
                     TextDistance.ContainmentSimilarity(a, b), 3);
    }
```

- [ ] **Step 2: Verify they fail** (method missing).
- [ ] **Step 3: Implement** in `TextDistance`:

```csharp
    /// <summary>Best token-window containment similarity (design 2026-07-10 section 4): the shorter
    /// normalized text scored against every same-token-count contiguous window of the longer, max
    /// taken. Catches an echo copy that picked up extra surrounding tokens (whole-string distance
    /// punishes length asymmetry: a verbatim 29-char prefix match can score 0.50). Guard: a shorter
    /// text under 12 normalized chars or 3 tokens returns 0 - interjections ("yeah", "okay") are
    /// contained in nearly everything and must never containment-match.</summary>
    public static double ContainmentSimilarity(string a, string b)
    {
        string na = Normalize(a), nb = Normalize(b);
        string shorter = na.Length <= nb.Length ? na : nb;
        string longer = na.Length <= nb.Length ? nb : na;
        var sTok = shorter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lTok = longer.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (shorter.Length < 12 || sTok.Length < 3) return 0.0;

        double best = 0.0;
        for (int start = 0; start + sTok.Length <= lTok.Length; start++)
        {
            string window = string.Join(' ', lTok.Skip(start).Take(sTok.Length));
            int max = Math.Max(shorter.Length, window.Length);
            double sim = 1.0 - Levenshtein(shorter.ToCharArray(), window.ToCharArray()) / (double)max;
            if (sim > best) best = sim;
        }
        return best;
    }
```

- [ ] **Step 4: Run the tests** → PASS. Full Core suite → no NEW failures.
- [ ] **Step 5: Commit:** `git commit -m "feat(core): containment text similarity for echo-copy detection"`

### Task 7: Bidirectional `PhantomBleedDedup`

**Files:**
- Modify: `src/LocalScribe.Core/Projection/PhantomBleedDedup.cs`
- Modify: `tests/LocalScribe.Core.Tests/PhantomBleedDedupTests.cs` (KEEP every existing fact untouched; append)

**Interfaces:**
- Consumes: `TextDistance.ContainmentSimilarity` (Task 6). Uses the existing `Seg(...)` helper in `PhantomBleedDedupTests.cs`.
- Produces: `Filter` also hides a **Remote** segment that is an echo of a KEPT Local segment — near-window + `max(NormalizedSimilarity, ContainmentSimilarity) >= MinSimilarity` + **RMS evidence required** (`|localRms - remoteRms| >= MinRmsGapDb`; no text-only fallback in this direction). Threshold VALUES unchanged. Corrected/split exemption in both directions. A matching pair can never vanish entirely (pass 2 only checks KEPT locals).

- [ ] **Step 1: Failing tests** (append to `PhantomBleedDedupTests`, reusing `Seg`):

```csharp
    [Fact]
    public void Remote_echo_of_the_users_own_speech_is_hidden()
    {
        // The observed 2026-07-10 session: the user's voice came back on the REMOTE leg (a second
        // device in the meeting), transcribed with extra tokens. Containment + RMS gap hides it.
        var local = Seg(TranscriptSource.Local, 0, 1000, 3000, "So I'm gonna be testing sound.", -20.0);
        var echo = Seg(TranscriptSource.Remote, 1, 1200, 3400,
            "Okay, so I'm gonna be testing sound. I'm testing, I'm testing.", -28.0);
        var kept = new PhantomBleedDedup().Filter(new[] { local, echo });
        var only = Assert.Single(kept);
        Assert.Equal(TranscriptSource.Local, only.Source);
    }

    [Fact]
    public void Remote_with_comparable_energy_is_never_hidden()
    {
        // A genuine remote speaker repeating the user's words has comparable energy - keep it.
        var local = Seg(TranscriptSource.Local, 0, 1000, 3000, "So I'm gonna be testing sound.", -20.0);
        var remote = Seg(TranscriptSource.Remote, 1, 1200, 3400,
            "Okay, so I'm gonna be testing sound. I'm testing, I'm testing.", -21.0);   // 1 dB gap
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { local, remote }).Count);
    }

    [Fact]
    public void Remote_direction_requires_rms_evidence_no_text_only_fallback()
    {
        var local = Seg(TranscriptSource.Local, 0, 1000, 3000, "So I'm gonna be testing sound.", null);
        var echo = Seg(TranscriptSource.Remote, 1, 1200, 3400, "So I'm gonna be testing sound.", null);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { local, echo }).Count);   // both kept
    }

    [Fact]
    public void A_pair_can_never_vanish_entirely()
    {
        // Identical text both legs with the LOCAL quieter: pass 1 hides the local as a bleed of
        // the remote; pass 2 must then NOT also hide the remote (it only checks KEPT locals).
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 4000, "I pushed the auth changes last night.", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 1150, 4100, "I pushed the auth changes last night.", -31.5);
        var kept = new PhantomBleedDedup().Filter(new[] { remote, local });
        var only = Assert.Single(kept);
        Assert.Equal(TranscriptSource.Remote, only.Source);
    }

    [Fact]
    public void Garbled_echo_pair_stays_visible_documented_limitation()
    {
        // Observed pair 3: no safe text gate catches "hold on to my name" vs "Hold on my mind."
        var local = Seg(TranscriptSource.Local, 0, 8000, 9000, "hold on to my name", -20.0);
        var garbled = Seg(TranscriptSource.Remote, 1, 8100, 9200, "Hold on my mind.", -28.0);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { local, garbled }).Count);
    }

    [Fact]
    public void Corrected_remote_echo_is_exempt_from_hiding()
    {
        var local = Seg(TranscriptSource.Local, 0, 1000, 3000, "So I'm gonna be testing sound.", -20.0);
        var echo = Seg(TranscriptSource.Remote, 1, 1200, 3400,
            "Okay, so I'm gonna be testing sound. I'm testing, I'm testing.", -28.0) with { Corrected = true };
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { local, echo }).Count);
    }
```

- [ ] **Step 2: Verify they fail** (new-direction facts fail; existing facts still pass).
- [ ] **Step 3: Implement.** Replace `Filter` and add the second gate (keep `IsBleedOf` semantics; swap its similarity call for the shared max):

```csharp
    public IReadOnlyList<ProjectedSegment> Filter(IReadOnlyList<ProjectedSegment> segments)
    {
        var remotes = segments.Where(s => s.Source == TranscriptSource.Remote).ToList();
        var locals = segments.Where(s => s.Source == TranscriptSource.Local).ToList();
        if (remotes.Count == 0 || locals.Count == 0) return segments;

        // Pass 1 (classic direction, unchanged semantics): a quieter Local copy of a
        // near-simultaneous Remote is the bled copy - hide it.
        var hiddenLocals = new HashSet<ProjectedSegment>(
            locals.Where(s => !(s.Corrected || s.IsSplitChild) && remotes.Any(r => IsBleedOf(s, r))));

        // Pass 2 (2026-07-10, design section 4): the user's own voice echoed back on the Remote
        // leg. Only KEPT locals anchor a remote-hide, so a matching pair can never vanish
        // entirely; RMS evidence is REQUIRED in this direction (a genuine remote speaker
        // repeating the words has comparable energy and must survive).
        var keptLocals = locals.Where(l => !hiddenLocals.Contains(l)).ToList();

        var kept = new List<ProjectedSegment>(segments.Count);
        foreach (var s in segments)
        {
            if (s.Source == TranscriptSource.Local && hiddenLocals.Contains(s)) continue;
            if (s.Source == TranscriptSource.Remote && !(s.Corrected || s.IsSplitChild)
                && keptLocals.Any(l => IsEchoOfLocal(s, l)))
                continue;                                   // hidden at render; JSONL untouched
            kept.Add(s);
        }
        return kept;
    }

    /// <summary>Echo-copy similarity: whole-string OR best-containment (an echo leg often picks up
    /// extra surrounding tokens, which whole-string distance over-punishes). Threshold VALUES are
    /// unchanged - tune ONLY against the golden corpus.</summary>
    private static double Similarity(string a, string b)
        => Math.Max(TextDistance.NormalizedSimilarity(a, b), TextDistance.ContainmentSimilarity(a, b));

    private bool IsEchoOfLocal(ProjectedSegment remote, ProjectedSegment local)
    {
        bool near = remote.StartMs < local.EndMs + _o.NearWindowMs
                 && local.StartMs - _o.NearWindowMs < remote.EndMs;
        if (!near) return false;
        if (local.Line.RmsDb is not { } lr || remote.Line.RmsDb is not { } rr) return false;
        return Similarity(remote.Text, local.Text) >= _o.MinSimilarity
            && Math.Abs(lr - rr) >= _o.MinRmsGapDb;
    }
```

And inside `IsBleedOf`, change `double similarity = TextDistance.NormalizedSimilarity(local.Text, remote.Text);` to `double similarity = Similarity(local.Text, remote.Text);` (containment can only raise, never lower, a same-length pair — Task 6's degeneracy test pins that).

- [ ] **Step 4: Run the FULL dedup suite** (`--filter PhantomBleedDedupTests`) — every pre-existing fact must pass unchanged (especially `Missing_rms_uses_the_stricter_text_only_bar`: its near-match pair has equal token counts, so containment degenerates to the whole-string value and the expectation holds). Then both full suites → no NEW failures.
- [ ] **Step 5: Commit:** `git commit -m "feat(core): bidirectional phantom-bleed dedup - the user's own echo on the remote leg is hidden"`

---

## Phase D — UIA spike (gated; the watcher is NOT in this plan)

### Task 8: UIA probe tool + user runbook

**Files:**
- Create: `tools/UiaProbe/UiaProbe.csproj`, `tools/UiaProbe/Program.cs`
- Create: `docs/plans/2026-07-10-uia-mute-spike-runbook.md`

**Purpose:** capture the CURRENT Webex (CiscoCollabHost) and new-Teams UIA trees during a real call, muted and unmuted, so the advisory watcher's selectors can be designed from evidence. **STOP after this task** — the watcher implementation is planned only from the findings.

- [ ] **Step 1: Create the probe** — a self-contained console app (net10.0-windows, `<UseWindowsForms>false</UseWindowsForms>`, NuGet `FlaUI.UIA3` latest stable; this project is NOT referenced by the solution's product assemblies — Core stays UIA-free):

```csharp
// tools/UiaProbe/Program.cs
// Dumps the UIA tree (ControlType, AutomationId, Name, ClassName, TogglePattern state) of every
// top-level window belonging to the given process names, to a timestamped file. Read-only: never
// invokes patterns, never focuses windows. Usage: UiaProbe [processName ...]
// (defaults: CiscoCollabHost ms-teams Teams)
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

string[] targets = args.Length > 0 ? args : new[] { "CiscoCollabHost", "ms-teams", "Teams" };
var sb = new StringBuilder();
using var automation = new UIA3Automation();
var desktop = automation.GetDesktop();
foreach (var w in desktop.FindAllChildren())
{
    string pname;
    try { pname = System.Diagnostics.Process.GetProcessById(w.Properties.ProcessId.Value).ProcessName; }
    catch { continue; }
    if (!targets.Any(t => pname.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
    sb.AppendLine($"===== window: '{w.Name}' process={pname} class={w.ClassName} =====");
    Dump(w, 0);
}
string outPath = Path.Combine(AppContext.BaseDirectory,
    $"uia-dump-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
File.WriteAllText(outPath, sb.ToString());
Console.WriteLine($"wrote {outPath} ({sb.Length} chars)");

void Dump(AutomationElement e, int depth)
{
    if (depth > 25) return;
    string toggle = "";
    try
    {
        if (e.Patterns.Toggle.IsSupported)
            toggle = $" TOGGLE={e.Patterns.Toggle.Pattern.ToggleState.Value}";
    }
    catch { }
    string id = "", name = "", cls = "";
    try { id = e.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
    try { name = e.Properties.Name.ValueOrDefault ?? ""; } catch { }
    try { cls = e.ClassName ?? ""; } catch { }
    sb.AppendLine($"{new string(' ', depth * 2)}[{e.Properties.ControlType.ValueOrDefault}] id='{id}' name='{name}' class='{cls}'{toggle}");
    foreach (var c in e.FindAllChildren()) Dump(c, depth + 1);
}
```

- [ ] **Step 2: Verify it builds and runs standalone** (`dotnet run --project tools/UiaProbe` with no meeting open — it should produce a small/empty dump without crashing).
- [ ] **Step 3: Write the runbook** (`docs/plans/2026-07-10-uia-mute-spike-runbook.md`): user starts a real Webex meeting; runs the probe UNMUTED; mutes in Webex; runs it again; repeats with meeting-controls auto-hidden; (optionally same for Teams); attaches the dump files. The findings to extract: is the mic toggle in the UIA tree at all; does it carry a stable language-independent `AutomationId`; does `TogglePattern` reflect mute or does only the `Name` change ("Mute"/"Unmute"); does the element survive controls auto-hide. **The advisory watcher's plan is written from these findings — do not implement the watcher speculatively.**
- [ ] **Step 4: Commit:** `git commit -m "chore(tools): UIA probe + runbook for the app-mute spike (watcher gated on findings)"`

---

## Post-implementation

- [ ] **Task 9 (docs): spec deltas** — `docs/specs/localscribe-specs.md`: §2.1 local-leg mute in the lifecycle (mute stops local capture; Resume honors mute; the four new markers); §5 bidirectional dedup + containment metric + RMS-required remote direction (values unchanged); §8 the device-mute and muted-state console indicators. Commit as `docs(spec): mute controls + bidirectional dedup`.
- [ ] **Whole-branch review** (opus, `superpowers:requesting-code-review`): focus on (a) the mute×Pause×Resume×Stop state matrix (no interleaving may silently unmute or strand a leg), (b) the device-mute hook's thread/lifetime safety (COM callback thread → outbox; stale-leg guard), (c) dedup pass-2 safety (a pair can never fully vanish; existing direction byte-identical).
- [ ] **User smoke runbook:** (1) mute/unmute mid-recording → markers in the read view, local audio silent in the gap, remote unaffected; (2) headset hardware mute → instant banner; (3) re-run the two-device echo test → the echo copy hidden in the read view; (4) run the UIA probe during a real Webex call (Task 8 runbook) and hand back the dumps.

## Self-Review notes

- **Design coverage:** Feature A → Tasks 1-2; B → 3-5; D(dedup) → 6-7; C(spike only, per design gating) → 8; spec deltas → 9. The advisory watcher is deliberately absent (gated on the spike — design §3).
- **Type consistency:** `SetLocalMuteAsync`/`LocalMuted`/`LocalMuteChanged`, `IEndpointMuteObservable.DeviceMuted`/`DeviceMuteChanged`, `MicDeviceMuteChanged`, `Markers.{LocalMuted,LocalUnmuted,MicDeviceMuted,MicDeviceUnmuted}`, `TextDistance.ContainmentSimilarity`, `IsEchoOfLocal` used consistently across tasks.
- **Known judgment calls encoded:** Resume honors mute (Task 1 Step 4.4); no text-only fallback in the remote dedup direction (Task 7); UIA watcher writes no markers and is not in this plan (design §3); `StopThrowingSource` deliberately does not forward the mute capability (fail-open contract, Task 3).
