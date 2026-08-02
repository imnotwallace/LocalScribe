using System;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>ScrollEasing is a pure static class with NO WPF references (see ScrollGlide.cs), so
/// it is unit-testable here without an STA harness - unlike the ScrollGlide frame pump and the
/// ReadViewWindow rework that consumes it, which are view-layer and covered by the build gate +
/// manual smoke only (UX round 2026-08-03 item A).</summary>
public sealed class ScrollEasingTests
{
    [Fact]
    public void EaseOutCubic_endpoints_are_exact()
    {
        Assert.Equal(0.0, ScrollEasing.EaseOutCubic(0.0), 10);
        Assert.Equal(1.0, ScrollEasing.EaseOutCubic(1.0), 10);
    }

    [Fact]
    public void EaseOutCubic_clamps_out_of_range_t()
    {
        // Below 0 behaves exactly like 0; above 1 behaves exactly like 1 - a caller that
        // overshoots elapsed/duration past 1.0 (e.g. a delayed final frame) must not overshoot
        // the animated value past the destination.
        Assert.Equal(ScrollEasing.EaseOutCubic(0.0), ScrollEasing.EaseOutCubic(-0.5), 10);
        Assert.Equal(ScrollEasing.EaseOutCubic(1.0), ScrollEasing.EaseOutCubic(1.5), 10);
    }

    [Fact]
    public void EaseOutCubic_is_monotonically_increasing()
    {
        double previous = ScrollEasing.EaseOutCubic(0.0);
        for (double t = 0.05; t <= 1.0; t += 0.05)
        {
            double current = ScrollEasing.EaseOutCubic(t);
            Assert.True(current >= previous, $"t={t}: {current} should be >= previous {previous}");
            previous = current;
        }
    }

    [Fact]
    public void EaseOutCubic_front_loads_the_motion_past_the_midpoint()
    {
        // The defining ease-OUT property: at the halfway point in TIME, MORE than half the
        // distance is already covered (fast start, gentle settle) - a linear curve would give
        // exactly 0.5 here, so this is what distinguishes EaseOutCubic from lerp.
        Assert.True(ScrollEasing.EaseOutCubic(0.5) > 0.5);
    }

    [Fact]
    public void DurationFor_clamps_tiny_distances_to_the_180ms_floor()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(180), ScrollEasing.DurationFor(0));
        Assert.Equal(TimeSpan.FromMilliseconds(180), ScrollEasing.DurationFor(5));
    }

    [Fact]
    public void DurationFor_clamps_huge_distances_to_the_400ms_ceiling()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(400), ScrollEasing.DurationFor(1000));
        Assert.Equal(TimeSpan.FromMilliseconds(400), ScrollEasing.DurationFor(10_000));
    }

    [Fact]
    public void DurationFor_scales_linearly_with_distance_in_the_mid_range()
    {
        // 140 + 0.35 * 300 = 245ms, inside [180, 400] - unclamped, so this pins the exact formula
        // rather than just its clamped edges.
        Assert.Equal(TimeSpan.FromMilliseconds(245), ScrollEasing.DurationFor(300));
    }

    [Fact]
    public void DurationFor_is_symmetric_for_upward_scrolls()
    {
        // Negative distances (scrolling UP toward an earlier row, e.g. after a backward seek)
        // must take exactly as long as the same-magnitude downward scroll - only the magnitude
        // drives duration, never the direction.
        Assert.Equal(ScrollEasing.DurationFor(300), ScrollEasing.DurationFor(-300));
        Assert.Equal(ScrollEasing.DurationFor(1000), ScrollEasing.DurationFor(-1000));
        Assert.Equal(ScrollEasing.DurationFor(0), ScrollEasing.DurationFor(-0.0));
    }
}
