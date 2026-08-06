using LocalScribe.Core.Live;

namespace LocalScribe.Core.Tests;

/// <summary>Per-leg frame-arrival watchdog (Tier 1B design 2026-08-05, T1-4a). Tested in isolation
/// for the same reason SilentLegMonitor is: FakeCaptureSource replays every frame SYNCHRONOUSLY
/// inside Start() and FakeClock never advances on its own, so no end-to-end controller test can
/// starve a leg and then observe a timeout. The controller's wiring is thin pass-through onto this
/// class under a lock, covered by SessionControllerCaptureHealthTests.</summary>
public sealed class FrameArrivalWatchdogTests
{
    [Fact]
    public void Does_not_trip_inside_the_grace_window()
    {
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 0);

        Assert.False(w.Tick(4000));
        Assert.False(w.Tick(8000));       // exactly at the boundary: not yet EXCEEDED
        Assert.False(w.Stalled);
    }

    [Fact]
    public void Trips_exactly_once_past_the_grace_window()
    {
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 0);

        Assert.True(w.Tick(8001));        // raises
        Assert.True(w.Stalled);
        Assert.False(w.Tick(20_000));     // persistent, never re-raised
        Assert.False(w.Tick(60_000));
    }

    [Fact]
    public void A_frame_resets_the_window_so_a_healthy_leg_never_trips()
    {
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 0);

        for (long t = 1000; t <= 60_000; t += 1000)
        {
            w.OnFrame(t);
            Assert.False(w.Tick(t));
        }
        Assert.False(w.Stalled);
    }

    [Fact]
    public void A_frame_after_a_stall_clears_it_exactly_once()
    {
        // Notification symmetry, the rule SilentLegMonitor.Reset's comment states: every raised
        // "stalled" must have exactly one matching "recovered", or a banner driven off the pair
        // stays stuck on after the leg comes back.
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 0);
        Assert.True(w.Tick(9000));

        Assert.True(w.OnFrame(9500));     // clears - exactly once
        Assert.False(w.Stalled);
        Assert.False(w.OnFrame(9600));    // every later frame is unremarkable
    }

    [Fact]
    public void A_frame_while_healthy_reports_no_transition()
    {
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 0);
        Assert.False(w.OnFrame(100));
    }

    [Fact]
    public void Reset_rearms_the_window_from_now_and_reports_whether_it_was_stalled()
    {
        // Called at every point a fresh leg starts (Resume, unmute, remote re-target, watchdog
        // restart). The return value lets the caller raise the matching "recovered" for a leg that
        // was flagged at the moment it was replaced.
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 0);
        Assert.True(w.Tick(9000));

        Assert.True(w.Reset(10_000));     // was stalled
        Assert.False(w.Stalled);
        Assert.False(w.Tick(18_000));     // window restarted from 10_000
        Assert.True(w.Tick(18_001));

        Assert.True(w.Reset(20_000));
        Assert.False(w.Reset(20_000));    // second reset: nothing to clear
    }

    [Fact]
    public void ForceStale_makes_the_next_tick_trip_but_never_un_flags_a_leg_already_reported()
    {
        // The fast path: a source that has told us it is dead (ICaptureHealthObservable) should not
        // have to wait out the grace window. ForceStale rewinds the last-frame stamp so the NEXT
        // Tick trips - but it must be inert once the leg is ALREADY flagged. Reset() cannot be used
        // for this: Reset CLEARS _stalled, so an already-reported leg would be silently un-flagged,
        // re-reported and re-marked a second time, and its matching CaptureRecovered swallowed.
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 0);
        w.OnFrame(10_000);

        w.ForceStale(10_000);
        Assert.False(w.Stalled);          // ForceStale itself raises nothing - Tick decides
        Assert.True(w.Tick(10_001));      // no grace left to wait out
        Assert.True(w.Stalled);

        w.ForceStale(20_000);             // already flagged and already reported
        Assert.True(w.Stalled);           // NOT un-flagged
        Assert.False(w.Tick(20_001));     // and NOT re-raised
    }

    [Fact]
    public void A_clock_that_appears_to_move_backwards_never_trips_it()
    {
        // The session clock is monotonic (StopwatchClock/QPC), but the watchdog is also driven from
        // a UI DispatcherTimer reading it across threads. A negative delta must read as "a frame
        // arrived very recently", never as a huge positive gap.
        var w = new FrameArrivalWatchdog(graceMs: 8000, startMs: 30_000);

        Assert.False(w.Tick(1000));
        Assert.False(w.Stalled);
    }
}
