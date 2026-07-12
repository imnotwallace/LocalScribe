using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Task 6 (design 2026-07-12 section 1): the Record console's Remote-target picker
/// friendly-labels live-discovered apps. A recognized image (Webex/Zoom/Teams/Browser via
/// AppKindResolver.FriendlyName) gets an "image - Friendly" label; an unrecognized one shows the
/// bare process name. Construction mirrors RecordingConsoleViewModelTests.MakeConsole exactly
/// (same ctor + same fakes, including the FakeScanner these tests drive directly).</summary>
public sealed class RecordingConsoleAppSelectorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-console-appsel-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private readonly FakeCaptureDeviceEnumerator _devices =
        new(new AudioDeviceInfo("id-headset", "Headset Microphone"));

    private sealed class FakeScanner : IAudioSessionScanner
    {
        public List<AudioSessionInfo> Active = new();
        public IReadOnlyList<AudioSessionInfo> Scan() => Active;
    }

    private readonly FakeScanner _scanner = new();

    private RecordingConsoleViewModel MakeConsole(Settings initial)
    {
        var settings = new FakeSettingsService(initial);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, settings.Current, dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var over = new RemoteTargetOverride();
        var maintenance = new MaintenanceService(new StoragePaths(_root), settings,
            new FakeRecycleBin(), TimeProvider.System);
        var matterSelection = new MatterSelectionOverride();
        var micOverride = new MicOverride();
        return new RecordingConsoleViewModel(settings, session, over, maintenance,
            matterSelection, _devices, micOverride, _scanner, confirmSystemMix: () => true,
            dispatch: a => a());
    }

    [Fact]
    public async Task Live_item_shows_process_name_with_friendly_suffix()
    {
        var vm = MakeConsole(new Settings { Remote = new RemoteSetting { Mode = RemoteMode.Auto } });
        _scanner.Active.Add(new AudioSessionInfo(9, "CiscoCollabHost"));
        await vm.RefreshRemoteTargetsAsync();
        Assert.Contains(vm.RemoteTargetOptions, o => o.Label == "CiscoCollabHost - Webex");
    }

    [Fact]
    public async Task Unknown_live_process_shows_the_bare_name()
    {
        var vm = MakeConsole(new Settings { Remote = new RemoteSetting { Mode = RemoteMode.Auto } });
        _scanner.Active.Add(new AudioSessionInfo(9, "Spotify"));
        // no friendly suffix, no fullmix annotation
        await vm.RefreshRemoteTargetsAsync();
        Assert.Contains(vm.RemoteTargetOptions, o => o.Label == "Spotify");
    }
}
