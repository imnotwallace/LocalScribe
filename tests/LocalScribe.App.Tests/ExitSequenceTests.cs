using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The shared stop-then-drain-then-exit sequence (Tier 1B design 2026-08-05, T1-2).
/// Extracted from TrayIconHost's Exit menu item because that class has NO tests at all (there is no
/// TrayIconHostTests.cs and no STA harness in this suite), and because Application.SessionEnding
/// must run the SAME sequence - two copies of an evidentiary shutdown path would drift.</summary>
public sealed class ExitSequenceTests
{
    private sealed class Recorder
    {
        public readonly List<string> Calls = new();
        public SessionState State = SessionState.Idle;
        public bool Confirm = true;
        public Task? InFlight;
        public Exception? StopThrows;

        public ExitSequence Build() => new(
            state: () => State,
            stopRecording: () =>
            {
                Calls.Add("stop");
                return StopThrows is null ? Task.CompletedTask : Task.FromException(StopThrows);
            },
            inFlightStop: () => { Calls.Add("inflight"); return InFlight; },
            drainFinalize: () => { Calls.Add("drain"); return Task.CompletedTask; },
            confirmStopWhileRecording: () => { Calls.Add("confirm"); return Confirm; },
            notify: m => Calls.Add("notify:" + m),
            flushDiagnostics: () => { Calls.Add("flush"); return Task.CompletedTask; });
    }

    [Fact]
    public async Task An_idle_exit_still_drains_a_finalize_left_running_by_an_earlier_stop()
    {
        // THE BUG: StopAsync returns Idle the moment audio is closed and hands session.json +
        // the projection regen to a background task. Exiting seconds later - with State already
        // Idle - abandoned that write and turned a finished recording into a crash-recovery husk.
        var r = new Recorder { State = SessionState.Idle };

        Assert.True(await r.Build().RunAsync());

        Assert.Equal(new[] { "drain", "flush" }, r.Calls);
    }

    [Fact]
    public async Task A_recording_exit_confirms_then_stops_then_drains_then_flushes_in_that_order()
    {
        // The flush is LAST on purpose: every earlier step can write diagnostics (the stop, the
        // fault notice, the drain), so flushing before them would persist a log that stops short of
        // the very shutdown it is meant to explain. Shared contract section 1 names this path.
        var r = new Recorder { State = SessionState.Recording };

        Assert.True(await r.Build().RunAsync());

        Assert.Equal(new[] { "confirm", "stop", "drain", "flush" }, r.Calls);
    }

    [Fact]
    public async Task Declining_the_confirm_stops_nothing_drains_nothing_and_refuses_the_shutdown()
    {
        var r = new Recorder { State = SessionState.Recording, Confirm = false };

        Assert.False(await r.Build().RunAsync());          // caller must NOT call Shutdown()

        Assert.Equal(new[] { "confirm" }, r.Calls);        // and nothing is flushed: we are not exiting
    }

    [Fact]
    public async Task A_paused_session_takes_the_same_confirm_and_stop_path_as_a_recording_one()
    {
        var r = new Recorder { State = SessionState.Paused };

        Assert.True(await r.Build().RunAsync());

        Assert.Equal(new[] { "confirm", "stop", "drain", "flush" }, r.Calls);
    }

    [Fact]
    public async Task Finalizing_awaits_the_in_flight_stop_without_re_confirming()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r = new Recorder { State = SessionState.Finalizing, InFlight = gate.Task };

        Task<bool> run = r.Build().RunAsync();
        Assert.False(SpinWait.SpinUntil(() => r.Calls.Contains("drain"), TimeSpan.FromMilliseconds(200)));

        gate.SetResult();
        Assert.True(await run);
        Assert.Equal(new[] { "inflight", "drain", "flush" }, r.Calls);
    }

    [Fact]
    public async Task A_faulted_stop_is_surfaced_and_STILL_drains_and_still_permits_the_exit()
    {
        // The StopAsync FAULT path (a disk-full leg fault) never assigns _pendingFinalize, so the
        // drain await is a no-op there - but it must still HAPPEN, because the drain call sits
        // deliberately OUTSIDE the try/catch that swallows the stop fault. The recovery scan is
        // the documented safety net for the fault path, not this await.
        var r = new Recorder
        {
            State = SessionState.Recording,
            StopThrows = new IOException("There is not enough space on the disk."),
        };

        Assert.True(await r.Build().RunAsync());           // the user asked to exit; still exit

        Assert.Equal(new[] { "confirm", "stop", "notify:Error stopping recording: There is not enough space on the disk.", "drain", "flush" },
            r.Calls);
    }

    [Fact]
    public async Task An_unattended_run_stops_a_recording_session_without_ever_prompting()
    {
        // Windows logoff/shutdown (Application.SessionEnding). The attended path raises a modal
        // MessageBox; on the logoff path NOBODY CAN ANSWER IT - the OS is tearing the session down
        // and the caller can only wait a bounded time. A prompt there means the wait expires with
        // stopRecording never called and a live evidentiary session orphaned with no EndedAtUtc,
        // which is precisely the loss Task 13's log-off smoke item forbids. Windows has already
        // asked the user whether to log off; asking again is both impossible and redundant.
        var r = new Recorder { State = SessionState.Recording, Confirm = false };

        Assert.True(await r.Build().RunUnattendedAsync());   // Confirm=false is IGNORED here

        Assert.Equal(new[] { "stop", "drain", "flush" }, r.Calls);
        Assert.DoesNotContain("confirm", r.Calls);
    }

    [Fact]
    public async Task An_unattended_run_from_idle_still_drains_and_flushes()
    {
        var r = new Recorder { State = SessionState.Idle };

        Assert.True(await r.Build().RunUnattendedAsync());

        Assert.Equal(new[] { "drain", "flush" }, r.Calls);
    }

    [Fact]
    public async Task A_wedged_diagnostic_flush_cannot_hold_the_exit_open()
    {
        // The regression the bound exists to prevent, and the exact shape a bare `await` passes
        // silently: a flush that NEVER completes (dead disk, vanished network path, antivirus
        // holding the file). Unbounded, RunAsync never returns, so the caller never reaches
        // Shutdown(), so App.OnExit's own backstop never runs either - the user is left with a
        // tray process only Task Manager can end. Plan A shipped that shape once and had to fix it.
        var never = new TaskCompletionSource();
        var sequence = new ExitSequence(
            state: () => SessionState.Idle,
            stopRecording: () => Task.CompletedTask,
            inFlightStop: () => null,
            drainFinalize: () => Task.CompletedTask,
            confirmStopWhileRecording: () => true,
            notify: _ => { },
            flushDiagnostics: () => never.Task);

        Task<bool> run = sequence.RunAsync();

        // Generous relative to ShutdownFlush.Timeout (2 s) and still finite: the assertion is that
        // the wait is BOUNDED at all, not that it lands on a particular millisecond.
        Assert.True(await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15))) == run,
            "RunAsync did not return while the diagnostic flush was wedged - the bound is gone.");
        Assert.True(await run);
    }

    [Fact]
    public void The_shutdown_budget_is_eight_seconds_by_default()
    {
        // The number lives HERE rather than as a literal in an App.xaml.cs lambda, because
        // App.xaml.cs has no test coverage in this repo at all (105 test files, no AppTests.cs).
        // 8 s sits inside the OS's own logoff grace and comfortably past a transcript drain plus a
        // session.json write. REJECTED: an unbounded wait - a hung drain would hold up the whole
        // machine's logoff, which is hostile and gets the app killed anyway.
        Assert.Equal(TimeSpan.FromSeconds(8), new Recorder().Build().ShutdownBudget);
    }
}
