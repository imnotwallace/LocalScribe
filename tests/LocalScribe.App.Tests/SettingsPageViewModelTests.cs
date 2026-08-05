using System.IO;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Diagnostics;
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
    // Tier 1 plan A (2026-08-05): CAPTURING, not discarding. MakeVm passed `openFolder: _ => { }`
    // until this round, which is why no test ever asserted anything about a folder command -
    // including the OpenMcpAuditFolderCommand this one is modelled on.
    private readonly List<string> _openedFolders = new();
    private readonly List<string> _copied = new();
    private FakeCaptureDeviceEnumerator _devices =
        new(new AudioDeviceInfo("id-headset", "Headset Microphone"),
            new AudioDeviceInfo("id-webcam", "Webcam Mic"));

    public SettingsPageViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        Directory.CreateDirectory(Path.Combine(_root, "storage", "sessions"));
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private SettingsPageViewModel MakeVm(Settings? initial = null,
        Func<string?>? assistantHelperProbe = null,
        StoragePaths? paths = null,
        string? buildInfo = null,
        Func<DiagnosticEntry?>? lastError = null)
    {
        initial ??= new Settings();
        // Hermetic isolation (review finding): the VM ctor unconditionally runs LoadMcpAsync,
        // which reads mcp/consent.json and matters/matters.json off StorageRoot. A default
        // Settings().StorageRoot resolves to the REAL %USERPROFILE%/LocalScribe, so an unrelated
        // test that doesn't care about StorageRoot would otherwise touch the developer's real
        // legal-transcript matter index (same class of machine-dependence AssistantHelperLocator.
        // FindExe is guarded against elsewhere). Give every test an isolated temp root unless it
        // deliberately picked its own (e.g. the OneDrive sync-provider test).
        if (initial.StorageRoot == new Settings().StorageRoot)
            initial = initial with { StorageRoot = Path.Combine(_root, "storage") };
        _settings = new FakeSettingsService(initial);
        var maintenance = new Services.MaintenanceService(
            new StoragePaths(Path.Combine(_root, "storage")), _settings, new FakeRecycleBin(),
            TimeProvider.System);
        return new SettingsPageViewModel(_settings, maintenance, _launch,
            pickFolder: () => _pickResult, openFolder: _openedFolders.Add, _errors,
            dispatch: a => a(), _devices, modelsRoot: Path.Combine(_root, "models"),
            // Deterministic default (Task 5 review finding 2): without this, an unspecified probe
            // falls through to the real AssistantHelperLocator.FindExe() and the real filesystem
            // (including the repo tools\assistant\ dev fallback), making the suite machine-dependent.
            assistantHelperProbe: assistantHelperProbe ?? (() => null),
            paths: paths,
            buildInfo: buildInfo,
            lastError: lastError,
            copyToClipboard: _copied.Add);
    }

    [Fact]
    public void Open_diagnostics_folder_creates_and_opens_the_PINNED_diagnostics_dir()
    {
        // Deliberately NOT _settings.Current.StorageRoot (which OpenMcpAuditFolderCommand uses):
        // the log writes under the root CompositionRoot pinned at startup, so after a storage-root
        // change the live value points at a folder the log has never written to.
        var pinned = new StoragePaths(Path.Combine(_root, "pinned"));
        var vm = MakeVm(paths: pinned);

        vm.OpenDiagnosticsFolderCommand.Execute(null);

        Assert.True(Directory.Exists(pinned.DiagnosticsDir));
        Assert.Equal(new[] { pinned.DiagnosticsDir }, _openedFolders);
    }

    [Fact]
    public void Open_diagnostics_folder_is_inert_when_no_paths_were_injected()
    {
        // paths is an OPTIONAL ctor parameter and null in most unit tests; the command must
        // degrade to a no-op rather than throw a NullReferenceException at the user.
        MakeVm().OpenDiagnosticsFolderCommand.Execute(null);
        Assert.Empty(_openedFolders);
    }

    [Fact]
    public void The_version_line_shows_the_build_stamp()
    {
        Assert.Equal("LocalScribe 0.9.0+g1628935", MakeVm(buildInfo: "0.9.0+g1628935").AppVersionLine);
        // No stamp injected (unit tests, and any future composition that forgets it): say so
        // rather than render "LocalScribe " with a blank where the version should be.
        Assert.Contains("development", MakeVm().AppVersionLine);
    }

    [Fact]
    public void Copy_last_error_copies_the_build_stamp_together_with_the_recorded_error()
    {
        var entry = new DiagnosticEntry(new DateTimeOffset(2026, 8, 5, 9, 30, 0, TimeSpan.Zero),
            "error", "dispatcher", "Unhandled dispatcher exception",
            "System.IO.IOException: [redacted]");
        var vm = MakeVm(buildInfo: "0.9.0+g1628935", lastError: () => entry);

        vm.CopyLastErrorCommand.Execute(null);

        string copied = Assert.Single(_copied);
        Assert.Contains("0.9.0+g1628935", copied);            // support needs the build first
        Assert.Contains("2026-08-05T09:30:00", copied);
        Assert.Contains("dispatcher: Unhandled dispatcher exception", copied);
        Assert.Contains("[redacted]", copied);                 // already redacted by DiagnosticLog
    }

    [Fact]
    public void Copy_last_error_says_so_when_nothing_has_failed()
    {
        MakeVm(buildInfo: "0.9.0").CopyLastErrorCommand.Execute(null);
        Assert.Contains("No errors", Assert.Single(_copied));
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
        Assert.Equal(new[] { "auto", "small", "tiny.en" }, vm.ModelChoices.Select(c => c.Name));
        Assert.Equal("Choose automatically for this PC", vm.ModelChoices[0].Subtitle);
        vm.Model = "tiny.en";
        await vm.LastSave;
        Assert.Equal("tiny.en", _settings.Current.Model);
    }

    [Fact]
    public void Model_choices_dedupe_quantized_files_to_canonical_names()
    {
        // Quantization is a file-level detail (WhisperEngineFactory picks the best file per
        // backend); the picker must offer canonical model names only, once each. Enumeration
        // now delegates to ModelPaths.AvailableModels (UX round 2026-08-02 item 4) - same rule,
        // one implementation.
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-tiny.en.bin"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-tiny.en-q8_0.bin"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-base.en-q5_1.bin"), new byte[] { 1 });
        var vm = MakeVm();
        Assert.Equal(new[] { "auto", "base.en", "tiny.en" }, vm.ModelChoices.Select(c => c.Name));
    }

    [Fact]
    public void Persisted_quantized_model_name_displays_as_its_canonical_choice()
    {
        // Re-verify finding (2026-07-13): a pre-branch/hand-edited Model="small.en-q8_0" is
        // valid at Start (Select canonicalizes) but ModelChoices holds canonical names only -
        // the raw getter value matched nothing and the ComboBox rendered blank. Still pinned
        // after the SelectedValuePath="Name" switch: the canonical getter value must match a
        // choice's Name or SelectedValue selects nothing.
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-small.en-q8_0.bin"), new byte[] { 1 });
        var vm = MakeVm(new Settings { Model = "small.en-q8_0" });
        Assert.Equal("small.en", vm.Model);
        Assert.Contains(vm.ModelChoices, c => c.Name == vm.Model);
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
        // Isolated StorageRoot for the same reason as MakeVm above: the ctor's LoadMcpAsync must
        // never reach the real %USERPROFILE%/LocalScribe.
        var real = new Services.SettingsService(path, new Settings { StorageRoot = Path.Combine(_root, "storage") });
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

    [Fact]
    public void Call_detect_surface_seeds_from_current_settings()
    {
        var vm = MakeVm();
        Assert.True(vm.CallDetectEnabled);                          // design 5.2: default ON
        Assert.Equal(new[] { "CiscoCollabHost.exe", "webex.exe", "ms-teams.exe", "Zoom.exe" },
            vm.CallDetectApps);
        Assert.Contains("advisory", vm.CallDetectNote, StringComparison.OrdinalIgnoreCase);

        var off = MakeVm(new Settings { CallDetect = new CallDetectSetting { Enabled = false } });
        Assert.False(off.CallDetectEnabled);
    }

    [Fact]
    public async Task Call_detect_toggle_commits_without_touching_the_apps()
    {
        var vm = MakeVm();
        vm.CallDetectEnabled = false;
        await vm.LastSave;
        Assert.False(_settings.Current.CallDetect.Enabled);
        Assert.Equal(4, _settings.Current.CallDetect.Apps.Count);
    }

    [Fact]
    public async Task Call_detect_add_trims_dedups_by_exe_key_and_persists()
    {
        var vm = MakeVm();
        vm.NewCallDetectApp = "  discord.exe ";
        vm.AddCallDetectAppCommand.Execute(null);
        await vm.LastSave;
        Assert.Contains("discord.exe", vm.CallDetectApps);
        Assert.Contains("discord.exe", _settings.Current.CallDetect.Apps);
        Assert.Equal("", vm.NewCallDetectApp);                      // box clears after add

        vm.NewCallDetectApp = "DISCORD";                            // same app, scanner spelling
        vm.AddCallDetectAppCommand.Execute(null);
        await vm.LastSave;
        Assert.Equal(1, vm.CallDetectApps.Count(a => CallDetectionPolicy.ExeKey(a) == "discord"));
        Assert.Equal(5, vm.CallDetectApps.Count);                   // 4 defaults + discord, once

        vm.NewCallDetectApp = "   ";
        vm.AddCallDetectAppCommand.Execute(null);
        Assert.Equal(5, vm.CallDetectApps.Count);                   // whitespace adds nothing
    }

    [Fact]
    public async Task Call_detect_remove_and_reset_persist()
    {
        var vm = MakeVm();
        vm.RemoveCallDetectAppCommand.Execute("webex.exe");
        await vm.LastSave;
        Assert.DoesNotContain("webex.exe", _settings.Current.CallDetect.Apps);
        Assert.Equal(3, _settings.Current.CallDetect.Apps.Count);

        vm.ResetCallDetectAppsCommand.Execute(null);
        await vm.LastSave;
        Assert.Equal(new CallDetectSetting().Apps, _settings.Current.CallDetect.Apps);
        Assert.Equal(4, vm.CallDetectApps.Count);
    }

    [Fact]
    public async Task Compact_console_on_start_commits_through_settings()
    {
        // Design 2026-07-18 section 6: the collapse-on-start option ships DEFAULT OFF and
        // auto-saves through the same Commit/LastSave chain as every other field.
        var vm = MakeVm();
        Assert.False(vm.CompactConsoleOnStart);

        vm.CompactConsoleOnStart = true;
        await vm.LastSave;
        Assert.True(_settings.Current.Console.CompactOnStart);
        Assert.True(vm.CompactConsoleOnStart);

        vm.CompactConsoleOnStart = false;
        await vm.LastSave;
        Assert.False(_settings.Current.Console.CompactOnStart);
    }

    [Fact]
    public void Stale_persisted_model_is_injected_as_a_not_installed_choice_and_selected()
    {
        // UX round 2026-08-02 item 3.10: weights deleted but settings.json still pins the model.
        // The raw value matched nothing -> blank ComboBox. Mic-picker pattern: inject a truthful
        // row and select it; NEVER silently rewrite the saved setting. Catalog shape: the row
        // keeps the real canonical Name (so SelectedValuePath="Name" matches with no mapping)
        // and carries the "(not installed)" mark on its subtitle line.
        var vm = MakeVm(new Settings { Model = "large-v3" });      // no ggml files on disk
        Assert.Equal("large-v3", vm.Model);
        Assert.Equal(new[] { "auto", "large-v3" }, vm.ModelChoices.Select(c => c.Name));
        Assert.Equal("(not installed)", vm.ModelChoices[1].Subtitle);
        Assert.Equal(0, _settings.SaveCount);                      // display-only on page-open
    }

    [Fact]
    public async Task Reselecting_the_not_installed_model_entry_commits_the_real_name()
    {
        var vm = MakeVm(new Settings { Model = "large-v3" });
        vm.Model = "large-v3";                                     // user re-picks the injected row
        await vm.LastSave;
        Assert.Equal("large-v3", _settings.Current.Model);         // bare name; subtitle never persisted
    }

    [Fact]
    public void Stale_persisted_language_is_injected_and_selected_by_code()
    {
        // "sv" is a valid Whisper code outside the curated 20 (hand-edited settings.json or an
        // older build) - SelectedValuePath="Code" matched nothing -> blank ComboBox.
        var vm = MakeVm(new Settings { Language = "sv" });
        Assert.Equal("sv", vm.Language);
        Assert.Contains(vm.LanguageChoices, c => c.Code == "sv" && c.Name == "sv (not installed)");
        Assert.Equal(0, _settings.SaveCount);
    }

    [Fact]
    public void Installed_model_and_curated_language_get_no_injected_entries()
    {
        File.WriteAllBytes(Path.Combine(_root, "models", "ggml-small.en.bin"), new byte[] { 1 });
        var vm = MakeVm(new Settings { Model = "small.en", Language = "en" });
        Assert.Equal(new[] { "auto", "small.en" }, vm.ModelChoices.Select(c => c.Name));
        Assert.DoesNotContain(vm.ModelChoices, c => c.Subtitle == "(not installed)");
        Assert.DoesNotContain(vm.LanguageChoices, c => c.Name.Contains("(not installed)"));
    }
}
