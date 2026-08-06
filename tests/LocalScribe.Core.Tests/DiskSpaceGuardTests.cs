using LocalScribe.Core.Live;

namespace LocalScribe.Core.Tests;

/// <summary>Disk-space policy (Tier 1B design 2026-08-05, T1-4c). Pure and probe-free: the real
/// DriveInfo call is a delegate seam on SessionController, so nothing here touches a filesystem or
/// depends on the developer's free space.</summary>
public sealed class DiskSpaceGuardTests
{
    private const long Gib = 1024L * 1024 * 1024;

    [Fact]
    public void Refuses_below_the_floor_and_names_the_shortfall()
    {
        string? reason = DiskSpaceGuard.RefusalFor(300L * 1024 * 1024, 2 * Gib);

        Assert.NotNull(reason);
        Assert.Contains("300 MB", reason);      // what is free
        Assert.Contains("2048 MB", reason);     // what is needed
    }

    [Fact]
    public void Permits_at_and_above_the_floor()
    {
        Assert.Null(DiskSpaceGuard.RefusalFor(2 * Gib, 2 * Gib));
        Assert.Null(DiskSpaceGuard.RefusalFor(500 * Gib, 2 * Gib));
    }

    [Fact]
    public void An_unknown_free_space_never_refuses_a_recording()
    {
        // The probe returns null for a UNC path, an unmapped root, or any DriveInfo throw. Refusing
        // to record because we could not MEASURE the disk would block the primary use case on a
        // guess - and the mid-session warning plus the audio-write fault marker still cover the
        // real failure. Fail OPEN.
        Assert.Null(DiskSpaceGuard.RefusalFor(null, 2 * Gib));
    }

    [Fact]
    public void Warns_exactly_once_when_free_space_crosses_below_the_warn_floor()
    {
        var g = new DiskSpaceGuard(warnFloorBytes: Gib);

        Assert.False(g.OnPoll(4 * Gib));
        Assert.True(g.OnPoll(900L * 1024 * 1024));      // crossing: raises
        Assert.False(g.OnPoll(800L * 1024 * 1024));     // still low: never re-raised
        Assert.False(g.OnPoll(700L * 1024 * 1024));
    }

    [Fact]
    public void Recovering_above_the_floor_re_arms_the_warning()
    {
        // The user freed space mid-call. If it drops again that is a NEW fact and must be reported
        // again - the state machine latches the WARNING, not the session.
        var g = new DiskSpaceGuard(warnFloorBytes: Gib);
        Assert.True(g.OnPoll(500L * 1024 * 1024));

        Assert.False(g.OnPoll(8 * Gib));                // recovered - no event, just re-armed
        Assert.True(g.OnPoll(500L * 1024 * 1024));      // dropped again: reported again
    }

    [Fact]
    public void An_unknown_reading_neither_warns_nor_clears()
    {
        var g = new DiskSpaceGuard(warnFloorBytes: Gib);
        Assert.False(g.OnPoll(null));
        Assert.True(g.OnPoll(500L * 1024 * 1024));
        Assert.False(g.OnPoll(null));                   // a failed probe must not "recover" it
        Assert.False(g.OnPoll(400L * 1024 * 1024));     // still latched
    }
}
