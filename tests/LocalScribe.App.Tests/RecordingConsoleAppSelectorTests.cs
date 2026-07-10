using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Phase 3 Task 6: the Record console's app selector is shown in both Auto and
/// PerProcess mode (design 5.4 section 6 / Task 7), but only PerProcess treats it as a
/// mandatory pin. In Auto/SystemMix-adjacent modes the app is auto-detected (Webex/Zoom); the
/// selector there is an OPTIONAL override, so its label/placeholder must say so instead of
/// reading as a required field. Construction mirrors RecordingConsoleViewModelTests.MakeConsole
/// exactly (same ctor + same fakes), since these two properties are pure presentation derived
/// from the same _settings.Current.Remote.Mode the rest of that suite already exercises.</summary>
public sealed class RecordingConsoleAppSelectorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-console-appsel-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private readonly FakeCaptureDeviceEnumerator _devices =
        new(new AudioDeviceInfo("id-headset", "Headset Microphone"));

    private RecordingConsoleViewModel MakeConsole(Settings initial)
    {
        var settings = new FakeSettingsService(initial);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, settings.Current, dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var over = new RemoteAppOverride();
        var maintenance = new MaintenanceService(new StoragePaths(_root), settings,
            new FakeRecycleBin(), TimeProvider.System);
        var matterSelection = new MatterSelectionOverride();
        var micOverride = new MicOverride();
        return new RecordingConsoleViewModel(settings, session, over, maintenance,
            matterSelection, _devices, micOverride, dispatch: a => a());
    }

    [Fact]
    public void Auto_mode_labels_the_selector_as_an_optional_override()
    {
        var vm = MakeConsole(new Settings { Remote = new RemoteSetting { Mode = RemoteMode.Auto } });
        Assert.Equal("Override app (optional)", vm.AppSelectorLabel);
        Assert.Contains("Auto-detect", vm.AppSelectorPlaceholder);   // "blank = auto-detect" affordance
        Assert.True(vm.ShowAppSelector);
    }

    [Fact]
    public void PerProcess_mode_labels_the_selector_as_the_target_app()
    {
        var vm = MakeConsole(new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Webex" } });
        Assert.Equal("Record this app", vm.AppSelectorLabel);
        Assert.Equal("App to record", vm.AppSelectorPlaceholder);
    }
}
