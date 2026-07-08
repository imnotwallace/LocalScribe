using System.IO;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SettingsPageViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-setvm-" + Guid.NewGuid().ToString("N"));
    private FakeSettingsService _settings = new();
    private readonly FakeUiErrorReporter _errors = new();
    private readonly FakeLaunchAtLogin _launch = new();
    private string? _pickResult;
    private FakeCaptureDeviceEnumerator _devices =
        new(new AudioDeviceInfo("id-headset", "Headset Microphone"),
            new AudioDeviceInfo("id-webcam", "Webcam Mic"));

    public SettingsPageViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        Directory.CreateDirectory(Path.Combine(_root, "storage", "sessions"));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private SettingsPageViewModel MakeVm(Settings? initial = null)
    {
        if (initial is not null) _settings = new FakeSettingsService(initial);
        var maintenance = new Services.MaintenanceService(
            new StoragePaths(Path.Combine(_root, "storage")), _settings, new FakeRecycleBin(),
            TimeProvider.System);
        return new SettingsPageViewModel(_settings, maintenance, _launch,
            pickFolder: () => _pickResult, openFolder: _ => { }, _errors,
            dispatch: a => a(), _devices, modelsRoot: Path.Combine(_root, "models"));
    }

    [Fact]
    public async Task Pick_folder_stores_the_literal_path_and_flags_restart_required()
    {
        var vm = MakeVm();
        _pickResult = Path.Combine(_root, "new-home");
        vm.PickStorageRootCommand.Execute(null);
        await vm.LastSave;
        Assert.Equal(_pickResult, _settings.Current.StorageRoot);   // literal, never re-tokenized
        Assert.Equal(_pickResult, vm.StorageRoot);
        Assert.True(vm.RestartRequired);
        Assert.Contains("restart", vm.RestartRequiredNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelled_pick_saves_nothing()
    {
        var vm = MakeVm();
        _pickResult = null;
        vm.PickStorageRootCommand.Execute(null);
        await vm.LastSave;
        Assert.Equal(0, _settings.SaveCount);
        Assert.False(vm.RestartRequired);
    }

    [Fact]
    public void Sync_provider_warning_fires_only_under_a_known_provider()
    {
        var warned = MakeVm(new Settings { StorageRoot = Path.Combine(_root, "OneDrive", "LocalScribe") });
        Assert.NotNull(warned.SyncProviderWarning);
        Assert.Contains("OneDrive", warned.SyncProviderWarning);
        var clean = MakeVm(new Settings { StorageRoot = Path.Combine(_root, "plain") });
        Assert.Null(clean.SyncProviderWarning);
    }

    [Fact]
    public async Task Regenerate_all_projections_runs_and_resets_state()
    {
        var vm = MakeVm(new Settings { StorageRoot = Path.Combine(_root, "storage") });
        await vm.RegenerateAllProjectionsCommand.ExecuteAsync(null);
        Assert.False(vm.IsRegenerating);
        Assert.Empty(_errors.Reports);
    }

    [Fact]
    public async Task Recording_fields_commit_and_carry_the_next_start_note()
    {
        var vm = MakeVm();
        vm.AudioFormat = AudioFormat.Wav;
        await vm.LastSave;
        vm.RemoteMode = RemoteMode.SystemMix;
        await vm.LastSave;
        Assert.Equal(AudioFormat.Wav, _settings.Current.AudioFormat);
        Assert.Equal(RemoteMode.SystemMix, _settings.Current.Remote.Mode);
        Assert.Contains("next Start", vm.RecordingApplyNote);
        Assert.Equal(new[] { AudioFormat.Flac, AudioFormat.Wav }, vm.AudioFormatChoices);
        Assert.Equal(new[] { RemoteMode.Auto, RemoteMode.PerProcess, RemoteMode.SystemMix }, vm.RemoteModeChoices);
    }

    [Fact]
    public void Retention_is_a_read_only_display()
    {
        // Mic is now the picker (see the mic-picker facts below); retention stays read-only.
        var follow = MakeVm();
        Assert.Contains("Keep everything", follow.AudioRetentionDisplay);
        var legacy = MakeVm(new Settings { AudioRetention = "days:30" });
        Assert.Contains("days:30", legacy.AudioRetentionDisplay);
    }

    [Fact]
    public async Task Model_choices_enumerate_only_installed_ggml_files_plus_auto()
    {
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-tiny.en.bin"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-small.bin"), new byte[] { 1 });
        File.WriteAllText(Path.Combine(_root, "models", "silero_vad.onnx"), "x");   // not a whisper model
        var vm = MakeVm();
        Assert.Equal(new[] { "auto", "small", "tiny.en" }, vm.ModelChoices);
        vm.Model = "tiny.en";
        await vm.LastSave;
        Assert.Equal("tiny.en", _settings.Current.Model);
    }

    [Fact]
    public async Task Backend_and_language_commit_and_blank_language_normalizes_to_auto()
    {
        var vm = MakeVm();
        Assert.Equal("auto", vm.Language);
        vm.Backend = Backend.Cpu;
        await vm.LastSave;
        vm.Language = "  ";
        await vm.LastSave;
        Assert.Equal(Backend.Cpu, _settings.Current.Backend);
        Assert.Equal("auto", _settings.Current.Language);
        vm.Language = "en";
        await vm.LastSave;
        Assert.Equal("en", _settings.Current.Language);
    }

    [Fact]
    public async Task Identity_commits_and_blank_role_normalizes_to_null()
    {
        var vm = MakeVm();
        vm.SelfName = "Sam";
        await vm.LastSave;
        vm.SelfRole = "  ";
        await vm.LastSave;
        Assert.Equal("Sam", _settings.Current.Self.Name);
        Assert.Null(_settings.Current.Self.Role);
        vm.SelfRole = "Attorney";
        await vm.LastSave;
        Assert.Equal("Attorney", _settings.Current.Self.Role);
    }

    [Fact]
    public async Task Privacy_toggles_commit_to_privacy_and_overlay_settings()
    {
        var vm = MakeVm();
        Assert.True(vm.ExcludeWindowsFromCapture);              // default true (design section 2)
        vm.ExcludeWindowsFromCapture = false;
        await vm.LastSave;
        vm.OverlayShowSessionName = true;
        await vm.LastSave;
        vm.OverlayExcludeFromCapture = false;
        await vm.LastSave;
        vm.OverlayShowLevelMeter = false;
        await vm.LastSave;
        vm.OverlayEnabled = false;
        await vm.LastSave;
        Assert.False(_settings.Current.Privacy.ExcludeWindowsFromCapture);
        Assert.True(_settings.Current.Overlay.ShowSessionName);
        Assert.False(_settings.Current.Overlay.ExcludeFromCapture);
        Assert.False(_settings.Current.Overlay.ShowLevelMeter);
        Assert.False(_settings.Current.Overlay.Enabled);
        Assert.Contains("redacted", vm.LoggingRedactionNote);
    }

    [Fact]
    public async Task Launch_at_login_drives_the_seam_and_persists()
    {
        var vm = MakeVm();
        vm.LaunchAtLogin = false;
        await vm.LastSave;
        Assert.Equal(new[] { false }, _launch.SetCalls);
        Assert.False(_settings.Current.LaunchAtLogin);
        vm.Timestamps = "wallclock";
        await vm.LastSave;
        Assert.Equal("wallclock", _settings.Current.Timestamps);
    }

    [Fact]
    public async Task Two_unawaited_commits_to_different_fields_both_persist()
    {
        // F3 (Stage4 review): two quick commits must not lose an update. Against the REAL
        // SettingsService (async file I/O), the second commit is built BEFORE the first swaps
        // Current; the VM chains commits and SettingsService serializes the write+swap, so both
        // fields survive - in memory and on disk - with no settings.json.tmp collision.
        string path = Path.Combine(_root, "settings.json");
        var real = new Services.SettingsService(path, new Settings());
        var maintenance = new Services.MaintenanceService(
            new StoragePaths(Path.Combine(_root, "storage")), real, new FakeRecycleBin(),
            TimeProvider.System);
        var vm = new SettingsPageViewModel(real, maintenance, _launch,
            pickFolder: () => _pickResult, openFolder: _ => { }, _errors,
            dispatch: a => a(), _devices, modelsRoot: Path.Combine(_root, "models"));

        vm.AudioFormat = AudioFormat.Wav;   // commit 1 (fire-and-forget)
        vm.Backend = Backend.Cpu;           // commit 2, built before commit 1's Current swap
        await vm.LastSave;

        Assert.Equal(AudioFormat.Wav, real.Current.AudioFormat);   // no lost update in memory
        Assert.Equal(Backend.Cpu, real.Current.Backend);
        var reloaded = await new SettingsStore(path).LoadOrDefaultAsync(CancellationToken.None);
        Assert.Equal(AudioFormat.Wav, reloaded.AudioFormat);       // ...nor on disk
        Assert.Equal(Backend.Cpu, reloaded.Backend);
        Assert.Empty(_errors.Reports);                             // no .tmp collision surfaced
    }

    [Fact]
    public void Vm_exposes_no_dropped_setting_surfaces()
    {
        // Design 6.1: recordingIndicator, hotkeys, autoDetect are NOT exposed. Vocabulary IS
        // exposed as of Stage 6.2 (see Adding_a_global_term_persists_to_settings_vocabulary).
        var names = typeof(SettingsPageViewModel).GetProperties().Select(p => p.Name).ToArray();
        foreach (string banned in new[] { "RecordingIndicator", "Hotkey", "AutoDetect" })
            Assert.DoesNotContain(names, n => n.Contains(banned, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RemoteApp_set_persists_the_trimmed_value()
    {
        var vm = MakeVm(new Settings { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess } });
        vm.RemoteApp = "  CiscoCollabHost  ";
        await vm.LastSave;
        Assert.Equal("CiscoCollabHost", _settings.Current.Remote.App);
        Assert.Equal("CiscoCollabHost", vm.RemoteApp);
        Assert.Equal(RemoteMode.PerProcess, _settings.Current.Remote.Mode);   // mode untouched
    }

    [Fact]
    public async Task RemoteApp_whitespace_clears_to_null()
    {
        var vm = MakeVm(new Settings
        { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Zoom" } });
        vm.RemoteApp = "   ";
        await vm.LastSave;
        Assert.Null(_settings.Current.Remote.App);
        Assert.Equal("", vm.RemoteApp);
    }

    [Fact]
    public void RemoteApp_roundtrips_from_current_settings()
    {
        var seeded = MakeVm(new Settings { Remote = new RemoteSetting { App = "Zoom" } });
        Assert.Equal("Zoom", seeded.RemoteApp);
        var blank = MakeVm(new Settings { Remote = new RemoteSetting { App = null } });
        Assert.Equal("", blank.RemoteApp);
        // One shared suggestion list (Core), plus the note that names Webex's audio process.
        Assert.Equal(new[] { "CiscoCollabHost", "Webex", "Zoom" }, seeded.RemoteAppSuggestions);
        Assert.Contains("CiscoCollabHost", seeded.RemoteAppNote);
    }

    [Fact]
    public async Task RemoteMode_change_notifies_IsPerProcess()
    {
        var vm = MakeVm();                                          // default Remote.Mode == Auto
        Assert.False(vm.IsPerProcess);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.RemoteMode = RemoteMode.PerProcess;
        await vm.LastSave;

        Assert.True(vm.IsPerProcess);                               // flipped false -> true
        Assert.Contains(nameof(SettingsPageViewModel.IsPerProcess), changed);
        Assert.Contains(nameof(SettingsPageViewModel.RemoteMode), changed);
    }

    [Fact]
    public async Task Adding_a_global_term_persists_to_settings_vocabulary()
    {
        var vm = MakeVm();
        vm.Vocabulary.NewTerm = "arraignment";
        await vm.Vocabulary.AddTermCommand.ExecuteAsync(null);
        await vm.LastSave;

        Assert.Contains("arraignment", _settings.Current.Vocabulary.Terms);
    }

    [Fact]
    public async Task Selecting_a_device_pins_it()
    {
        var vm = MakeVm();
        var device = vm.MicChoices.First(c => c.Id == "id-headset");
        vm.SelectedMic = device;
        await vm.LastSave;
        Assert.Equal(MicMode.Pinned, _settings.Current.Mic.Mode);
        Assert.Equal("id-headset", _settings.Current.Mic.Id);
        Assert.Equal("Headset Microphone", _settings.Current.Mic.Name);
    }

    [Fact]
    public async Task Selecting_follow_default_clears_the_pin()
    {
        var vm = MakeVm(new Settings
        { Mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-headset", Name = "Headset Microphone" } });
        vm.SelectedMic = vm.MicChoices.First(c => c.Id is null);   // the follow-default choice
        await vm.LastSave;
        Assert.Equal(MicMode.FollowDefault, _settings.Current.Mic.Mode);
        Assert.Null(_settings.Current.Mic.Id);
    }

    [Fact]
    public void Absent_saved_pin_surfaces_not_connected_and_stays_selected()
    {
        var vm = MakeVm(new Settings
        { Mic = new MicSetting { Mode = MicMode.Pinned, Id = "id-unplugged", Name = "Old USB Mic" } });
        Assert.Equal("id-unplugged", vm.SelectedMic.Id);
        Assert.Contains("not connected", vm.SelectedMic.Label);
    }

    [Fact]
    public void Enumeration_failure_leaves_only_follow_default()
    {
        _devices = new FakeCaptureDeviceEnumerator();              // empty list (enumeration failed)
        var vm = MakeVm();
        Assert.Single(vm.MicChoices);
        Assert.Null(vm.MicChoices[0].Id);
    }
}
