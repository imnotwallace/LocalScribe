using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;

namespace LocalScribe.App.ViewModels;

/// <summary>One transcription-language option: the Whisper code stored in settings (Code) and a
/// friendly display name. "auto" is auto-detect (LanguageResolver probes then locks).</summary>
public sealed record LanguageChoice(string Code, string Name);

/// <summary>One microphone option in the Settings pin picker (design section 4). Id null is the
/// "follow the Windows Communications default" choice; a device carries its WASAPI Id + friendly
/// Name; a saved-but-absent pin surfaces as a "(not connected)" Label kept selected (the pin is
/// never silently dropped - capture's own fall-back marker handles the real absence at Start).</summary>
public sealed record MicChoice(string? Id, string Name, string Label);

/// <summary>Settings page VM (design 6.1/6.2). WPF-free. Every committed change goes through
/// ISettingsService.SaveAsync (Current with { ... }) - auto-save on field commit, no Save
/// button. Deliberately NOT exposed (design 6.1): recordingIndicator (the tray consent
/// indicator is immovable), hotkeys (dropped, design 1.1), autoDetect (disabled seam) - a
/// reflection test pins their absence. The Mic group is a real picker over
/// ICaptureDeviceEnumerator (design section 4): pinning a device or following the Windows
/// Communications default both auto-save through the same Commit/CommitAsync chain.</summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly MaintenanceService _maintenance;
    private readonly ILaunchAtLogin _launchAtLogin;
    private readonly Func<string?> _pickFolder;
    private readonly Action<string> _openFolder;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private readonly ICaptureDeviceEnumerator _deviceEnumerator;
    private readonly string _initialRoot;
    private MicChoice _selectedMic;

    [ObservableProperty] private bool _restartRequired;
    [ObservableProperty] private bool _isRegenerating;
    [ObservableProperty] private int _regenerateProgress;

    /// <summary>The last SaveAsync round-trip. Production fire-and-forgets (failures surface
    /// via IUiErrorReporter); tests await it so no commit is in flight when they assert.</summary>
    public Task LastSave { get; private set; } = Task.CompletedTask;

    /// <summary>Global custom-vocabulary editor (Stage 6.2). Auto-saves each add/remove straight
    /// into settings.json via the same Commit/LastSave chain as every other field - no Save
    /// button. Effective vocabulary at record/render time is this UNION each session's matters'
    /// vocab.</summary>
    public VocabularyEditorViewModel Vocabulary { get; }

    public SettingsPageViewModel(ISettingsService settings, MaintenanceService maintenance,
        ILaunchAtLogin launchAtLogin, Func<string?> pickFolder, Action<string> openFolder,
        IUiErrorReporter errors, Action<Action> dispatch, ICaptureDeviceEnumerator deviceEnumerator,
        string? modelsRoot = null)
    {
        (_settings, _maintenance, _launchAtLogin, _pickFolder, _openFolder, _errors, _dispatch)
            = (settings, maintenance, launchAtLogin, pickFolder, openFolder, errors, dispatch);
        _deviceEnumerator = deviceEnumerator;
        _initialRoot = settings.Current.StorageRoot;
        ModelChoices = BuildModelChoices(modelsRoot ?? ModelPaths.ModelsRoot);
        MicChoices = BuildMicChoices(out _selectedMic);         // must precede any SelectedMic read

        PickStorageRootCommand = new RelayCommand(PickStorageRoot);
        OpenStorageRootCommand = new RelayCommand(
            () => _openFolder(new StoragePaths(_settings.Current.StorageRoot).Root));
        RegenerateAllProjectionsCommand = new AsyncRelayCommand(RegenerateAllAsync, () => !IsRegenerating);

        Vocabulary = new VocabularyEditorViewModel(
            (v, _) => { Commit(s => s with { Vocabulary = v }); return LastSave; }, errors);
        Vocabulary.Load(_settings.Current.Vocabulary);
    }

    // ---------- Storage ----------
    public string StorageRoot => _settings.Current.StorageRoot;
    public IRelayCommand PickStorageRootCommand { get; }
    public IRelayCommand OpenStorageRootCommand { get; }
    public IAsyncRelayCommand RegenerateAllProjectionsCommand { get; }

    public string RestartRequiredNote { get; } =
        "The storage root change takes effect after a restart. No data is migrated: existing "
        + "sessions stay in the old root and will drop out of the list.";

    public string? SyncProviderWarning
        => SyncProviderCheck.ResolvesUnderSyncProvider(
               new StoragePaths(_settings.Current.StorageRoot).Root, out string? provider)
           ? $"This folder is under {provider}: audio and transcripts would sync off this machine."
           : null;

    private void PickStorageRoot()
    {
        string? picked = _pickFolder();
        if (string.IsNullOrWhiteSpace(picked)) return;
        // Picking always stores the LITERAL path (design 6.1); a %VAR% form survives only
        // while the stored value is left untouched.
        Commit(s => s with { StorageRoot = picked });
        RestartRequired = !string.Equals(picked, _initialRoot, StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(StorageRoot));
        OnPropertyChanged(nameof(SyncProviderWarning));
    }

    private async Task RegenerateAllAsync()
    {
        IsRegenerating = true;
        RegenerateProgress = 0;
        try
        {
            await _maintenance.RegenerateAllAsync(
                new DispatchedProgress(_dispatch, n => RegenerateProgress = n), CancellationToken.None);
        }
        catch (Exception ex) { _errors.Report("Regenerate all projections", ex); }
        finally { IsRegenerating = false; RegenerateAllProjectionsCommand.NotifyCanExecuteChanged(); }
    }

    /// <summary>IProgress that marshals via the injected dispatch (never Progress&lt;T&gt;,
    /// which captures SynchronizationContext - VMs must stay WPF-free and test-deterministic).</summary>
    private sealed class DispatchedProgress(Action<Action> dispatch, Action<int> apply) : IProgress<int>
    {
        public void Report(int value) => dispatch(() => apply(value));
    }

    // ---------- Recording (design 6.2: applies at the NEXT Start) ----------
    public string RecordingApplyNote { get; } = "Recording settings apply at the next Start.";

    public IReadOnlyList<AudioFormat> AudioFormatChoices { get; } = [AudioFormat.Flac, AudioFormat.Wav];
    public AudioFormat AudioFormat
    {
        get => _settings.Current.AudioFormat;
        set { Commit(s => s with { AudioFormat = value }); OnPropertyChanged(); }
    }

    public IReadOnlyList<RemoteMode> RemoteModeChoices { get; } =
        [RemoteMode.Auto, RemoteMode.PerProcess, RemoteMode.SystemMix];
    public RemoteMode RemoteMode
    {
        get => _settings.Current.Remote.Mode;
        set
        {
            Commit(s => s with { Remote = s.Remote with { Mode = value } });
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPerProcess));
        }
    }

    /// <summary>True when remote capture is pinned per-process - gates the per-app target row.</summary>
    public bool IsPerProcess => RemoteMode == RemoteMode.PerProcess;

    public IReadOnlyList<string> RemoteAppSuggestions { get; } = RemoteCapturePlanner.SuggestedPerProcessApps;

    public string RemoteAppNote { get; } =
        "Used when Remote capture is perProcess: the process name to record (CiscoCollabHost is "
        + "Webex's audio process). You can also change it for a single recording in the Record console.";

    public string RemoteApp
    {
        get => _settings.Current.Remote.App ?? "";
        set
        {
            Commit(s => s with
            { Remote = s.Remote with { App = string.IsNullOrWhiteSpace(value) ? null : value.Trim() } });
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<MicChoice> MicChoices { get; }

    /// <summary>The selected mic. Setting a device pins it ({Pinned, Id, Name}); setting the
    /// follow-default choice clears the pin ({FollowDefault}). Auto-saves via the shared Commit
    /// chain (design section 4). A synthetic "(not connected)" choice for an absent saved pin is
    /// selectable-but-inert here: re-selecting it re-commits the same pin (harmless).</summary>
    public MicChoice SelectedMic
    {
        get => _selectedMic;
        set
        {
            if (value is null || value == _selectedMic) return;
            _selectedMic = value;
            Commit(s => s with
            {
                Mic = value.Id is null
                    ? new MicSetting { Mode = MicMode.FollowDefault }
                    : new MicSetting { Mode = MicMode.Pinned, Id = value.Id, Name = value.Name },
            });
            OnPropertyChanged();
        }
    }

    /// <summary>Build the picker: a leading follow-default choice, then one per live device. If the
    /// saved pin's device is absent, prepend a "(not connected)" choice and select it (never
    /// silently dropped). Selects the matching device / the follow choice otherwise.</summary>
    private IReadOnlyList<MicChoice> BuildMicChoices(out MicChoice selected)
    {
        var follow = new MicChoice(null, "", "Windows Communications default (follow)");
        var choices = new List<MicChoice> { follow };
        foreach (var d in _deviceEnumerator.ListInputDevices())
            choices.Add(new MicChoice(d.Id, d.Name, d.Name));

        var mic = _settings.Current.Mic;
        if (mic.Mode == MicMode.Pinned && !string.IsNullOrEmpty(mic.Id))
        {
            var match = choices.FirstOrDefault(c => c.Id == mic.Id);
            if (match is not null) { selected = match; return choices; }
            // Pinned device not present: prepend a "(not connected)" choice, keep it selected.
            var synthetic = new MicChoice(mic.Id, mic.Name ?? "", $"{mic.Name ?? "Pinned device"} (not connected)");
            choices.Insert(1, synthetic);
            selected = synthetic;
            return choices;
        }
        selected = follow;
        return choices;
    }

    public string AudioRetentionDisplay
    {
        get
        {
            string v = _settings.Current.AudioRetention;
            return v is "keep" or "forever"
                ? "Keep everything (audio is never auto-deleted)"
                : "Migrated policy: " + v + " (retention editing is not exposed)";
        }
    }

    // ---------- Transcription ----------
    public IReadOnlyList<string> ModelChoices { get; }
    public string Model
    {
        get => _settings.Current.Model;
        set { Commit(s => s with { Model = value }); OnPropertyChanged(); }
    }

    public IReadOnlyList<Backend> BackendChoices { get; } =
        [Backend.Auto, Backend.Cuda, Backend.Vulkan, Backend.Cpu];
    public Backend Backend
    {
        get => _settings.Current.Backend;
        set { Commit(s => s with { Backend = value }); OnPropertyChanged(); }
    }

    /// <summary>Auto-detect + common Whisper languages (a curated subset of the ~99 Whisper
    /// supports). "auto" stays the default (LanguageResolver auto-detects then locks); a fixed
    /// pick locks that language immediately.</summary>
    public IReadOnlyList<LanguageChoice> LanguageChoices { get; } =
    [
        new("auto", "Auto-detect"),
        new("en", "English"),
        new("es", "Spanish"),
        new("zh", "Chinese"),
        new("hi", "Hindi"),
        new("ar", "Arabic"),
        new("fr", "French"),
        new("de", "German"),
        new("pt", "Portuguese"),
        new("ru", "Russian"),
        new("it", "Italian"),
        new("ja", "Japanese"),
        new("ko", "Korean"),
        new("vi", "Vietnamese"),
        new("nl", "Dutch"),
        new("pl", "Polish"),
        new("tr", "Turkish"),
        new("uk", "Ukrainian"),
        new("id", "Indonesian"),
        new("th", "Thai"),
    ];
    public string Language
    {
        get => _settings.Current.Language;
        set
        {
            Commit(s => s with
            { Language = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim() });
            OnPropertyChanged();
        }
    }

    /// <summary>"auto" + only the models actually on disk (design 6.1: an absent model cannot
    /// be selected; model-download UX is Stage 7). Engine files are ggml-{name}.bin, with
    /// quantized variants (ggml-{name}-q8_0.bin) collapsing to the canonical name -
    /// WhisperEngineFactory picks the best file per backend (ModelFileResolver).</summary>
    private static IReadOnlyList<string> BuildModelChoices(string modelsRoot)
    {
        var choices = new List<string> { "auto" };
        try
        {
            if (Directory.Exists(modelsRoot))
                choices.AddRange(Directory.EnumerateFiles(modelsRoot, "ggml-*.bin")
                    .Select(f => Path.GetFileNameWithoutExtension(f)["ggml-".Length..])
                    .Select(ModelFileResolver.CanonicalName)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(n => n, StringComparer.Ordinal));
        }
        catch (IOException) { }              // unreadable models dir -> "auto" only
        return choices;
    }

    // ---------- Identity (snapshotted into FUTURE sessions only - SessionBootstrap) ----------
    public string IdentityNote { get; } =
        "Your name and role are snapshotted into future sessions when they start; existing "
        + "sessions are never rewritten.";
    public string SelfName
    {
        get => _settings.Current.Self.Name;
        set
        {
            Commit(s => s with { Self = s.Self with { Name = value } });
            OnPropertyChanged();
        }
    }
    public string SelfRole
    {
        get => _settings.Current.Self.Role ?? "";
        set
        {
            Commit(s => s with
            { Self = s.Self with { Role = string.IsNullOrWhiteSpace(value) ? null : value } });
            OnPropertyChanged();
        }
    }

    // ---------- Privacy ----------
    public bool ExcludeWindowsFromCapture
    {
        get => _settings.Current.Privacy.ExcludeWindowsFromCapture;
        set
        {
            Commit(s => s with
            { Privacy = s.Privacy with { ExcludeWindowsFromCapture = value } });
            OnPropertyChanged();
        }
    }

    public bool OverlayEnabled
    {
        get => _settings.Current.Overlay.Enabled;
        set
        {
            Commit(s => s with { Overlay = s.Overlay with { Enabled = value } });
            OnPropertyChanged();
        }
    }
    public bool OverlayShowSessionName
    {
        get => _settings.Current.Overlay.ShowSessionName;
        set
        {
            Commit(s => s with { Overlay = s.Overlay with { ShowSessionName = value } });
            OnPropertyChanged();
        }
    }
    public bool OverlayShowLevelMeter
    {
        get => _settings.Current.Overlay.ShowLevelMeter;
        set
        {
            Commit(s => s with { Overlay = s.Overlay with { ShowLevelMeter = value } });
            OnPropertyChanged();
        }
    }
    public bool OverlayExcludeFromCapture
    {
        get => _settings.Current.Overlay.ExcludeFromCapture;
        set
        {
            Commit(s => s with { Overlay = s.Overlay with { ExcludeFromCapture = value } });
            OnPropertyChanged();
        }
    }

    public string LoggingRedactionNote { get; } =
        "Transcript text is redacted from logs by default (logging arrives in Stage 7).";

    // ---------- App ----------
    public bool LaunchAtLogin
    {
        get => _settings.Current.LaunchAtLogin;
        set
        {
            try { _launchAtLogin.SetEnabled(value); }
            catch (Exception ex) { _errors.Report("Launch at login", ex); }
            Commit(s => s with { LaunchAtLogin = value });
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> TimestampChoices { get; } = ["relative", "wallclock"];
    public string Timestamps
    {
        get => _settings.Current.Timestamps;
        set { Commit(s => s with { Timestamps = value }); OnPropertyChanged(); }
    }

    private void Commit(Func<Settings, Settings> mutate) => LastSave = CommitAsync(mutate);

    private async Task CommitAsync(Func<Settings, Settings> mutate)
    {
        // Chain onto the previous save so each update is built from the SWAPPED Current, never a
        // stale base: two quick commits to DIFFERENT fields must both survive (F3, no lost update).
        // SettingsService serializes the write+swap; awaiting the prior commit closes the
        // read-modify-write gap that would otherwise drop one field.
        var prior = LastSave;
        if (!prior.IsCompleted) { try { await prior; } catch { /* prior reported its own error */ } }
        try { await _settings.SaveAsync(mutate(_settings.Current), CancellationToken.None); }
        catch (Exception ex) { _errors.Report("Saving settings", ex); }
    }
}
