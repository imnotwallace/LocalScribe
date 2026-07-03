using System.IO;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SettingsPageViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-setvm-" + Guid.NewGuid().ToString("N"));
    private FakeSettingsService _settings = new();
    private readonly FakeUiErrorReporter _errors = new();
    private readonly FakeLaunchAtLogin _launch = new();
    private string? _pickResult;

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
            dispatch: a => a(), modelsRoot: Path.Combine(_root, "models"));
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
    public void Mic_and_retention_are_read_only_displays()
    {
        var follow = MakeVm();
        Assert.Contains("Communications default", follow.MicDisplay);
        var pinned = MakeVm(new Settings { Mic = new MicSetting { Mode = MicMode.Pinned, Name = "Shure MV7" } });
        Assert.Contains("Shure MV7", pinned.MicDisplay);
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
    public void Vm_exposes_no_dropped_setting_surfaces()
    {
        // Design 6.1: recordingIndicator, hotkeys, autoDetect, vocabulary are NOT exposed.
        var names = typeof(SettingsPageViewModel).GetProperties().Select(p => p.Name).ToArray();
        foreach (string banned in new[] { "RecordingIndicator", "Hotkey", "AutoDetect", "Vocabulary" })
            Assert.DoesNotContain(names, n => n.Contains(banned, StringComparison.OrdinalIgnoreCase));
    }
}
