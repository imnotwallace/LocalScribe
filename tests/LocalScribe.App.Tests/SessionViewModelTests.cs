using System.IO;
using LocalScribe.App.ViewModels;
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
}
