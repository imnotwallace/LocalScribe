using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Live;
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Model;
using LocalScribe.Core.People;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;

namespace LocalScribe.App.ViewModels;

/// <summary>One transcription-language option: the Whisper code stored in settings (Code) and a
/// friendly display name. "auto" is auto-detect (LanguageResolver probes then locks).</summary>
public sealed record LanguageChoice(string Code, string Name)
{
    /// <summary>Auto-detect + common Whisper languages (a curated subset of the ~99 Whisper
    /// supports). Shared by the Settings page and the Re-transcribe dialog (design 2026-07-13
    /// section 3.4) so the two pickers can never drift.</summary>
    public static IReadOnlyList<LanguageChoice> All { get; } =
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
}

/// <summary>One microphone option in the Settings pin picker (design section 4). Id null is the
/// "follow the Windows Communications default" choice; a device carries its WASAPI Id + friendly
/// Name; a saved-but-absent pin surfaces as a "(not connected)" Label kept selected (the pin is
/// never silently dropped - capture's own fall-back marker handles the real absence at Start).</summary>
public sealed record MicChoice(string? Id, string Name, string Label);

/// <summary>One row of the Settings Voiceprints list (voiceprint design 2026-07-25): a saved
/// Person plus a plain-language read of what voice data is stored for them. An immutable
/// snapshot of the Person it was built from - every mutating command re-reads people.json and
/// rebuilds the whole list, so a row can never show a count that disk no longer agrees with.</summary>
public sealed class PersonRowViewModel
{
    public PersonRowViewModel(Person person)
    {
        Id = person.Id;
        Name = person.Name;
        EnrollmentCount = person.Voiceprint.Count;
        // OrderBy is stable, so equal timestamps keep people.json's own (append) order - the same
        // order PeopleRegistryOps.Enroll's FIFO eviction treats as oldest-first.
        var byAge = person.Voiceprint.OrderBy(e => e.EnrolledAtUtc).ToList();
        OldestEnrollmentId = byAge.Count > 0 ? byAge[0].Id : null;
        // "Stale" = saved by a different embedding model. VoiceprintMatcher only ever compares
        // same-Method vectors, so such a person can never be suggested until they re-enroll.
        NeedsReenroll = EnrollmentCount > 0
                        && !person.Voiceprint.Any(e => e.Method == EmbeddingMethods.CampPlus);
        if (byAge.Count == 0)
        {
            EnrollmentSummary = "";
        }
        else
        {
            var latest = byAge[^1];
            EnrollmentSummary =
                $"{EnrollmentCount} voiceprint{(EnrollmentCount == 1 ? "" : "s")} - latest "
                + $"{latest.EnrolledAtUtc:yyyy-MM-dd} from session {latest.SourceSessionId}";
        }
    }

    public string Id { get; }
    public string Name { get; }
    public int EnrollmentCount { get; }
    public bool HasEnrollments => EnrollmentCount > 0;

    /// <summary>Empty when nothing is stored for this person (the row then reads as name-only).</summary>
    public string EnrollmentSummary { get; }

    /// <summary>Every stored enrollment uses a superseded embedding model, so none of them can be
    /// matched against - the row surfaces a re-enroll hint.</summary>
    public bool NeedsReenroll { get; }

    /// <summary>Target of the delete-oldest action (per-enrollment granularity in the list UI is a
    /// follow-up; PeopleRegistryOps.RemoveEnrollment already takes any id). Null when none.</summary>
    public string? OldestEnrollmentId { get; }
}

/// <summary>One row of the Settings "MCP Access" matter allowlist (design 2026-07-26): the matter's
/// display label (Name + optional Reference, matching MattersPageViewModel's labelling) and whether
/// it is currently ticked. Ticking/unticking calls back into the owning VM's save (a save-on-commit
/// property just like every other Settings field), never buffering an unsaved edit.</summary>
public sealed partial class McpMatterToggle : ObservableObject
{
    private readonly Action _onChanged;

    public McpMatterToggle(string id, string label, bool isAllowed, Action onChanged)
    {
        Id = id;
        Label = label;
        _isAllowed = isAllowed;
        _onChanged = onChanged;
    }

    public string Id { get; }
    public string Label { get; }

    [ObservableProperty] private bool _isAllowed;

    partial void OnIsAllowedChanged(bool value) => _onChanged();
}

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
    private readonly AssistantManifestCache? _assistantModels;
    /// <summary>Resolves the deployed helper exe (null = not deployed). Injected for tests;
    /// production uses AssistantHelperLocator.FindExe - same seam as AssistantTabViewModel
    /// (design 2026-07-23 section 4).</summary>
    private readonly Func<string?> _helperProbe;
    private readonly string _initialRoot;
    private MicChoice _selectedMic;
    // --- Voiceprints (design 2026-07-25 section "Deletion - three levels"). All optional: a
    // composition that does not pass them (the non-voiceprint unit tests) simply gets an empty,
    // inert section rather than a half-wired one that could appear to delete and not.
    private readonly StoragePaths? _paths;
    private readonly PeopleStore? _people;
    private readonly VoiceprintEnrollmentService? _enrollment;
    private readonly IEmbeddingEngine? _embeddingEngine;
    private readonly Func<string, string>? _resolveModel;
    private readonly Func<string, bool>? _confirm;
    /// <summary>One-engine-at-a-time probe (design 2026-07-28 adjacent fix 3): non-null = a
    /// user-facing reason another heavy engine owns the machine right now. Null (or a null probe)
    /// means run. Probe-and-refuse, never a latch - the seam is deliberately cooperative
    /// (SessionController.cs:168-170, pinned by SessionControllerTests.cs:544-566).</summary>
    private readonly Func<string?>? _engineBusy;

    // --- MCP Access (design 2026-07-26): the ONLY writer of mcp/consent.json (spec's dark-by-
    // default consent surface). Default dark - a fresh consent.json-less install shows the toggle
    // OFF and every matter unticked. Storage root is re-resolved from _settings.Current on every
    // load/save (never cached), so a storage-root change is picked up without extra wiring.
    private readonly Func<string, bool>? _confirmMcpEnable;
    private readonly Action<string> _copyMcpSnippetToClipboard;

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
        string? modelsRoot = null, AssistantManifestCache? assistantModels = null,
        Func<string?>? assistantHelperProbe = null,
        StoragePaths? paths = null, PeopleStore? people = null,
        VoiceprintEnrollmentService? enrollment = null, IEmbeddingEngine? embeddingEngine = null,
        Func<string, string>? resolveModel = null, Func<string, bool>? confirm = null,
        Func<string, bool>? confirmMcpEnable = null, Action<string>? copyMcpSnippetToClipboard = null,
        Func<string?>? engineBusy = null)
    {
        (_settings, _maintenance, _launchAtLogin, _pickFolder, _openFolder, _errors, _dispatch)
            = (settings, maintenance, launchAtLogin, pickFolder, openFolder, errors, dispatch);
        _deviceEnumerator = deviceEnumerator;
        _assistantModels = assistantModels;
        (_paths, _people, _enrollment, _embeddingEngine, _resolveModel, _confirm)
            = (paths, people, enrollment, embeddingEngine, resolveModel, confirm);
        _engineBusy = engineBusy;
        _confirmMcpEnable = confirmMcpEnable;
        _copyMcpSnippetToClipboard = copyMcpSnippetToClipboard ?? (_ => { });
        _helperProbe = assistantHelperProbe ?? AssistantHelperLocator.FindExe;
        _initialRoot = settings.Current.StorageRoot;
        ModelChoices = BuildModelChoices(modelsRoot ?? ModelPaths.ModelsRoot);
        string storedModel = ModelFileResolver.CanonicalName(settings.Current.Model);
        if (!ModelChoices.Any(c => c.Name == storedModel))
        {
            // Stale pin (weights deleted / different root): inject the saved model as a truthful
            // "(not installed)" row at index 1 (after "auto"), mirroring the mic picker. The row
            // keeps the REAL canonical name so SelectedValuePath="Name" selects it and the
            // existing setter commits it verbatim - nothing is rewritten on page-open (item 3.10).
            var withMissing = ModelChoices.ToList();
            withMissing.Insert(1, new WhisperModelInfo(storedModel, "(not installed)",
                int.MaxValue, storedModel.EndsWith(".en", StringComparison.Ordinal)));
            ModelChoices = withMissing;
        }
        LanguageChoices = BuildLanguageChoices(settings.Current.Language);
        MicChoices = BuildMicChoices(out _selectedMic);         // must precede any SelectedMic read

        PickStorageRootCommand = new RelayCommand(PickStorageRoot);
        OpenStorageRootCommand = new RelayCommand(
            () => _openFolder(new StoragePaths(_settings.Current.StorageRoot).Root));
        RegenerateAllProjectionsCommand = new AsyncRelayCommand(RegenerateAllAsync, () => !IsRegenerating);

        Vocabulary = new VocabularyEditorViewModel(
            (v, _) => { Commit(s => s with { Vocabulary = v }); return LastSave; }, errors);
        Vocabulary.Load(_settings.Current.Vocabulary);

        // Call detection (design 2026-07-18 section 5.2): seed the editable allowlist and wire
        // the editor commands. Every mutation auto-saves through the same Commit chain.
        CallDetectApps = new ObservableCollection<string>(_settings.Current.CallDetect.Apps);
        AddCallDetectAppCommand = new RelayCommand(AddCallDetectApp);
        RemoveCallDetectAppCommand = new RelayCommand<string>(RemoveCallDetectApp);
        ResetCallDetectAppsCommand = new RelayCommand(ResetCallDetectApps);

        RefreshAssistantHelperNote();

        CopyMcpSnippetCommand = new RelayCommand(() => _copyMcpSnippetToClipboard(McpConfigSnippet));
        OpenMcpAuditFolderCommand = new RelayCommand(() =>
        {
            var mcpPaths = new StoragePaths(_settings.Current.StorageRoot);
            Directory.CreateDirectory(mcpPaths.McpAuditDir);
            _openFolder(mcpPaths.McpAuditDir);
        });

        AssistantModelsLoad = LoadAssistantModelsAsync();
        PeopleLoad = ReloadPeopleAsync();
        McpLoad = LoadMcpAsync();
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
    public IReadOnlyList<WhisperModelInfo> ModelChoices { get; }
    public string Model
    {
        // Canonicalized for display: a persisted/hand-edited quantized name ("small.en-q8_0",
        // valid at Start - Select canonicalizes it too) must select its canonical entry in
        // ModelChoices instead of rendering a blank ComboBox (re-verify finding 2026-07-13).
        get => ModelFileResolver.CanonicalName(_settings.Current.Model);
        set { Commit(s => s with { Model = value }); OnPropertyChanged(); }
    }

    public IReadOnlyList<Backend> BackendChoices { get; } =
        [Backend.Auto, Backend.Cuda, Backend.Vulkan, Backend.Cpu];
    public Backend Backend
    {
        get => _settings.Current.Backend;
        set { Commit(s => s with { Backend = value }); OnPropertyChanged(); }
    }

    /// <summary>See LanguageChoice.All - shared with the Re-transcribe dialog. Instance-built:
    /// a saved code outside the curated list gets an injected "(not installed)" entry
    /// (item 3.10) so the ComboBox (SelectedValuePath=Code) still selects it truthfully.</summary>
    public IReadOnlyList<LanguageChoice> LanguageChoices { get; }
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
    /// be selected; model-download UX is Stage 7). Enumeration delegates to
    /// ModelPaths.AvailableModels - the one glob+canonicalize rule every surface uses (quantized
    /// ggml variants collapse; WhisperEngineFactory picks the best file per backend) - then
    /// projects through the shared catalog for the two-line picker rows (UX round 2026-08-02
    /// item 4; the old inline scan was the exact drift LanguageChoice's doc comment warns about).</summary>
    private static IReadOnlyList<WhisperModelInfo> BuildModelChoices(string modelsRoot)
    {
        var choices = new List<WhisperModelInfo> { WhisperModelCatalog.Describe("auto") };
        choices.AddRange(WhisperModelCatalog.DescribeAll(ModelPaths.AvailableModels(modelsRoot)));
        return choices;
    }

    /// <summary>LanguageChoice.All plus, when settings.json carries a code outside the curated
    /// list (hand-edited, or an older build's value), an injected "{code} (not installed)" entry
    /// at index 1 - selected by Code, so no setter mapping is needed and nothing is rewritten.</summary>
    private static IReadOnlyList<LanguageChoice> BuildLanguageChoices(string saved)
    {
        if (LanguageChoice.All.Any(c => c.Code == saved)) return LanguageChoice.All;
        var choices = LanguageChoice.All.ToList();
        choices.Insert(1, new LanguageChoice(saved, saved + " (not installed)"));
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

    /// <summary>Design 2026-07-18 section 6: collapse the Record console to the compact
    /// always-on-top pill when recording starts. DEFAULT OFF (opt-in). Read live by
    /// CompactConsoleViewModel at each Start, so no restart/apply note is needed.</summary>
    public bool CompactConsoleOnStart
    {
        get => _settings.Current.Console.CompactOnStart;
        set
        {
            Commit(s => s with { Console = s.Console with { CompactOnStart = value } });
            OnPropertyChanged();
        }
    }

    /// <summary>Design 2026-08-04 section 6: Save-As default-name template for the three textual
    /// export formats. Set-once preference, so it lives here rather than in the export dialog -
    /// the Save-As default name is already the live preview.</summary>
    public string ExportFilenameTemplate
    {
        get => _settings.Current.Export.FilenameTemplate;
        set
        {
            Commit(s => s with { Export = s.Export with { FilenameTemplate = value } });
            OnPropertyChanged();
        }
    }

    public string ExportTemplateTokens { get; } =
        "Tokens: {title} {date} {time} {matter} {version} {id}. "
        + "An unknown token is left as typed. The .zip keeps its session-id name.";

    public string LoggingRedactionNote { get; } =
        "Transcript text is redacted from logs by default (logging arrives in Stage 7).";

    // ---------- Call detection (design 2026-07-18 section 5.2: ADVISORY-ONLY, locked) ----------
    public string CallDetectNote { get; } =
        "When a listed app starts using the microphone, LocalScribe shows an offer toast. "
        + "Detection is advisory-only: it never starts or stops a recording by itself, and "
        + "ignoring the offer does nothing.";

    public bool CallDetectEnabled
    {
        get => _settings.Current.CallDetect.Enabled;
        set
        {
            Commit(s => s with { CallDetect = s.CallDetect with { Enabled = value } });
            OnPropertyChanged();
        }
    }

    /// <summary>The editable exe allowlist ("webex.exe" spelling; matching is case-insensitive
    /// and extension-tolerant via CallDetectionPolicy.ExeKey). Seeded from settings in the ctor;
    /// each add/remove/reset commits the whole list.</summary>
    public ObservableCollection<string> CallDetectApps { get; }

    [ObservableProperty] private string _newCallDetectApp = "";

    public IRelayCommand AddCallDetectAppCommand { get; }
    public IRelayCommand<string> RemoveCallDetectAppCommand { get; }
    public IRelayCommand ResetCallDetectAppsCommand { get; }

    private void AddCallDetectApp()
    {
        string exe = NewCallDetectApp.Trim();
        if (exe.Length == 0) return;
        // Dedup with the policy's own identity: "WEBEX" and "webex.exe" are one entry, so the
        // list can never hold two spellings that the matcher treats as the same app.
        if (CallDetectApps.Any(a => CallDetectionPolicy.ExeKey(a) == CallDetectionPolicy.ExeKey(exe)))
        {
            NewCallDetectApp = "";
            return;
        }
        CallDetectApps.Add(exe);
        CommitCallDetectApps();
        NewCallDetectApp = "";
    }

    private void RemoveCallDetectApp(string? exe)
    {
        if (exe is null || !CallDetectApps.Remove(exe)) return;
        CommitCallDetectApps();
    }

    private void ResetCallDetectApps()
    {
        CallDetectApps.Clear();
        foreach (string a in new CallDetectSetting().Apps)   // single-sourced defaults (Task 1)
            CallDetectApps.Add(a);
        CommitCallDetectApps();
    }

    private void CommitCallDetectApps()
        => Commit(s => s with { CallDetect = s.CallDetect with { Apps = CallDetectApps.ToList() } });

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

    // --- Assistant (design 2026-07-18 sections 7.2/7.6) ---------------------------------

    /// <summary>Fetch instructions shown when no model is installed (design 7.6). Public
    /// const: tests and the note binding share one source of truth.</summary>
    public const string NoAssistantModelsNote =
        "No assistant model is installed. Run: pwsh tools/fetch-models.ps1 -Assistant "
        + "(downloads Qwen3-4B-Instruct-2507 q4_K_M, about 2.5 GB, SHA-verified, local-only). "
        + "Assistant features stay off until a model is present.";

    /// <summary>Awaitable manifest-load (the LastSave precedent - tests await it).</summary>
    public Task AssistantModelsLoad { get; private set; } = Task.CompletedTask;

    public ObservableCollection<string> AssistantModelChoices { get; } = [];

    [ObservableProperty] private string _assistantModelsNote = "";
    [ObservableProperty] private bool _hasAssistantModels;

    /// <summary>Helper-present/absent, reported SEPARATELY from model-installed (design
    /// 2026-07-23 section 7) - a missing helper was previously invisible until first use.</summary>
    [ObservableProperty] private string _assistantHelperNote = "";

    /// <summary>Re-probes the helper exe (a cheap File.Exists chain - NOT the expensive
    /// SHA-256 model-manifest hash, which legitimately stays cached at startup) and updates
    /// AssistantHelperNote. The Assistant tab and the assistant chat both re-probe on every
    /// use; freezing this note at construction made Settings the one surface that could lie
    /// about a helper deployed after startup (Task 5 review finding). SettingsPage.Loaded
    /// calls this on every re-navigation into Settings - the same "page navigation refresh"
    /// pattern Sessions/Matters/Search use (design 3.1) - so redeploying the helper shows up
    /// the next time the user opens Settings, no app restart required.</summary>
    public void RefreshAssistantHelperNote()
        => AssistantHelperNote = _helperProbe() is string helperPath
            ? $"Assistant helper: {helperPath}"
            : AssistantHelperLocator.MissingMessage;

    /// <summary>Master toggle (design 7.6). Auto-saved via the standard Commit pattern.</summary>
    public bool AssistantEnabled
    {
        get => _settings.Current.Assistant.Enabled;
        set
        {
            Commit(s => s with { Assistant = s.Assistant with { Enabled = value } });
            OnPropertyChanged();
        }
    }

    /// <summary>Model picker over manifest canonical names. Storing the locked default
    /// stores null (the "no explicit pick" sentinel), so a future default change follows.
    /// Display fallback (UX round 2026-08-02 item 3.1): mirrors Core's resolution chain
    /// (SummarizationService.cs:52-59 + AssistantModels.cs:87-88): explicit pick match ->
    /// DefaultCanonicalName name-match over installed -> first installed. When the stored/default
    /// name has no match, display falls through: try DefaultCanonicalName if installed, else
    /// the first installed chat model. So the picker never disagrees with what actually runs.
    /// Display-coerce ONLY: nothing is committed by reading this.</summary>
    public string AssistantModel
    {
        get
        {
            string stored = _settings.Current.Assistant.Model
                            ?? AssistantModelManifest.DefaultCanonicalName;
            if (AssistantModelChoices.Count == 0)
                return stored;
            if (AssistantModelChoices.Contains(stored))
                return stored;
            // Stored not found; try DefaultCanonicalName (matches Core's second tier).
            if (AssistantModelChoices.Contains(AssistantModelManifest.DefaultCanonicalName))
                return AssistantModelManifest.DefaultCanonicalName;
            // Default also not found; fall back to first installed (matches Core's third tier).
            return AssistantModelChoices[0];
        }
        set
        {
            Commit(s => s with
            {
                Assistant = s.Assistant with
                {
                    Model = string.IsNullOrWhiteSpace(value)
                            || value == AssistantModelManifest.DefaultCanonicalName ? null : value,
                },
            });
            OnPropertyChanged();
        }
    }

    /// <summary>Loads installed models off the UI thread (hash verify is seconds on a
    /// multi-GB file) and projects them onto the picker. No cache injected (tests of the
    /// non-assistant surface) -> instructions note only.</summary>
    private async Task LoadAssistantModelsAsync()
    {
        if (_assistantModels is null)
        {
            _dispatch(() => AssistantModelsNote = NoAssistantModelsNote);
            return;
        }
        try
        {
            var manifest = await Task.Run(() => _assistantModels.GetAsync(CancellationToken.None));
            _dispatch(() =>
            {
                // Chat-only (design 2026-07-25): Installed now mixes chat and embedding roles - the
                // embedding model is never a valid chat pick, so it must never reach this picker.
                var chatModels = manifest.Installed.Where(m => m.Role == "chat").ToList();
                AssistantModelChoices.Clear();
                foreach (var m in chatModels) AssistantModelChoices.Add(m.CanonicalName);
                HasAssistantModels = chatModels.Count > 0;
                AssistantModelsNote = chatModels.Count > 0
                    ? string.Join(" ", manifest.Notes)   // surfaced degradation (excluded entries)
                    : NoAssistantModelsNote;
                OnPropertyChanged(nameof(AssistantModel));
            });
        }
        catch (Exception ex)
        {
            // Task-4 review note: AssistantManifestCache caches a FAULTED load Task until
            // Invalidate() - a manifest-load failure (corrupt manifest.json, unreadable
            // models dir, ...) must still degrade to the disabled-with-explainer surface
            // (design 7.6/7.7), never a blank note or a crash. HasAssistantModels stays at
            // its default false, so the picker is already disabled; this adds the note.
            _dispatch(() => AssistantModelsNote = NoAssistantModelsNote);
            _errors.Report("Loading assistant models", ex);
        }
    }

    // --- Voiceprints (voiceprint design 2026-07-25) -------------------------------------
    // This is the screen that OWNS voiceprint deletion. The user's requirement is that saved voice
    // data be retrospectively deletable, reliably and unambiguously - so every command here
    // re-reads people.json after mutating (no stale counts on screen), never throws into the UI,
    // and the purge reports a PARTIAL failure as a partial failure, never as success.

    /// <summary>The embedding model the backfill scan runs. Now a single source of truth with the
    /// diarise path (design 2026-07-28 task 3): both resolve DiarisationModels.Embedding, so an
    /// enrollment made here can never be stamped with a Method that fails to match one made in
    /// SplitSpeakersViewModel's confirm path.</summary>
    private const string EmbeddingModelFile = LocalScribe.Core.Diarisation.DiarisationModels.Embedding;

    /// <summary>MaintenanceService's sentinel failure id for the People enrollment strip. A
    /// failure under this id means the saved voiceprints were NOT deleted (see DescribePurge).</summary>
    private const string PeopleFailureId = "people.json";

    /// <summary>MaintenanceService's sentinel failure id for "the sessions folder could not even
    /// be enumerated" - no session's voice data was swept.</summary>
    private const string SweepFailureId = "<sessions>";

    /// <summary>Honest-copy rule (locked for the whole feature): this is a convenience suggester,
    /// not voice identification. Nothing here may imply forensic capability.</summary>
    public string VoiceprintsNote { get; } =
        "A voiceprint lets LocalScribe suggest a name for a speaker it has heard before. "
        + "Voiceprints are stored only on this computer, are only ever a suggestion you accept or "
        + "dismiss, and can be deleted at any time. A suggestion is a similarity hint, not proof "
        + "of identity - LocalScribe never assigns a name on its own.";

    public string BackfillNote { get; } =
        "Scans finished sessions for speakers already linked to a person and saves a voiceprint "
        + "from their audio. Only speakers a person already owns are used; no new people are "
        + "created, and nothing is renamed.";

    public string ReenrollNote { get; } =
        "Saved with an older voice model - it cannot be matched. Delete it and enroll again.";

    /// <summary>The purge confirmation. States plainly what goes and what does NOT: a destructive,
    /// irreversible action on user data has to be legible before it is taken.
    ///
    /// Final review finding M2 - the reach clause. This purge sweeps the sessions folder and
    /// people.json, and nothing else. Two places hold voice measurements it CANNOT touch: a session
    /// folder already sent to the Recycle Bin (SessionDeleter recycles the whole folder,
    /// embeddings.json included) and an export .zip created before exports stopped carrying
    /// embeddings.json (SessionArchiver excludes it now, but a zip already written is out of
    /// reach). Deletion copy that promised "every session's stored voice measurements" full stop
    /// promised more than the action delivers, so it names both.</summary>
    public const string PurgeConfirmMessage =
        "Delete ALL voiceprint data? Every saved voiceprint, and the stored voice measurements in "
        + "every session in your storage folder, will be deleted. People keep their names; "
        + "transcripts, speaker names, and audio are untouched. This cannot be undone. "
        + "Two things this cannot reach: sessions you have already deleted (their voice "
        + "measurements went to the Recycle Bin with them - empty it to remove those too), and any "
        + "export .zip file created before this version, which may still contain them.";

    /// <summary>The saved people, newest state of people.json. Rebuilt wholesale on every load.</summary>
    public ObservableCollection<PersonRowViewModel> People { get; } = [];

    /// <summary>Awaitable people-list load (the LastSave / AssistantModelsLoad precedent - tests
    /// await it so no reload is in flight when they assert).</summary>
    public Task PeopleLoad { get; private set; } = Task.CompletedTask;

    /// <summary>Nothing is stored - drives the explicit empty state (an empty list with no message
    /// leaves "is anything saved about me?" unanswered, which is the one question this section
    /// exists to answer). Defaults false, NOT true: true is an affirmative claim that disk was
    /// checked and found empty, and neither the pre-load moment nor an inert (no <see cref="_people"/>)
    /// composition has checked anything (fix round 1, finding 1). Only a successful
    /// <see cref="ReloadPeopleAsync"/> may set this true.</summary>
    [ObservableProperty] private bool _hasNoPeople;

    /// <summary>The truth is UNKNOWN, not empty: the last attempt to read people.json threw (a
    /// corrupt or forward-versioned file). This must never collapse into <see cref="HasNoPeople"/>
    /// - biometric data can be sitting on disk right now, so the "nothing is stored" claim would be
    /// a false negative from the one screen whose job is to say what is stored (fix round 1,
    /// finding 1).</summary>
    [ObservableProperty] private bool _peopleUnreadable;

    /// <summary>Copy for <see cref="PeopleUnreadable"/> - bound from SettingsPage.xaml and directly
    /// test-pinned, the same instance-property pattern as <see cref="VoiceprintsNote"/> and its
    /// siblings (a bindable path needs a real property, not a const field).</summary>
    public string PeopleUnreadableNote { get; } =
        "The saved-people file could not be read. Voiceprints may still be stored on this "
        + "computer.";

    [ObservableProperty] private string _backfillStatus = "";

    /// <summary>What the last purge actually did. Never phrased as success when anything was
    /// skipped - see DescribePurge.</summary>
    [ObservableProperty] private string _purgeStatus = "";

    /// <summary>The last purge did NOT delete everything it was asked to. Styles PurgeStatus as a
    /// warning rather than an ordinary note.</summary>
    [ObservableProperty] private bool _purgeIncomplete;

    /// <summary>True while ANY voiceprint command (backfill, purge, or one of the three deletes)
    /// is in flight - the single re-entrancy gate for this whole section (final whole-branch review
    /// finding I2).
    ///
    /// Without it the commands were freely re-entrant, and the worst pair defeats deletion itself:
    /// a purge fired during the (minutes-long, one out-of-process embed per participant) backfill
    /// clears people.json and reports "Deleted all saved voiceprints...", then the scan reaches its
    /// terminal save which BY DESIGN reloads fresh and re-applies THIS scan's own enrollments - so
    /// voiceprints are back on disk moments after a purge reported complete success. Two
    /// overlapping backfills are the milder sibling: double enrollments against the FIFO-20 cap,
    /// evicting genuine older samples.
    ///
    /// Set synchronously at command entry (commands are invoked on the UI thread, exactly as
    /// IsRegenerating is) so the gate is closed before the first await; cleared through the
    /// injected dispatch because that continuation may resume off the UI thread.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VoiceprintCommandsEnabled))]
    [NotifyCanExecuteChangedFor(nameof(BackfillScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(PurgeVoiceprintsCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteEnrollmentCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteVoiceprintCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeletePersonCommand))]
    private bool _isVoiceprintBusy;

    /// <summary>Bindable inverse of <see cref="IsVoiceprintBusy"/>. The two action buttons are
    /// disabled by their own commands' CanExecute, but the per-person rows set IsEnabled
    /// explicitly (HasEnrollments), which overrides a command's automatic disable - so the list
    /// container binds this and WPF's inherited IsEnabled does the rest.</summary>
    public bool VoiceprintCommandsEnabled => !IsVoiceprintBusy;

    /// <summary>CanExecute for all five voiceprint commands (see <see cref="IsVoiceprintBusy"/>).
    /// A property, not a method, so the parameterised delete commands can share it.</summary>
    private bool CanRunVoiceprintCommand => !IsVoiceprintBusy;

    /// <summary>Turns a <see cref="VoiceprintPurgeResult"/> into what the user is told. Pure and
    /// public so the wording itself is directly test-pinned.
    ///
    /// The two failure kinds are NOT interchangeable:
    /// <list type="bullet">
    /// <item>A <c>people.json</c> failure means MaintenanceService could not read the registry and
    /// therefore SKIPPED the enrollment strip entirely - the saved voiceprints, the most
    /// identifying data in the product, are still on disk. That must be stated as "not deleted",
    /// never softened and never reported as a success with a footnote.</item>
    /// <item>A per-session id failure means one session's own derived voice data (embeddings.json /
    /// suggestion provenance) survives, while the saved voiceprints themselves were deleted.
    /// Incomplete, but a materially smaller thing - so it is said differently.</item>
    /// </list>
    /// <c>&lt;sessions&gt;</c> means the session sweep could not be enumerated at all.</summary>
    public static (string Message, bool Incomplete) DescribePurge(VoiceprintPurgeResult result)
    {
        if (result.Failures.Count == 0)
            return ($"Deleted all saved voiceprints. Voice data was cleared from "
                    + $"{result.SessionsTouched} session(s). Names, transcripts, and audio were "
                    + "not changed.", false);

        string? peopleError = result.Failures
            .Where(f => f.Id == PeopleFailureId).Select(f => f.Error).FirstOrDefault();
        string? sweepError = result.Failures
            .Where(f => f.Id == SweepFailureId).Select(f => f.Error).FirstOrDefault();
        var sessionIds = result.Failures
            .Where(f => f.Id != PeopleFailureId && f.Id != SweepFailureId)
            .Select(f => f.Id).ToList();

        // Fix round 1, finding 3: with the sweep itself unreadable, SessionsTouched is not a count
        // of anything that happened - it must not be voiced here, or "cleared from 0 session(s)"
        // would sit right next to "no session's stored voice measurements were deleted" and
        // contradict it.
        string sessionsClause = sweepError is null
            ? $" Voice data was cleared from {result.SessionsTouched} session(s)."
            : "";
        var parts = new List<string>
        {
            peopleError is not null
                // Fix round 1, finding 2: a people.json failure does not stop the session sweep -
                // sessions genuinely touched here must still be mentioned, not silently dropped.
                ? "The saved voiceprints could NOT be deleted and are still stored on this "
                  + $"computer (people.json could not be read: {peopleError}). Fix or remove that "
                  + "file, then purge again." + sessionsClause
                : "The saved voiceprints were deleted." + sessionsClause,
        };
        if (sweepError is not null)
            parts.Add("The sessions folder could not be read, so no session's stored voice "
                      + $"measurements were deleted ({sweepError}).");
        if (sessionIds.Count > 0)
            parts.Add($"{sessionIds.Count} session(s) could not be cleared and still hold stored "
                      + $"voice measurements: {string.Join(", ", sessionIds)}.");
        return (string.Join(" ", parts), true);
    }

    /// <summary>Deletes this person's OLDEST enrollment (the plan's declared deviation: the row
    /// carries a count, not an expandable per-enrollment list).</summary>
    [RelayCommand(CanExecute = nameof(CanRunVoiceprintCommand))]
    private Task DeleteEnrollmentAsync(PersonRowViewModel? row)
        => row?.OldestEnrollmentId is string enrollmentId
            ? MutatePeopleAsync(reg => PeopleRegistryOps.RemoveEnrollment(reg, row.Id, enrollmentId))
            : Task.CompletedTask;

    /// <summary>Deletes every enrollment for this person; the Person (and their name) survives.
    /// Deliberately NOT confirm-gated: this deletes the user's own biometric data at their own
    /// request, and the failure mode of an accidental click is "re-enroll", not data loss.</summary>
    [RelayCommand(CanExecute = nameof(CanRunVoiceprintCommand))]
    private Task DeleteVoiceprintAsync(PersonRowViewModel? row)
        => row is null
            ? Task.CompletedTask
            : MutatePeopleAsync(reg => PeopleRegistryOps.DeleteVoiceprint(reg, row.Id));

    /// <summary>Removes the Person entirely (and with them their voiceprint). Confirm-gated: this
    /// one destroys a name the user typed and breaks roster links, so it is not re-creatable by
    /// re-enrolling. Speaker names already written into transcripts are never touched.</summary>
    [RelayCommand(CanExecute = nameof(CanRunVoiceprintCommand))]
    private Task DeletePersonAsync(PersonRowViewModel? row)
    {
        if (row is null || _confirm is null) return Task.CompletedTask;
        if (!_confirm($"Delete \"{row.Name}\" from saved people, including any saved voiceprint? "
                      + "Names already written into transcripts and matter rosters are not changed."))
            return Task.CompletedTask;
        return MutatePeopleAsync(reg => PeopleRegistryOps.RemovePerson(reg, row.Id));
    }

    /// <summary>The global purge (design section "Deletion - three levels", level 3). Reports what
    /// actually happened - a partially-failed purge is never rendered as success.</summary>
    [RelayCommand(CanExecute = nameof(CanRunVoiceprintCommand))]
    private async Task PurgeVoiceprintsAsync()
    {
        if (_confirm is null || !_confirm(PurgeConfirmMessage)) return;
        IsVoiceprintBusy = true;    // see IsVoiceprintBusy: closed BEFORE the first await
        try
        {
            try
            {
                var result = await _maintenance.PurgeVoiceprintDataAsync(CancellationToken.None);
                var (message, incomplete) = DescribePurge(result);
                _dispatch(() => { PurgeStatus = message; PurgeIncomplete = incomplete; });
            }
            catch (Exception ex)
            {
                // The purge threw outright (e.g. the registry rewrite itself failed). Nothing about
                // what survived is known here, so say the least-flattering true thing.
                _dispatch(() =>
                {
                    PurgeStatus = "The purge did not finish. Some voiceprint data may still be stored "
                                  + "on this computer - check the error and try again.";
                    PurgeIncomplete = true;
                });
                _errors.Report("Voiceprints", ex);
            }
            // Outside the inner try: a failed purge still has to leave the list showing DISK truth,
            // not the pre-purge snapshot (ReloadPeopleAsync reports its own failures).
            await ReloadPeopleAsync();
        }
        // finally, never a plain trailing assignment: a throw escaping the reload must not wedge
        // every voiceprint command (including the deletes) permanently disabled.
        finally { _dispatch(() => IsVoiceprintBusy = false); }
    }

    /// <summary>One-batch backfill (the plan's declared deviation from a per-session action):
    /// enrolls voiceprints for speakers a person already durably owns in sessions diarised before
    /// embeddings existed. Never creates a person, never renames anything.</summary>
    [RelayCommand(CanExecute = nameof(CanRunVoiceprintCommand))]
    private async Task BackfillScanAsync()
    {
        if (_enrollment is null || _embeddingEngine is null || _resolveModel is null) return;
        // Same one-engine refusal as the Split Speakers run (design 2026-07-28 adjacent fix 3):
        // this walks EVERY finished session through the diarisation helper.
        if (_engineBusy?.Invoke() is string busy)
        {
            _dispatch(() => BackfillStatus = busy);
            return;
        }
        IsVoiceprintBusy = true;    // see IsVoiceprintBusy: closed BEFORE the first await
        _dispatch(() => BackfillStatus = "Scanning sessions...");
        try
        {
            try
            {
                string embModel = _resolveModel(EmbeddingModelFile);
                var report = await _enrollment.BackfillScanAsync(
                    _embeddingEngine, embModel, ResolveLeg, CancellationToken.None);
                string message = $"Scanned {report.SessionsScanned} session(s) - enrolled "
                                 + $"{report.Enrolled}, skipped {report.Skipped}.";
                if (report.Skipped > 0)
                    message += " Skipped sessions could not be read and were left untouched.";
                _dispatch(() => BackfillStatus = message);
            }
            catch (Exception ex)
            {
                _dispatch(() => BackfillStatus =
                    "The scan stopped early because of an error. Some sessions were not scanned.");
                _errors.Report("Voiceprints", ex);
            }
            await ReloadPeopleAsync();
        }
        finally { _dispatch(() => IsVoiceprintBusy = false); }
    }

    /// <summary>Re-reads people.json into <see cref="People"/>. Public for the page-navigation
    /// refresh: enrollments are made on the SPLIT-SPEAKERS dialog, never here, so a Settings VM
    /// built once at startup would otherwise show counts that predate every enrollment the user
    /// has made since - the same "one surface lies about another" defect AssistantHelperNote was
    /// fixed for (Task 5 review). SettingsPage.Loaded calls this on every re-navigation, mirroring
    /// RefreshAssistantHelperNote. Reports its own failures, so fire-and-forget is safe.</summary>
    public Task RefreshPeopleAsync() => PeopleLoad = ReloadPeopleAsync();

    /// <summary>Load-mutate-save-reload, shared by the three delete levels. Never throws into the
    /// UI (the neighbouring commands' idiom) and always ends by re-reading disk, so the counts on
    /// screen can never outlive the file they describe.</summary>
    private async Task MutatePeopleAsync(Func<PeopleRegistry, PeopleRegistry> mutate)
    {
        if (_people is null) return;
        IsVoiceprintBusy = true;    // see IsVoiceprintBusy: closed BEFORE the first await
        try
        {
            try
            {
                var registry = await _people.LoadAsync(CancellationToken.None);
                if (registry is not null)               // null = nothing stored, nothing to delete
                    await _people.SaveAsync(mutate(registry), CancellationToken.None);
            }
            catch (Exception ex) { _errors.Report("Voiceprints", ex); }
            // Always, even on the nothing-to-do and failed paths: the list must end on DISK truth,
            // not on whatever snapshot happened to be on screen when the command was invoked.
            await ReloadPeopleAsync();
        }
        finally { _dispatch(() => IsVoiceprintBusy = false); }
    }

    private async Task ReloadPeopleAsync()
    {
        if (_people is null) return;
        try
        {
            var registry = await _people.LoadAsync(CancellationToken.None);
            var rows = (registry?.People ?? [])
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => new PersonRowViewModel(p))
                .ToList();
            _dispatch(() =>
            {
                People.Clear();
                foreach (var row in rows) People.Add(row);
                HasNoPeople = People.Count == 0;
                PeopleUnreadable = false;
            });
        }
        catch (Exception ex)
        {
            // Fix round 1, finding 1: the truth is now UNKNOWN, not empty and not whatever was on
            // screen before this call. Clear the rows (a pre-mutation snapshot must never linger
            // asserting a count disk no longer agrees with) and say so distinctly - never
            // HasNoPeople, which would read as "checked, nothing there" when nothing was confirmed.
            _dispatch(() =>
            {
                People.Clear();
                HasNoPeople = false;
                PeopleUnreadable = true;
            });
            _errors.Report("Voiceprints", ex);
        }
    }

    /// <summary>Leg probe for the backfill scan - mirrors SplitSpeakersViewModel.ProbeLeg's
    /// preferred-then-other format fall-back (a session recorded before a format change still
    /// resolves). The retained-source check lives in the caller's own session data.</summary>
    private string? ResolveLeg(string sessionId, SourceKind kind)
    {
        if (_paths is null) return null;
        var preferred = _settings.Current.AudioFormat;
        var other = preferred == AudioFormat.Flac ? AudioFormat.Wav : AudioFormat.Flac;
        foreach (var format in new[] { preferred, other })
        {
            string path = _paths.AudioFile(sessionId, kind, format);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // --- MCP Access (design 2026-07-26) -------------------------------------------------
    // This screen is the ONLY writer of mcp/consent.json. The server re-reads that file on every
    // tool call and fails closed (absent/unreadable/malformed == disabled) - so simply saving here
    // IS the revocation, immediately, with no separate "apply" step. Polarity is deliberate:
    // enabling requires an explicit confirm (a Yes/No MessageBox, never a Wpf.Ui FluentWindow - see
    // App.xaml.cs); disabling never does, because turning exposure OFF can never be the harmful
    // direction. A matter's allowlist membership is NOT cleared when MCP is disabled (design: the
    // list is remembered, only exposure is off - so re-enabling can never silently expose more than
    // was ticked before, nor silently expose less by having "forgotten" the ticks).

    public const string McpEnableWarning =
        "Expose selected matters to MCP clients?\n\n"
        + "Apps you register (for example Claude Desktop) will be able to search and read the "
        + "transcripts of the matters you tick below. Everything stays on this computer and is "
        + "read-only, but what those apps do with text they read is outside LocalScribe's control. "
        + "Every read is recorded in the audit log.";

    /// <summary>Awaitable initial load (the PeopleLoad/AssistantModelsLoad precedent - tests await
    /// it so no load is in flight when they assert).</summary>
    public Task McpLoad { get; private set; } = Task.CompletedTask;

    /// <summary>The last consent.json save. Production fire-and-forgets; tests await it.</summary>
    public Task McpSave { get; private set; } = Task.CompletedTask;

    /// <summary>The full matter list (both active and archived - archiving a matter must not
    /// silently strip it from an allowlist the user can no longer see to review).</summary>
    public ObservableCollection<McpMatterToggle> McpMatters { get; } = [];

    private bool _mcpEnabled;

    /// <summary>Master exposure toggle. Turning ON requires an explicit confirm (design: the one
    /// deliberate consent moment) - on decline, the field is left untouched and nothing is saved;
    /// OnPropertyChanged still fires so a two-way-bound CheckBox that optimistically flipped itself
    /// reverts to match. Turning OFF is never confirm-gated and saves immediately.</summary>
    public bool McpEnabled
    {
        get => _mcpEnabled;
        set
        {
            if (value == _mcpEnabled) return;
            if (value && (_confirmMcpEnable is null || !_confirmMcpEnable(McpEnableWarning)))
            {
                OnPropertyChanged();     // revert the bound CheckBox; _mcpEnabled is unchanged
                return;
            }
            _mcpEnabled = value;
            OnPropertyChanged();
            QueueMcpSave();
        }
    }

    private bool _mcpAllowUnassigned;

    /// <summary>Include sessions tagged to no matter at all. Never confirm-gated (it is scoped by
    /// the master McpEnabled toggle, same as every per-matter tick below).</summary>
    public bool McpAllowUnassigned
    {
        get => _mcpAllowUnassigned;
        set
        {
            if (value == _mcpAllowUnassigned) return;
            _mcpAllowUnassigned = value;
            OnPropertyChanged();
            QueueMcpSave();
        }
    }

    /// <summary>The claude_desktop_config.json / `claude mcp add` snippet for this install.
    /// Copy-to-clipboard only - LocalScribe never writes another application's config file.</summary>
    public string McpConfigSnippet
    {
        get
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "LocalScribe.Mcp.exe");
            string root = new StoragePaths(_settings.Current.StorageRoot).Root;
            var doc = new
            {
                mcpServers = new
                {
                    localscribe = new { command = exe, args = new[] { "--storage-root", root } },
                },
            };
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    public IRelayCommand CopyMcpSnippetCommand { get; }
    public IRelayCommand OpenMcpAuditFolderCommand { get; }

    /// <summary>Reads mcp/consent.json + the matters index for the CURRENT storage root and
    /// populates the section. Read-only - never creates mcp/consent.json (that only ever happens
    /// on the first save, i.e. the first explicit enable or per-matter tick).</summary>
    private async Task LoadMcpAsync()
    {
        var mcpPaths = new StoragePaths(_settings.Current.StorageRoot);
        McpConsentDocument consent;
        try
        {
            consent = await new McpConsentStore(mcpPaths).ReadCurrentAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _errors.Report("MCP access", ex);
            consent = new McpConsentDocument();     // fail closed, matches the store's own contract
        }

        IReadOnlyList<MattersIndexEntry> matters;
        try
        {
            var index = await new MatterStore(mcpPaths.MattersDir).ListAsync(CancellationToken.None);
            matters = index.Matters;
        }
        catch (Exception ex)
        {
            _errors.Report("MCP access", ex);
            matters = [];
        }

        _dispatch(() =>
        {
            // Direct field writes, not the McpEnabled/McpAllowUnassigned setters: this is loading
            // saved state, not a user edit, so it must never re-trigger the confirm gate or a save.
            _mcpEnabled = consent.Enabled;
            OnPropertyChanged(nameof(McpEnabled));
            _mcpAllowUnassigned = consent.AllowUnassigned;
            OnPropertyChanged(nameof(McpAllowUnassigned));

            McpMatters.Clear();
            foreach (var m in matters)
            {
                string label = string.IsNullOrWhiteSpace(m.Reference) ? m.Name : $"{m.Name} ({m.Reference})";
                bool allowed = consent.AllowedMatterIds.Contains(m.Id, StringComparer.Ordinal);
                McpMatters.Add(new McpMatterToggle(m.Id, label, allowed, QueueMcpSave));
            }
        });
    }

    /// <summary>Chains onto the previous save (the CommitAsync precedent - two quick ticks must
    /// both survive, never a lost update from a stale read-modify-write).</summary>
    private void QueueMcpSave() => McpSave = SaveMcpConsentAsync(McpSave);

    private async Task SaveMcpConsentAsync(Task prior)
    {
        if (!prior.IsCompleted) { try { await prior; } catch { /* prior reported its own error */ } }
        var doc = new McpConsentDocument
        {
            Enabled = McpEnabled,
            AllowedMatterIds = McpMatters.Where(m => m.IsAllowed).Select(m => m.Id).ToList(),
            AllowUnassigned = McpAllowUnassigned,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        try
        {
            var store = new McpConsentStore(new StoragePaths(_settings.Current.StorageRoot));
            await store.SaveAsync(doc, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The server enforces whatever consent.json says, re-read on every tool call - this
            // VM is only a view of that file. A save that throws here must NEVER leave the UI
            // showing less exposure than what is actually still enforced (e.g. the checkbox
            // reading OFF while consent.json still says enabled:true). Reload the ACTUAL disk
            // state and republish it, discarding the optimistic in-memory edit that failed to
            // save. Reuses LoadMcpAsync (the one other reader of this file) so the reload goes
            // through the same direct-field-write path that deliberately bypasses the property
            // setters and therefore cannot itself queue another save.
            _errors.Report(
                "MCP access change was NOT applied - what was already saved is still in effect", ex);
            await LoadMcpAsync();
        }
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
