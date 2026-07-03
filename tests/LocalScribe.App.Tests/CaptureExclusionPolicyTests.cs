using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class CaptureExclusionPolicyTests
{
    [Fact]
    public void Reapply_only_when_the_privacy_toggle_actually_changed()
    {
        var on = new Settings { Privacy = new PrivacySetting { ExcludeWindowsFromCapture = true } };
        var off = new Settings { Privacy = new PrivacySetting { ExcludeWindowsFromCapture = false } };

        Assert.True(CaptureExclusionPolicy.ShouldReapply(on, off));
        Assert.True(CaptureExclusionPolicy.ShouldReapply(off, on));
        Assert.False(CaptureExclusionPolicy.ShouldReapply(off, off));
        // Unrelated settings churn (e.g. timestamps style) must never touch the HWND.
        Assert.False(CaptureExclusionPolicy.ShouldReapply(on, on with { Timestamps = "wallclock" }));
    }
}
