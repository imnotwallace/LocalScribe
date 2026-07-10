using System.IO;
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
}
