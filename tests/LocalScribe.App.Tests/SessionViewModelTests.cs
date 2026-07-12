using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SessionViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-vm-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Task 8: drives AppMuteWatcher without UIA - a settable tray reading fed via Poll().</summary>
    private sealed class FakeAppMuteSignalSource : IAppMuteSignalSource
    {
        public AppMuteReading Next = new(AppMuteState.Unknown, null);
        public AppMuteReading Read() => Next;
    }

    private (SessionViewModel Vm, SessionController Controller) MakeVm()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());      // test VAD, preflight off
        return (vm, controller);
    }

    [Fact]
    public async Task Commands_gate_on_state()
    {
        var (vm, _) = MakeVm();
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.False(vm.StopCommand.CanExecute(null));
        Assert.False(vm.PauseResumeCommand.CanExecute(null));

        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Recording, vm.State);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.StopCommand.CanExecute(null));
        Assert.True(vm.PauseResumeCommand.CanExecute(null));

        await vm.PauseResumeCommand.ExecuteAsync(null);      // pause
        Assert.Equal(SessionState.Paused, vm.State);
        await vm.PauseResumeCommand.ExecuteAsync(null);      // resume
        Assert.Equal(SessionState.Recording, vm.State);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.Equal(SessionState.Idle, vm.State);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Peaks_light_levels_and_notices_surface()
    {
        var (vm, controller) = MakeVm();
        string? seen = null;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.LastNotice)) seen = vm.LastNotice; };

        await vm.StartCommand.ExecuteAsync(null);            // fakes replay 0.5f speech frames
        // The fakes replay frames synchronously into an unbounded channel (capture must never
        // block - see CaptureFrameBridge), but the consuming VAD/peak loop is a separate
        // Task.Run started inside StartLeg and is NOT awaited by SessionController.StartAsync.
        // So "StartCommand completed" only guarantees the frames are queued, not yet drained -
        // bound the wait on the observable effect (no fixed sleep) rather than assume ordering.
        SpinWait.SpinUntil(() => vm.LocalLevel.Value > 0 && vm.RemoteLevel.Value > 0, TimeSpan.FromSeconds(2));
        Assert.True(vm.LocalLevel.Value > 0);
        Assert.True(vm.RemoteLevel.Value > 0);

        await vm.StopCommand.ExecuteAsync(null);
        // Intent: drive a second Notice event through to LastNotice. StopCommand's CanExecute
        // is false once State is Idle again - AsyncRelayCommand.ExecuteAsync ignores CanExecute
        // (that gate is a UI/binding concern), so the direct re-execute below normally still
        // reaches the controller's "Nothing to stop" Notice path; the CanExecute fallback to
        // calling the controller directly is kept as a belt-and-braces per the task brief.
        if (vm.StopCommand.CanExecute(null))
            await vm.StopCommand.ExecuteAsync(null);
        else
            await controller.StopAsync(CancellationToken.None);
        Assert.NotNull(vm.LastNotice);
        Assert.NotNull(seen);
    }

    [Fact]
    public async Task Repeated_identical_notice_still_raises_NoticeRaised_each_time()
    {
        // [ObservableProperty] gates PropertyChanged(LastNotice) on equality, so a second
        // IDENTICAL notice (e.g. the degraded-system-audio bleed/privacy warning on a later
        // session) would never re-fire a balloon keyed off that property. NoticeRaised must
        // fire every time regardless, so the tray can re-show a repeat warning.
        var (vm, controller) = MakeVm();
        var received = new List<string>();
        vm.NoticeRaised += received.Add;

        // Idle -> "Nothing to stop." is a controller.Notice with identical text both times.
        await controller.StopAsync(CancellationToken.None);
        await controller.StopAsync(CancellationToken.None);

        Assert.Equal(2, received.Count);
        Assert.Equal(received[0], received[1]);
    }

    [Fact]
    public async Task Elapsed_formats_and_resets()
    {
        var (vm, _) = MakeVm();
        Assert.Equal("00:00", vm.Elapsed);
        await vm.StartCommand.ExecuteAsync(null);
        vm.TimerTick();
        Assert.Matches(@"^\d{2}:\d{2}$", vm.Elapsed);
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Equal("00:00", vm.Elapsed);
    }

    [Fact]
    public void Silent_leg_detected_sets_a_visible_warning_cleared_on_recovery()
    {
        // Task 8 / Fix #2: SessionController.SilentLegDetected/Cleared are field-like events -
        // invocable only from within SessionController itself - and this codebase has no
        // InternalsVisibleTo wiring between LocalScribe.Core and the test assemblies, so a real
        // event needs a public test-only seam to drive it directly rather than waiting out the
        // real 15s grace window (see RaiseSilentLegDetectedForTest/RaiseSilentLegClearedForTest
        // on SessionController).
        var (vm, controller) = MakeVm();
        Assert.False(vm.MicSilent);
        Assert.False(vm.RemoteSilent);

        controller.RaiseSilentLegDetectedForTest(SourceKind.Local);
        Assert.True(vm.MicSilent);
        Assert.False(vm.RemoteSilent);

        controller.RaiseSilentLegDetectedForTest(SourceKind.Remote);
        Assert.True(vm.MicSilent);
        Assert.True(vm.RemoteSilent);

        controller.RaiseSilentLegClearedForTest(SourceKind.Local);
        Assert.False(vm.MicSilent);
        Assert.True(vm.RemoteSilent);

        controller.RaiseSilentLegClearedForTest(SourceKind.Remote);
        Assert.False(vm.MicSilent);
        Assert.False(vm.RemoteSilent);
    }

    [Fact]
    public async Task Stale_silent_leg_flag_from_a_prior_session_is_reset_on_the_next_Start()
    {
        // Final-review Finding 1: MicSilent/RemoteSilent are only ever cleared by a
        // SilentLegCleared event, but the VM is app-lifetime while SessionController hands out a
        // FRESH SilentLegMonitor per session - a leg left flagged at the end of session 1 (e.g.
        // the session ended before the leg recovered) would otherwise show a false "no speech"
        // banner from t=0 of session 2, since the fresh monitor never flags (so never clears) it.
        var (vm, controller) = MakeVm();

        controller.RaiseSilentLegDetectedForTest(SourceKind.Local);
        controller.RaiseSilentLegDetectedForTest(SourceKind.Remote);
        Assert.True(vm.MicSilent);
        Assert.True(vm.RemoteSilent);

        await vm.StartCommand.ExecuteAsync(null);   // new session Start must reset both flags

        Assert.False(vm.MicSilent);
        Assert.False(vm.RemoteSilent);

        await vm.StopCommand.ExecuteAsync(null);
    }

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

    [Fact]
    public async Task Muting_clears_a_stale_device_mute_banner()
    {
        // 2026-07-11 review fix: the controller suppresses MicDeviceMuteChanged while LocalMuted,
        // but a banner already showing (device muted BEFORE the user hit "Mute my side") would
        // otherwise go stale, rendering both banners simultaneously. Muting must clear it.
        var (controller, provider, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        await vm.StartCommand.ExecuteAsync(null);

        provider.LastMicFake!.RaiseDeviceMute(true);
        Assert.True(vm.MicDeviceMuted);

        await vm.MuteLocalCommand.ExecuteAsync(null);
        Assert.True(vm.IsLocalMuted);
        Assert.False(vm.MicDeviceMuted);

        await vm.StopCommand.ExecuteAsync(null);
        await controller.PendingFinalize;
        vm.Dispose();
    }

    [Fact]
    public async Task Device_mute_mirrors_the_controller_and_resets_on_next_Start()
    {
        // Task 5: mirrors Task 2's IsLocalMuted pattern, but the mute event originates from the
        // capture device itself (HookDeviceMute/OnDeviceMuteChanged in SessionController), driven
        // here via the fake mic leg's RaiseDeviceMute test seam - RaiseDeviceMute fires
        // synchronously and this VM's dispatch is inline (a => a()), so no waiting is needed.
        var (controller, provider, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.MicDeviceMuted);
        provider.LastMicFake!.RaiseDeviceMute(true);
        Assert.True(vm.MicDeviceMuted);
        provider.LastMicFake!.RaiseDeviceMute(false);
        Assert.False(vm.MicDeviceMuted);

        provider.LastMicFake!.RaiseDeviceMute(true);
        Assert.True(vm.MicDeviceMuted);
        await vm.StopCommand.ExecuteAsync(null);
        await controller.PendingFinalize;

        await vm.StartCommand.ExecuteAsync(null);   // new session Start must reset the flag
        Assert.False(vm.MicDeviceMuted);

        await vm.StopCommand.ExecuteAsync(null);
        await controller.PendingFinalize;
        vm.Dispose();
    }

    [Fact]
    public async Task App_mute_mismatch_banners_after_debounce_and_action_resolves_it()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var src = new FakeAppMuteSignalSource();                 // add this tiny fake next to the test
        var watcher = new AppMuteWatcher(src, () => controller.State == SessionState.Recording);
        long now = 0;
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options(), appMuteWatcher: watcher, wallClock: () => now);
        await vm.StartCommand.ExecuteAsync(null);

        src.Next = new AppMuteReading(AppMuteState.Muted, "Webex");
        watcher.Poll();                                          // t=0: mismatch begins
        Assert.Equal(AppMuteBannerKind.None, vm.AppMuteBannerKind);
        now = 6000;
        watcher.Poll();                                          // same reading; VM re-evaluates on poll tick
        Assert.Equal(AppMuteBannerKind.AppMutedButRecording, vm.AppMuteBannerKind);
        Assert.Contains("Webex looks muted", vm.AppMuteBannerText);
        Assert.Equal("Mute my side", vm.AppMuteActionLabel);

        await vm.MuteLocalCommand.ExecuteAsync(null);            // the banner's action = existing command
        // Fix 3: assert BEFORE the next poll so the immediate LocalMuteChanged ->
        // ReevaluateAppMuteBanner resolution is verified in isolation. Without this line the later
        // poll masks it - the test would still pass even if that immediate-resolution wiring were
        // deleted (the poll would clear the banner anyway).
        Assert.Equal(AppMuteBannerKind.None, vm.AppMuteBannerKind);
        now = 6200;
        watcher.Poll();
        Assert.Equal(AppMuteBannerKind.None, vm.AppMuteBannerKind);   // resolution clears immediately

        await vm.StopCommand.ExecuteAsync(null);
        await controller.PendingFinalize;
        vm.Dispose();
    }

    [Fact]
    public async Task App_mute_banner_clears_eagerly_when_leaving_Recording_on_Pause()
    {
        // Fix 2 (spec 8.3: the advisory banner is never shown while Idle/Paused). Before the fix the
        // banner cleared only lazily on the next 2 s poll, so a now-false "still recording your side"
        // line lingered for up to ~2 s after Pause. Leaving Recording must clear it at once, with NO
        // poll in between (and re-debounce the evaluator from scratch).
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var src = new FakeAppMuteSignalSource();
        var watcher = new AppMuteWatcher(src, () => controller.State == SessionState.Recording);
        long now = 0;
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options(), appMuteWatcher: watcher, wallClock: () => now);
        await vm.StartCommand.ExecuteAsync(null);

        src.Next = new AppMuteReading(AppMuteState.Muted, "Webex");
        watcher.Poll();                                          // t=0: mismatch begins
        now = 6000;
        watcher.Poll();                                          // >= 5 s: banner shows
        Assert.Equal(AppMuteBannerKind.AppMutedButRecording, vm.AppMuteBannerKind);

        await vm.PauseResumeCommand.ExecuteAsync(null);          // Pause -> leaves Recording
        Assert.Equal(SessionState.Paused, vm.State);
        Assert.Equal(AppMuteBannerKind.None, vm.AppMuteBannerKind);   // cleared eagerly, no poll needed
        Assert.Equal("", vm.AppMuteBannerText);
        Assert.Equal("", vm.AppMuteActionLabel);

        await vm.StopCommand.ExecuteAsync(null);
        await controller.PendingFinalize;
        vm.Dispose();
    }

    [Fact]
    public async Task App_live_but_muted_banner_shows_exact_copy_and_null_app_falls_back()
    {
        // Fix 4: the OTHER locked-rule direction (AppLiveButMuted) plus the null-app-name fallback.
        // The two copy strings are documented evidentiary invariants (ASCII hyphen) - guard them.
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var src = new FakeAppMuteSignalSource();
        var watcher = new AppMuteWatcher(src, () => controller.State == SessionState.Recording);
        long now = 0;
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options(), appMuteWatcher: watcher, wallClock: () => now);
        await vm.StartCommand.ExecuteAsync(null);

        await vm.MuteLocalCommand.ExecuteAsync(null);            // locally muted -> a Live reading now mismatches
        Assert.True(vm.IsLocalMuted);

        src.Next = new AppMuteReading(AppMuteState.Live, "Webex");
        watcher.Poll();                                          // t=0: mismatch begins
        Assert.Equal(AppMuteBannerKind.None, vm.AppMuteBannerKind);
        now = 6000;
        watcher.Poll();                                          // >= 5 s: opposite-direction banner shows
        Assert.Equal(AppMuteBannerKind.AppLiveButMuted, vm.AppMuteBannerKind);
        Assert.Equal("You are unmuted in Webex - LocalScribe is not recording your side.", vm.AppMuteBannerText);
        Assert.Equal("Unmute", vm.AppMuteActionLabel);

        src.Next = new AppMuteReading(AppMuteState.Live, null);  // no app name -> "the call app" fallback
        now = 6200;
        watcher.Poll();
        Assert.Equal(AppMuteBannerKind.AppLiveButMuted, vm.AppMuteBannerKind);
        Assert.Contains("the call app", vm.AppMuteBannerText);

        await vm.StopCommand.ExecuteAsync(null);
        await controller.PendingFinalize;
        vm.Dispose();
    }

    [Fact]
    public async Task App_mute_banner_does_not_carry_across_Stop_then_Start()
    {
        // Fix 5(a): a shown advisory must not survive a Stop -> Start into the next session.
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var src = new FakeAppMuteSignalSource();
        var watcher = new AppMuteWatcher(src, () => controller.State == SessionState.Recording);
        long now = 0;
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options(), appMuteWatcher: watcher, wallClock: () => now);
        await vm.StartCommand.ExecuteAsync(null);

        src.Next = new AppMuteReading(AppMuteState.Muted, "Webex");
        watcher.Poll();
        now = 6000;
        watcher.Poll();
        Assert.Equal(AppMuteBannerKind.AppMutedButRecording, vm.AppMuteBannerKind);   // shown

        await vm.StopCommand.ExecuteAsync(null);
        await controller.PendingFinalize;
        await vm.StartCommand.ExecuteAsync(null);               // next session must open clean

        Assert.Equal(AppMuteBannerKind.None, vm.AppMuteBannerKind);
        Assert.Equal("", vm.AppMuteBannerText);
        Assert.Equal("", vm.AppMuteActionLabel);

        // The three asserts above pass even without Fix 2 (StartAsync resets the surface props
        // unconditionally), so they do NOT bite the EVALUATOR-level no-carry. Feed the SAME mismatch
        // and poll only ~1 s into the restarted session: without Fix 2's Reset-on-leaving-Recording
        // the evaluator's _current would still be AppMutedButRecording from session 1 and re-show
        // IMMEDIATELY; with Fix 2 it re-debounces from scratch, so this early poll stays None.
        src.Next = new AppMuteReading(AppMuteState.Muted, "Webex");
        now += 1000;                                            // ~1 s into the restarted session (<< 5 s)
        watcher.Poll();
        Assert.Equal(AppMuteBannerKind.None, vm.AppMuteBannerKind);

        await vm.StopCommand.ExecuteAsync(null);
        await controller.PendingFinalize;
        vm.Dispose();
    }

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

    [Fact]
    public async Task SwitchRemoteTargetAsync_hot_swaps_and_returns_true()
    {
        var (vm, controller) = MakeVm();
        await vm.StartCommand.ExecuteAsync(null);
        bool ok = await vm.SwitchRemoteTargetAsync(
            new LocalScribe.Core.Model.RemoteSetting { Mode = LocalScribe.Core.Model.RemoteMode.SystemMix });
        Assert.True(ok);
        Assert.Equal(SessionState.Recording, vm.State);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task SwitchRemoteTargetAsync_returns_false_and_notices_on_build_failure()
    {
        var (controller, provider, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        await vm.StartCommand.ExecuteAsync(null);

        provider.ThrowOnNextRemoteCreate = true;
        bool ok = await vm.SwitchRemoteTargetAsync(
            new LocalScribe.Core.Model.RemoteSetting { Mode = LocalScribe.Core.Model.RemoteMode.SystemMix });

        Assert.False(ok);
        Assert.Equal(SessionState.Recording, vm.State);   // old leg untouched
        Assert.NotNull(vm.LastNotice);
        await vm.StopCommand.ExecuteAsync(null);
    }
}
