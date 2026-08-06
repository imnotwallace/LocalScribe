using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Suspend/resume policy (Tier 1B design 2026-08-05, T1-4d). Extracted from the
/// SystemEvents.PowerModeChanged handler for the StopConfirmToastGuard reason: App.xaml.cs has no
/// test coverage at all in this repo, so a decision left in an event handler is a decision that is
/// never tested. TimeProvider is injected because the wall-clock gap is the whole point.</summary>
public sealed class PowerTransitionCoordinatorTests
{
    private sealed class Harness
    {
        public SessionState State = SessionState.Recording;
        public readonly List<string> Calls = new();
        public readonly ManualUtcTimeProvider Time =
            new(new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero));
        public Exception? PauseThrows;

        public PowerTransitionCoordinator Build() => new(
            state: () => State,
            pauseForSleep: () =>
            {
                Calls.Add("pause");
                if (PauseThrows is not null) return Task.FromException(PauseThrows);
                State = SessionState.Paused;
                return Task.CompletedTask;
            },
            resumeAfterSleep: gap =>
            {
                Calls.Add("resume:" + gap.ToString("c", System.Globalization.CultureInfo.InvariantCulture));
                State = SessionState.Recording;
                return Task.CompletedTask;
            },
            Time,
            notify: m => Calls.Add("notify:" + m));
    }

    [Fact]
    public async Task A_suspend_while_recording_pauses_and_a_resume_reports_the_wall_clock_gap()
    {
        var h = new Harness();
        var c = h.Build();

        await c.OnSuspendAsync();
        Assert.True(c.AutoPaused);

        h.Time.Set(new DateTimeOffset(2026, 8, 5, 10, 37, 0, TimeSpan.Zero));
        await c.OnResumeAsync();

        Assert.Equal("pause", h.Calls[0]);
        Assert.Contains("resume:00:37:00", h.Calls);
        Assert.False(c.AutoPaused);
    }

    [Fact]
    public async Task A_suspend_while_idle_does_nothing_at_all()
    {
        var h = new Harness { State = SessionState.Idle };
        var c = h.Build();

        await c.OnSuspendAsync();
        await c.OnResumeAsync();

        Assert.Empty(h.Calls);
        Assert.False(c.AutoPaused);
    }

    [Fact]
    public async Task A_session_the_user_had_already_paused_is_never_auto_resumed()
    {
        // The evidentiary rule: a user who paused for a privileged aside and then closed the lid
        // must NOT come back to a recording session. Only a pause this coordinator performed is
        // ever undone by it.
        var h = new Harness { State = SessionState.Paused };
        var c = h.Build();

        await c.OnSuspendAsync();
        await c.OnResumeAsync();

        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task A_resume_without_a_preceding_suspend_is_a_no_op()
    {
        var h = new Harness();
        var c = h.Build();

        await c.OnResumeAsync();

        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task A_second_resume_does_not_resume_twice()
    {
        // Windows can raise Resume more than once for one suspend (Resume + ResumeAutomatic), and
        // a second ResumeAsync against an already-recording session would only log "Nothing to
        // resume" - but the coordinator must not report a second, wrong gap either.
        var h = new Harness();
        var c = h.Build();
        await c.OnSuspendAsync();
        h.Time.Set(new DateTimeOffset(2026, 8, 5, 10, 5, 0, TimeSpan.Zero));

        await c.OnResumeAsync();
        await c.OnResumeAsync();

        Assert.Single(h.Calls, x => x.StartsWith("resume:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_clock_that_appears_to_move_backwards_reports_a_zero_gap_not_a_negative_one()
    {
        var h = new Harness();
        var c = h.Build();
        await c.OnSuspendAsync();
        h.Time.Set(new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero));   // NTP correction

        await c.OnResumeAsync();

        Assert.Contains("resume:00:00:00", h.Calls);
    }

    [Fact]
    public async Task A_failing_pause_is_surfaced_and_never_thrown_at_the_system_events_callback()
    {
        // This runs from a SystemEvents callback during a suspend. An exception escaping there is
        // an unhandled exception on a thread nobody is watching, at the worst possible moment.
        var h = new Harness { PauseThrows = new InvalidOperationException("device gone") };
        var c = h.Build();

        await c.OnSuspendAsync();                      // must not throw

        Assert.Contains(h.Calls, x => x.StartsWith("notify:", StringComparison.Ordinal));
        Assert.False(c.AutoPaused);                    // the pause did not happen: never auto-resume
    }

    [Fact]
    public async Task The_power_mode_branch_itself_is_decided_here_not_in_an_App_xaml_lambda()
    {
        // SHARED-CONTRACT section 4 (trap 9): App.xaml.cs has NO test coverage in this repo - 105
        // test files, no AppTests.cs - so a suspend-vs-resume branch written into the
        // PowerModeChanged lambda is a branch nothing ever exercises. OnPowerModeAsync owns it; the
        // handler is left with one delegating line.
        var h = new Harness();
        var c = h.Build();

        await c.OnPowerModeAsync(suspending: true);
        Assert.True(c.AutoPaused);

        h.Time.Set(new DateTimeOffset(2026, 8, 5, 10, 12, 0, TimeSpan.Zero));
        await c.OnPowerModeAsync(suspending: false);

        Assert.Equal(new[] { "pause", "resume:00:12:00" }, h.Calls);
        Assert.False(c.AutoPaused);
    }
}
