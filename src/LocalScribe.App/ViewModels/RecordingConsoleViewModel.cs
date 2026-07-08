using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
namespace LocalScribe.App.ViewModels;

/// <summary>One selectable matter in the Record-console picker (Stage 6.2 Task 7). Rows are
/// rebuilt (not mutated) on every toggle/search so IsSelected always reflects the VM's
/// _pickedMatterIds - the XAML CheckBox binds IsChecked OneWay, so the VM stays the single
/// source of truth.</summary>
public sealed record MatterPickRow(string Id, string Display, bool IsSelected);

/// <summary>Idle-state brains of the Record console (design 5.4 section 6): a settings-derived
/// summary of what Start WILL capture, plus the per-session target-app selector that seeds from
/// Settings.Remote.App and mirrors into RemoteAppOverride - never into settings.json. All
/// lifecycle state/commands stay on the shared SessionViewModel (locked decision 1: no new
/// lifecycle logic; this VM only composes it). WPF-free; settings.Changed carries no thread
/// contract, so its handler marshals through the injected dispatch.
/// Stage 6.2 Task 7 adds an optional multi-select matter picker: ticking a matter writes
/// MatterSelectionOverride.MatterIds (mirrors RemoteAppOverride - per-session, never persisted
/// to settings.json), and SessionViewModel reads the seam at Start to bias the Whisper prompt +
/// seed meta.MatterIds. Ending a session (Idle) clears the picks, same as the app selector.</summary>
public sealed partial class RecordingConsoleViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly RemoteAppOverride _remoteOverride;
    private readonly MaintenanceService _maintenance;
    private readonly MatterSelectionOverride _matterSelection;
    private readonly ICaptureDeviceEnumerator _deviceEnumerator;
    private readonly MicOverride _micOverride;
    private readonly Action<Action> _dispatch;
    private readonly List<MattersIndexEntry> _allMatters = new();
    private readonly HashSet<string> _pickedMatterIds = new(StringComparer.Ordinal);
    private MicChoice _selectedMic;

    public SessionViewModel Session { get; }

    /// <summary>The console selector's text: the app to record for THIS session. Seeds from
    /// Settings.Remote.App; re-seeds when a session ends (next session reverts to the saved
    /// default) and when a settings save changes the default under an untouched selector.</summary>
    [ObservableProperty] private string _sessionTargetApp = "";

    public bool ShowAppSelector => _settings.Current.Remote.Mode != RemoteMode.SystemMix;
    public IReadOnlyList<string> AppSuggestions { get; } = RemoteCapturePlanner.SuggestedPerProcessApps;

    public string RemoteSummary
    {
        get
        {
            var remote = _settings.Current.Remote;
            if (remote.Mode == RemoteMode.SystemMix) return "Remote audio: full system mix";
            if (remote.Mode == RemoteMode.PerProcess)
            {
                string target = SessionTargetApp.Trim();
                return target.Length > 0
                    ? $"Remote audio: per-app ({target})"
                    : "Remote audio: per-app (no app set - will fall back to system mix)";
            }
            return "Remote audio: auto (Webex/Zoom per-app when found, else system mix)";
        }
    }

    public string MicSummary
    {
        get
        {
            var mic = _micOverride.Override ?? _settings.Current.Mic;
            return mic.Mode == MicMode.Pinned
                ? "Microphone: pinned - " + (mic.Name ?? "(unnamed device)")
                : "Microphone: follows the Windows Communications default";
        }
    }

    public IReadOnlyList<MicChoice> MicChoices { get; }

    /// <summary>The per-session mic choice: writes MicOverride.Override (a device pin or
    /// follow-default) - never settings.json. Cleared on Idle. Seeded from Settings.Mic.</summary>
    public MicChoice SelectedMic
    {
        get => _selectedMic;
        set
        {
            if (value is null || value == _selectedMic) return;
            _selectedMic = value;
            _micOverride.Override = value.Id is null
                ? new MicSetting { Mode = MicMode.FollowDefault }
                : new MicSetting { Mode = MicMode.Pinned, Id = value.Id, Name = value.Name };
            OnPropertyChanged();
            OnPropertyChanged(nameof(MicSummary));
        }
    }

    /// <summary>The matter picker's search box (Stage 6.2). Filters MatterOptions live.</summary>
    public ObservableCollection<MatterPickRow> MatterOptions { get; } = new();
    [ObservableProperty] private string _matterPickerQuery = "";

    public string SelectedMatterSummary => _pickedMatterIds.Count == 0
        ? "No matters selected (record first, classify later)."
        : $"{_pickedMatterIds.Count} matter(s) selected - their vocabulary will bias this recording.";

    public IRelayCommand<MatterPickRow> ToggleMatterCommand { get; }

    public RecordingConsoleViewModel(ISettingsService settings, SessionViewModel session,
        RemoteAppOverride remoteOverride, MaintenanceService maintenance,
        MatterSelectionOverride matterSelection, ICaptureDeviceEnumerator deviceEnumerator,
        MicOverride micOverride, Action<Action> dispatch)
    {
        (_settings, Session, _remoteOverride, _maintenance, _matterSelection, _dispatch)
            = (settings, session, remoteOverride, maintenance, matterSelection, dispatch);
        _deviceEnumerator = deviceEnumerator;
        _micOverride = micOverride;
        _sessionTargetApp = settings.Current.Remote.Mode == RemoteMode.PerProcess
            ? (settings.Current.Remote.App ?? "") : "";
        _remoteOverride.App = settings.Current.Remote.Mode == RemoteMode.PerProcess
            ? Normalize(_sessionTargetApp) : null;
        MicChoices = BuildMicChoices(out _selectedMic);
        ToggleMatterCommand = new RelayCommand<MatterPickRow>(ToggleMatter);
        settings.Changed += OnSettingsChanged;
        session.PropertyChanged += OnSessionChanged;
    }

    private static string? Normalize(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Follow-default choice + one per live device, with the choice matching the current
    /// Settings.Mic selected (a saved pin whose device is absent falls back to follow-default in
    /// the seed - capture's own marker handles the real absence at Start).</summary>
    private IReadOnlyList<MicChoice> BuildMicChoices(out MicChoice selected)
    {
        var follow = new MicChoice(null, "", "Windows Communications default (follow)");
        var choices = new List<MicChoice> { follow };
        foreach (var d in _deviceEnumerator.ListInputDevices())
            choices.Add(new MicChoice(d.Id, d.Name, d.Name));

        var mic = _settings.Current.Mic;
        selected = mic.Mode == MicMode.Pinned && !string.IsNullOrEmpty(mic.Id)
            ? choices.FirstOrDefault(c => c.Id == mic.Id) ?? follow
            : follow;
        return choices;
    }

    private MicChoice BuildSelectedFromSettings()
    {
        var mic = _settings.Current.Mic;
        return mic.Mode == MicMode.Pinned && !string.IsNullOrEmpty(mic.Id)
            ? MicChoices.FirstOrDefault(c => c.Id == mic.Id) ?? MicChoices[0]
            : MicChoices[0];
    }

    /// <summary>Load the matter catalog for the picker (index entries only, non-archived).
    /// Called when the console appears; safe to call repeatedly. Best-effort: the picker is
    /// optional (record first, classify later - locked decision), so a failed catalog read must
    /// never crash the Record console; this ctor takes no IUiErrorReporter (its signature is
    /// pinned by tests), so a failure just leaves MatterOptions as they were until the next
    /// successful call.</summary>
    public async Task LoadMattersAsync()
    {
        try
        {
            var index = await _maintenance.ListMattersAsync(CancellationToken.None);
            _allMatters.Clear();
            _allMatters.AddRange(index.Matters.Where(m => !m.Archived));

            // Review fix: reconcile already-picked ids against the reloaded catalog. A matter
            // can be deleted (or archived) between console refreshes while still picked; without
            // this, _pickedMatterIds/_matterSelection.MatterIds would keep a dangling id that no
            // longer resolves to any matter, and Start would persist it into meta.MatterIds on
            // the finalized session (the Whisper prompt just skips the missing file, but the
            // stale tag itself gets written to disk). Intersecting against the reloaded set is a
            // no-op whenever every pick is still valid (normal refresh) or picks are already
            // empty (post-Idle refresh), so this never changes existing behavior in those cases.
            var validIds = _allMatters.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
            int before = _pickedMatterIds.Count;
            _pickedMatterIds.IntersectWith(validIds);
            if (_pickedMatterIds.Count != before)
            {
                _matterSelection.MatterIds = _pickedMatterIds.ToList();
                OnPropertyChanged(nameof(SelectedMatterSummary));
            }

            RebuildMatterOptions();
        }
        catch (Exception ex)
        {
            // Best-effort; see summary above. Still logged so a broken matters index is visible
            // in diagnostics instead of silently leaving the picker stale/empty.
            Debug.WriteLine($"LoadMattersAsync failed: {ex}");
        }
    }

    partial void OnMatterPickerQueryChanged(string value) => RebuildMatterOptions();

    private void RebuildMatterOptions()
    {
        string q = MatterPickerQuery.Trim();
        MatterOptions.Clear();
        foreach (var e in _allMatters)
            if (q.Length == 0 || MatterSearch.Matches(e, q))
                MatterOptions.Add(new MatterPickRow(e.Id,
                    string.IsNullOrEmpty(e.Reference) ? e.Name : $"{e.Name} ({e.Reference})",
                    _pickedMatterIds.Contains(e.Id)));
    }

    private void ToggleMatter(MatterPickRow? row)
    {
        if (row is null) return;
        if (!_pickedMatterIds.Remove(row.Id)) _pickedMatterIds.Add(row.Id);
        _matterSelection.MatterIds = _pickedMatterIds.ToList();
        RebuildMatterOptions();
        OnPropertyChanged(nameof(SelectedMatterSummary));
    }

    partial void OnSessionTargetAppChanged(string value)
    {
        _remoteOverride.App = Normalize(value);
        OnPropertyChanged(nameof(RemoteSummary));
    }

    // A finished session reverts the selector (and thus the override) to the saved default:
    // the override is per-session by construction, not by cleanup code elsewhere.
    private void OnSessionChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionViewModel.State)) return;
        if (Session.State == SessionState.Idle)
        {
            SessionTargetApp = _settings.Current.Remote.Mode == RemoteMode.PerProcess
                ? (_settings.Current.Remote.App ?? "") : "";
            _pickedMatterIds.Clear();
            _matterSelection.MatterIds = [];
            _micOverride.Override = null;
            _selectedMic = BuildSelectedFromSettings();
            OnPropertyChanged(nameof(SelectedMic));
            OnPropertyChanged(nameof(MicSummary));
            RebuildMatterOptions();
            // Review fix: also refresh the catalog itself so a matter created during/after the
            // last session appears without waiting for the console window's next visible-refresh.
            // Fire-and-forget AFTER the synchronous RebuildMatterOptions() above so the picks-
            // cleared rebuild (and thus MatterOptions/seam.MatterIds going empty) stays
            // deterministic and synchronous with Stop; this async reload only re-rebuilds from
            // the (still-cleared) picks once the catalog read completes. LoadMattersAsync has its
            // own try/catch, so this fire-and-forget can never throw unobserved.
            _ = LoadMattersAsync();
            OnPropertyChanged(nameof(SelectedMatterSummary));
        }
    }

    private void OnSettingsChanged(Settings oldSettings, Settings newSettings)
        => _dispatch(() =>
        {
            // Re-seed only an UNTOUCHED selector (still equal to the old default): a user's
            // in-flight per-session edit is never clobbered by a background settings save.
            string newDefault = newSettings.Remote.Mode == RemoteMode.PerProcess
                ? (newSettings.Remote.App ?? "") : "";
            string oldDefault = oldSettings.Remote.Mode == RemoteMode.PerProcess
                ? (oldSettings.Remote.App ?? "") : "";
            if (SessionTargetApp == oldDefault) SessionTargetApp = newDefault;
            OnPropertyChanged(nameof(ShowAppSelector));
            OnPropertyChanged(nameof(RemoteSummary));
            OnPropertyChanged(nameof(MicSummary));
        });

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        Session.PropertyChanged -= OnSessionChanged;
    }
}
