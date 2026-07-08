// tests/LocalScribe.Core.Tests/MicCapturePlannerTests.cs
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.Core.Tests;

public class MicCapturePlannerTests
{
    private static readonly AudioDeviceInfo[] TwoMics =
    [
        new("id-headset", "Headset Microphone"),
        new("id-webcam", "Webcam Mic"),
    ];

    [Fact]
    public void PinnedPresent_OpensById_NoFallback()
    {
        var mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-headset", Name = "Headset Microphone" };
        var plan = MicCapturePlanner.Plan(mic, TwoMics);
        Assert.Equal(MicMode.Pinned, plan.Mode);
        Assert.Equal("id-headset", plan.DeviceId);
        Assert.False(plan.FellBackToDefault);
    }

    [Fact]
    public void PinnedAbsent_FallsBackToDefault_AndFlagsIt()
    {
        var mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-unplugged", Name = "Old USB Mic" };
        var plan = MicCapturePlanner.Plan(mic, TwoMics);
        Assert.Equal(MicMode.FollowDefault, plan.Mode);
        Assert.Null(plan.DeviceId);
        Assert.True(plan.FellBackToDefault);
    }

    [Fact]
    public void PinnedWithNullId_IsTreatedAsFollowDefault_NoFallbackMarker()
    {
        // A malformed pin (mode pinned but no id) is not an "unavailable device"; it is just
        // follow-default. No marker (nothing was pinned to be unavailable).
        var mic = new MicSetting { Mode = MicMode.Pinned, Id = null, Name = null };
        var plan = MicCapturePlanner.Plan(mic, TwoMics);
        Assert.Equal(MicMode.FollowDefault, plan.Mode);
        Assert.False(plan.FellBackToDefault);
    }

    [Fact]
    public void FollowDefault_IsUnchanged()
    {
        var plan = MicCapturePlanner.Plan(new MicSetting(), TwoMics);
        Assert.Equal(MicMode.FollowDefault, plan.Mode);
        Assert.Null(plan.DeviceId);
        Assert.False(plan.FellBackToDefault);
    }

    [Fact]
    public void PinnedPresent_ButEmptyDeviceList_FallsBack()
    {
        var mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-headset", Name = "Headset Microphone" };
        var plan = MicCapturePlanner.Plan(mic, []);   // enumeration returned nothing
        Assert.Equal(MicMode.FollowDefault, plan.Mode);
        Assert.True(plan.FellBackToDefault);
    }
}
