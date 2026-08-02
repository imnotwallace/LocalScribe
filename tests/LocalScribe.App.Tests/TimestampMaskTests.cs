// tests/LocalScribe.App.Tests/TimestampMaskTests.cs
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Credit-card-expiry style auto-colon mask for the read view's go-to box (UX round
/// 2026-08-03): left-anchored, append-only - digits never shift position, a colon appears by
/// itself once a pair completes. Pure/static/WPF-free, so it is exercised directly here without
/// any VM or window scaffolding.</summary>
public sealed class TimestampMaskTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("1", "1")]
    [InlineData("14", "14")]
    [InlineData("141", "14:1")]
    [InlineData("1415", "14:15")]
    [InlineData("14153", "14:15:3")]
    [InlineData("141530", "14:15:30")]
    public void Format_inserts_a_colon_after_every_completed_pair_left_anchored(string typed, string expected)
        => Assert.Equal(expected, TimestampMask.Format(typed));

    [Fact]
    public void Format_returns_empty_for_null()
        => Assert.Equal("", TimestampMask.Format(null));

    [Fact]
    public void Format_strips_non_digits_so_a_pasted_colonised_stamp_re_masks_cleanly()
    {
        Assert.Equal("14:15", TimestampMask.Format("14:15"));       // already-masked input is idempotent
        Assert.Equal("10:20:3", TimestampMask.Format("1:02:03"));   // digits re-grouped in pairs from the left
        Assert.Equal("12:34", TimestampMask.Format("ab12cd34"));    // arbitrary letters stripped
    }

    [Fact]
    public void Format_caps_at_six_digits_and_ignores_the_rest()
    {
        Assert.Equal("12:34:56", TimestampMask.Format("1234567"));
        Assert.Equal("12:34:56", TimestampMask.Format("123456789"));
    }
}
