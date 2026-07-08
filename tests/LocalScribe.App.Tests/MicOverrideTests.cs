using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The Record console's per-session mic override (design section 3), twin of
/// RemoteAppOverride: composes over the ONE live Func&lt;Settings&gt; and never writes settings.json.
/// A set Override wins (a device pin OR follow-default); unset is identity so the persistent
/// Settings pin stands.</summary>
public sealed class MicOverrideTests
{
    private static Settings Pinned(string id, string name) => new()
    { Mic = new MicSetting { Mode = MicMode.Pinned, Id = id, Name = name } };

    [Fact]
    public void Set_override_replaces_the_mic()
    {
        var settings = Pinned("id-saved", "Saved Studio Mic");
        var box = new MicOverride
        { Override = new MicSetting { Mode = MicMode.Pinned, Id = "id-session", Name = "Session Headset" } };

        var applied = box.Apply(settings);

        Assert.Equal("id-session", applied.Mic.Id);
        Assert.Equal(MicMode.Pinned, applied.Mic.Mode);
        Assert.NotSame(settings, applied);                       // new record, not a mutation
        Assert.Equal("id-saved", settings.Mic.Id);               // input untouched
    }

    [Fact]
    public void Override_can_force_follow_default_over_a_persistent_pin()
    {
        var settings = Pinned("id-saved", "Saved Studio Mic");
        var box = new MicOverride { Override = new MicSetting { Mode = MicMode.FollowDefault } };

        var applied = box.Apply(settings);

        Assert.Equal(MicMode.FollowDefault, applied.Mic.Mode);
        Assert.Null(applied.Mic.Id);
    }

    [Fact]
    public void Unset_override_is_identity()
    {
        var settings = Pinned("id-saved", "Saved Studio Mic");
        Assert.Same(settings, new MicOverride().Apply(settings));
    }
}
