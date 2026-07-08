using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Stage 5.4 section 6 (locked decision 2), widened by the device-selection design
/// section 5: the Record console's per-session target-app override composes over the ONE live
/// Func&lt;Settings&gt; that CompositionRoot.Build hands to SessionController and
/// WasapiCaptureSourceProvider. Apply forces Remote to PerProcess on the override app whenever
/// one is set - regardless of the base mode, so picking an app in Auto captures exactly that app
/// for the session; it is identity when unset and NEVER writes back to the settings service
/// (settings.json stays the single persistent source of truth).</summary>
public sealed class RemoteAppOverrideTests
{
    [Fact]
    public void PerProcess_override_replaces_the_app()
    {
        var settings = new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Webex" } };
        var box = new RemoteAppOverride { App = "CiscoCollabHost" };

        var applied = box.Apply(settings);

        Assert.Equal("CiscoCollabHost", applied.Remote.App);
        Assert.Equal(RemoteMode.PerProcess, applied.Remote.Mode);   // only App is replaced
        Assert.NotSame(settings, applied);                          // a new record, not a mutation
        Assert.Equal("Webex", settings.Remote.App);                 // input record untouched
    }

    [Fact]
    public void Null_empty_or_unset_override_is_identity()
    {
        var settings = new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Webex" } };
        var box = new RemoteAppOverride();

        Assert.Same(settings, box.Apply(settings));                 // unset (null)

        box.App = "";
        Assert.Same(settings, box.Apply(settings));                 // empty string
    }

    [Fact]
    public void Set_override_forces_per_process_from_any_base_mode()
    {
        var box = new RemoteAppOverride { App = "CiscoCollabHost" };
        var auto = new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.Auto, App = "Webex" } };

        var applied = box.Apply(auto);

        // Design section 5: an explicitly chosen app captures THAT app per-process, even from Auto.
        Assert.Equal(RemoteMode.PerProcess, applied.Remote.Mode);
        Assert.Equal("CiscoCollabHost", applied.Remote.App);
        Assert.Equal(RemoteMode.Auto, auto.Remote.Mode);            // input untouched
    }

    [Fact]
    public void Unset_override_leaves_the_base_mode_unchanged()
    {
        var box = new RemoteAppOverride();                          // null
        var auto = new Settings { Remote = new RemoteSetting { Mode = RemoteMode.Auto, App = "Webex" } };
        var mix = new Settings { Remote = new RemoteSetting { Mode = RemoteMode.SystemMix, App = "Webex" } };

        Assert.Same(auto, box.Apply(auto));                        // Auto's auto-detect stands
        Assert.Same(mix, box.Apply(mix));                          // SystemMix stands

        box.App = "";
        Assert.Same(auto, box.Apply(auto));                        // empty string is also identity
    }

    [Fact]
    public void Override_never_touches_the_settings_service()
    {
        var service = new FakeSettingsService(new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Webex" } });
        var box = new RemoteAppOverride();
        // EXACTLY CompositionRoot.Build's composed form: the one live Func<Settings> seam.
        Func<Settings> current = () => box.Apply(service.Current);

        box.App = "CiscoCollabHost";
        var first = current();
        var second = current();                                     // a Resume leg re-resolves

        Assert.Equal("CiscoCollabHost", first.Remote.App);
        Assert.Equal("CiscoCollabHost", second.Remote.App);
        Assert.Equal(0, service.SaveCount);                         // never persisted
        Assert.Equal("Webex", service.Current.Remote.App);          // service snapshot untouched
    }
}
