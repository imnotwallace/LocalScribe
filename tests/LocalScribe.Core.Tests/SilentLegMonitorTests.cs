using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.Core.Tests;

/// <summary>Task 7 (Fix #2): pure unit tests for the sustained-no-speech "silent leg" monitor,
/// extracted from SessionController per the task-7 brief's authorized fallback. Driving
/// "audio-but-zero-segments" end-to-end through SessionController's fakes is not just fiddly but
/// impossible to pin deterministically: FakeCaptureSource emits every frame of a leg
/// SYNCHRONOUSLY inside StartLeg (see CapturePipelineTests: "FakeCaptureSource emits
/// synchronously"), so by the time StartAsync returns there is no peak left to fire after a
/// FakeClock.Advance - the controller-level scenario in the brief's Step 1 sketch can never
/// observe the boundary. These tests pin the exact boundary condition directly against the pure
/// SilentLegMonitor state machine instead; SessionController's wiring is thin pass-through onto
/// this class (guarded by a lock, since PeakObserved and segment-insert fire on different
/// threads), covered by the unchanged existing SessionController test suite for the healthy path
/// (FakeClock never advances there, so the grace window can never elapse - no new false
/// positives).</summary>
public sealed class SilentLegMonitorTests
{
    [Fact]
    public void Raises_after_the_grace_window_elapses_with_no_segment()
    {
        var m = new SilentLegMonitor(graceMs: 1000, startMs: 0);

        // Peaks inside the window never raise.
        Assert.False(m.OnPeak(500));
        Assert.False(m.OnPeak(1000));   // exactly at the boundary: not yet exceeded

        // Past the boundary: raises exactly once.
        Assert.True(m.OnPeak(1001));
        Assert.True(m.Flagged);
    }

    [Fact]
    public void Does_not_re_raise_on_subsequent_peaks_while_already_flagged()
    {
        var m = new SilentLegMonitor(graceMs: 1000, startMs: 0);
        Assert.True(m.OnPeak(1500));

        Assert.False(m.OnPeak(2000));
        Assert.False(m.OnPeak(5000));
        Assert.True(m.Flagged);
    }

    [Fact]
    public void Healthy_path_a_segment_within_the_window_means_later_peaks_never_raise()
    {
        var m = new SilentLegMonitor(graceMs: 1000, startMs: 0);

        Assert.False(m.OnPeak(500));
        Assert.False(m.OnSegment(600));   // a segment arrives well inside the window
        Assert.False(m.Flagged);

        // The window restarts from the segment, not from leg-start: a peak just past the OLD
        // deadline (1000) but well inside the NEW one (600 + 1000 = 1600) must not raise.
        Assert.False(m.OnPeak(1200));
        Assert.False(m.Flagged);

        // Only once 1000ms have elapsed with no FURTHER segment does it raise.
        Assert.True(m.OnPeak(1601));
    }

    [Fact]
    public void Clears_on_a_later_segment_once_flagged()
    {
        var m = new SilentLegMonitor(graceMs: 1000, startMs: 0);
        Assert.True(m.OnPeak(1500));
        Assert.True(m.Flagged);

        Assert.True(m.OnSegment(1600));   // clears - returns true exactly once
        Assert.False(m.Flagged);

        // A second segment while already clear does not re-report a clear.
        Assert.False(m.OnSegment(1700));
    }

    [Fact]
    public void Reset_restarts_the_window_and_clears_any_flag_as_on_resume()
    {
        var m = new SilentLegMonitor(graceMs: 1000, startMs: 0);
        Assert.True(m.OnPeak(2000));
        Assert.True(m.Flagged);

        Assert.True(m.Reset(nowMs: 5000));   // was flagged at reset time - reports it
        Assert.False(m.Flagged);

        // Freshly reset: a peak just past the OLD deadline must not raise - the window is
        // measured from the reset time (5000), not leg-start (0).
        Assert.False(m.OnPeak(5999));
        Assert.True(m.OnPeak(6001));
    }

    [Fact]
    public void Reset_reports_false_when_the_leg_was_never_flagged()
    {
        // Notification symmetry (review finding): ResumeAsync uses Reset()'s return value to
        // decide whether to raise a matching SilentLegCleared. A healthy leg that was never
        // flagged must report false, so Resume does not raise a spurious Cleared with no prior
        // Detected.
        var m = new SilentLegMonitor(graceMs: 1000, startMs: 0);
        Assert.False(m.OnPeak(500));   // inside the window - never flags
        Assert.False(m.Flagged);

        Assert.False(m.Reset(nowMs: 5000));
        Assert.False(m.Flagged);
    }
}
