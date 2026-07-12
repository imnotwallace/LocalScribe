using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Design 2026-07-12 "Architecture 1": the per-session Remote-target override composes over
/// the ONE live Func&lt;Settings&gt; CompositionRoot hands to SessionController and the capture
/// provider. Apply replaces the whole Remote when set (Auto / app / system mix), is identity when
/// unset, and NEVER writes back to the settings service.</summary>
public sealed class RemoteTargetOverrideTests
{
    [Fact]
    public void Set_override_replaces_the_whole_remote()
    {
        var settings = new Settings { Remote = new RemoteSetting { Mode = RemoteMode.Auto } };
        var box = new RemoteTargetOverride
        { Override = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "CiscoCollabHost" } };

        var applied = box.Apply(settings);

        Assert.Equal(RemoteMode.PerProcess, applied.Remote.Mode);
        Assert.Equal("CiscoCollabHost", applied.Remote.App);
        Assert.NotSame(settings, applied);
        Assert.Equal(RemoteMode.Auto, settings.Remote.Mode);        // input untouched
    }

    [Fact]
    public void System_mix_override_forces_system_mix_from_any_base()
    {
        var box = new RemoteTargetOverride { Override = new RemoteSetting { Mode = RemoteMode.SystemMix } };
        var perApp = new Settings { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Webex" } };
        Assert.Equal(RemoteMode.SystemMix, box.Apply(perApp).Remote.Mode);
    }

    [Fact]
    public void Unset_override_is_identity()
    {
        var settings = new Settings { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Webex" } };
        var box = new RemoteTargetOverride();
        Assert.Same(settings, box.Apply(settings));
    }

    [Fact]
    public void Override_never_touches_the_settings_service()
    {
        var service = new FakeSettingsService(new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.Auto } });
        var box = new RemoteTargetOverride();
        Func<Settings> current = () => box.Apply(service.Current);

        box.Override = new RemoteSetting { Mode = RemoteMode.SystemMix };
        Assert.Equal(RemoteMode.SystemMix, current().Remote.Mode);
        Assert.Equal(RemoteMode.SystemMix, current().Remote.Mode);  // a Resume leg re-resolves
        Assert.Equal(0, service.SaveCount);
        Assert.Equal(RemoteMode.Auto, service.Current.Remote.Mode);
    }
}
