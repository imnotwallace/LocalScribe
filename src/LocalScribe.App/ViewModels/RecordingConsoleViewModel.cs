using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Transcription;
namespace LocalScribe.App.ViewModels;

/// <summary>One selectable matter in the Record-console picker (Stage 6.2 Task 7). Rows are
/// rebuilt (not mutated) on every toggle/search so IsSelected always reflects the VM's
/// _pickedMatterIds - the XAML CheckBox binds IsChecked OneWay, so the VM stays the single
/// source of truth.</summary>
public sealed record MatterPickRow(string Id, string Display, bool IsSelected);

/// <summary>One entry in the console's Remote-target picker (design 2026-07-12 section 1): a
/// display label plus the RemoteSetting it applies. IsSystemMix drives the live confirm gate.
/// Value-record equality lets it back a ComboBox SelectedItem and the dedup set.</summary>
public sealed record RemoteTargetOption(string Label, RemoteSetting Setting, bool IsSystemMix);

/// <summary>Idle-state brains of the Record console (design 5.4 section 6): a settings-derived
/// summary of what Start WILL capture, plus the per-session Remote-target picker (Task 6, design
/// 2026-07-12) that seeds from Settings.Remote and mirrors into RemoteTargetOverride - never into
/// settings.json. All lifecycle state/commands stay on the shared SessionViewModel (locked
/// decision 1: no new lifecycle logic; this VM only composes it). WPF-free; settings.Changed
/// carries no thread contract, so its handler marshals through the injected dispatch.
/// Stage 6.2 Task 7 adds an optional multi-select matter picker: ticking a matter writes
/// MatterSelectionOverride.MatterIds (mirrors RemoteTargetOverride - per-session, never persisted
/// to settings.json), and SessionViewModel reads the seam at Start to bias the Whisper prompt +
/// seed meta.MatterIds. Ending a session (Idle) clears the picks, same as the target picker.</summary>
public sealed partial class RecordingConsoleViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly RemoteTargetOverride _remoteOverride;
    private readonly MaintenanceService _maintenance;
    private readonly MatterSelectionOverride _matterSelection;
    private readonly ICaptureDeviceEnumerator _deviceEnumerator;
    private readonly MicOverride _micOverride;
    private readonly Action<Action> _dispatch;
    private readonly List<MattersIndexEntry> _allMatters = new();
    private readonly HashSet<string> _pickedMatterIds = new(StringComparer.Ordinal);
    private MicChoice _selectedMic;

    public SessionViewModel Session { get; }

    // B1-2: the RemoteSummary/MicSummary display properties were removed - their XAML bindings were
    // replaced by the ready-card pre-flight line (design section 5 item 5); nothing bound them.

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
        }
    }

    private readonly IAudioSessionScanner _scanner;
    private readonly Func<bool> _confirmSystemMix;
    private RemoteTargetOption _selectedRemoteTarget = null!;

    /// <summary>The Remote-target picker's items: Auto, live apps (friendly-labelled, FullMix
    /// annotated), the always-present Webex/Zoom fallbacks, and System mix. Rebuilt on refresh.</summary>
    public ObservableCollection<RemoteTargetOption> RemoteTargetOptions { get; } = new();

    /// <summary>The chosen Remote target for THIS session. The setter mirrors into
    /// RemoteTargetOverride (never settings.json). Used by both the idle and live pickers; the live
    /// hot-swap + confirm gate live in ChangeRemoteTargetCommand.</summary>
    public RemoteTargetOption SelectedRemoteTarget
    {
        get => _selectedRemoteTarget;
        set
        {
            if (value is null || value == _selectedRemoteTarget) return;
            _selectedRemoteTarget = value;
            _remoteOverride.Override = value.Setting;
            OnPropertyChanged();
        }
    }

    public IAsyncRelayCommand<RemoteTargetOption> ChangeRemoteTargetCommand { get; }

    /// <summary>Ready-card pre-flight line (design 2026-07-13 section 5 item 5): what the remote
    /// leg WOULD capture right now, from the same WASAPI scan the target picker refreshes on and
    /// the same pure RemoteCapturePlanner Start resolves through. Replaces the two grey summary
    /// lines that duplicated the pickers. Informational ONLY - it NEVER gates or delays Start
    /// (locked anti-pattern, design section 7). "" until the first visible-refresh lands.</summary>
    [ObservableProperty] private string _preflightSummary = "";
    /// <summary>Ready-card engine chip (design 2026-07-13 section 5 item 4): the model+backend
    /// Start WOULD bind (settings + BackendSelector via SessionViewModel.PreviewEnginePlan),
    /// in the read-view footer's model-middledot-BACKEND shape (rendered "base.en (middot) CPU").
    /// "" until the first refresh.</summary>
    [ObservableProperty] private string _engineSummary = "";

    /// <summary>The matter picker's search box (Stage 6.2). Filters MatterOptions live.</summary>
    public ObservableCollection<MatterPickRow> MatterOptions { get; } = new();
    [ObservableProperty] private string _matterPickerQuery = "";

    public string SelectedMatterSummary => _pickedMatterIds.Count == 0
        ? "No matters selected (record first, classify later)."
        : $"{_pickedMatterIds.Count} matter(s) selected - their vocabulary will bias this recording.";

    public IRelayCommand<MatterPickRow> ToggleMatterCommand { get; }

    public RecordingConsoleViewModel(ISettingsService settings, SessionViewModel session,
        RemoteTargetOverride remoteOverride, MaintenanceService maintenance,
        MatterSelectionOverride matterSelection, ICaptureDeviceEnumerator deviceEnumerator,
        MicOverride micOverride, IAudioSessionScanner scanner, Func<bool> confirmSystemMix,
        Action<Action> dispatch)
    {
        (_settings, Session, _remoteOverride, _maintenance, _matterSelection, _dispatch)
            = (settings, session, remoteOverride, maintenance, matterSelection, dispatch);
        _deviceEnumerator = deviceEnumerator;
        _micOverride = micOverride;
        _scanner = scanner;
        _confirmSystemMix = confirmSystemMix;
        RebuildRemoteTargetOptions(Array.Empty<AudioSessionInfo>());     // base options (no scan yet)
        SeedSelectedFromSettings();
        MicChoices = BuildMicChoices(out _selectedMic);
        ToggleMatterCommand = new RelayCommand<MatterPickRow>(ToggleMatter);
        ChangeRemoteTargetCommand = new AsyncRelayCommand<RemoteTargetOption>(ChangeRemoteTargetAsync);
        settings.Changed += OnSettingsChanged;
        session.PropertyChanged += OnSessionChanged;
    }

    /// <summary>Rebuilds RemoteTargetOptions: Auto, then live apps (deduped by image, friendly-
    /// labelled, FullMix annotated), then the known fallbacks whose image is not already live, then
    /// System mix. Preserves the current selection by value when it still exists.</summary>
    private void RebuildRemoteTargetOptions(IReadOnlyList<AudioSessionInfo> active)
    {
        var seenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<RemoteTargetOption>
        { new("Auto - detect the call app", new RemoteSetting { Mode = RemoteMode.Auto }, false) };

        foreach (var s in active)
        {
            if (!seenImages.Add(s.ProcessName)) continue;
            // FullMix apps (Teams/browsers) are captured as system mix regardless, so annotate the
            // bare image name and DO NOT append the friendly bucket (which for e.g. chrome is
            // "Browser") - matches design section 4 and the test's expected "chrome (captured as
            // system mix)". Non-FullMix apps get the "image - Friendly" disambiguation.
            string label = RemoteCapturePlanner.IsFullMix(s.ProcessName)
                ? $"{s.ProcessName} (captured as system mix)"
                : AppKindResolver.FriendlyName(s.ProcessName) is { } friendly
                    ? $"{s.ProcessName} - {friendly}"
                    : s.ProcessName;
            options.Add(new RemoteTargetOption(label,
                new RemoteSetting { Mode = RemoteMode.PerProcess, App = s.ProcessName }, false));
        }

        foreach (var (friendly, image) in RemoteCapturePlanner.KnownTargets)
            if (seenImages.Add(image))
                options.Add(new RemoteTargetOption(friendly,
                    new RemoteSetting { Mode = RemoteMode.PerProcess, App = image }, false));

        options.Add(new RemoteTargetOption("System mix - everything",
            new RemoteSetting { Mode = RemoteMode.SystemMix }, true));

        RemoteTargetOptions.Clear();
        foreach (var o in options) RemoteTargetOptions.Add(o);

        // Preserve the selection by value; re-point the field at the equal instance in the new list
        // so ComboBox SelectedItem stays bound. Falls back to the settings-derived option.
        if (_selectedRemoteTarget is not null)
        {
            var match = RemoteTargetOptions.FirstOrDefault(o => o.Setting == _selectedRemoteTarget.Setting);
            _selectedRemoteTarget = match ?? OptionFor(_settings.Current.Remote);
            OnPropertyChanged(nameof(SelectedRemoteTarget));
        }
    }

    /// <summary>The option matching a RemoteSetting, creating an app option if the image is not in
    /// the current list (an unknown pinned app).</summary>
    private RemoteTargetOption OptionFor(RemoteSetting r)
    {
        if (r.Mode == RemoteMode.SystemMix)
            return RemoteTargetOptions.First(o => o.IsSystemMix);
        if (r.Mode == RemoteMode.PerProcess && !string.IsNullOrEmpty(r.App))
            return RemoteTargetOptions.FirstOrDefault(o => o.Setting.Mode == RemoteMode.PerProcess
                    && string.Equals(o.Setting.App, r.App, StringComparison.OrdinalIgnoreCase))
                ?? new RemoteTargetOption(r.App!, new RemoteSetting { Mode = RemoteMode.PerProcess, App = r.App }, false);
        return RemoteTargetOptions.First(o => o.Setting.Mode == RemoteMode.Auto);
    }

    /// <summary>One-click hand-off from the call-detect offer toast (design 2026-07-18 section
    /// 5.3): selects the detected app through the SAME SelectedRemoteTarget setter a manual pick
    /// uses, which mirrors into RemoteTargetOverride - so Start adopts it exactly like a user
    /// click and the console UI shows the applied target. ADVISORY-ONLY locked rule: this never
    /// starts anything itself (the toast action invokes the normal StartCommand separately).
    /// No-op unless Idle - a live session's target is never yanked by a background detection
    /// (the live hot-swap stays the confirm-gated ChangeRemoteTargetCommand path).</summary>
    public void ApplyDetectedTarget(string exe)
    {
        if (Session.State != SessionState.Idle) return;
        SelectedRemoteTarget = OptionFor(new RemoteSetting { Mode = RemoteMode.PerProcess, App = exe });
    }

    /// <summary>Pure mapping from (active render sessions, the APPLIED remote setting) to the
    /// ready card's pre-flight line (design 2026-07-13 section 5 item 5). Planner-truthful: a
    /// per-process plan reads "detected"; a LIVE full-mix image (Teams/browsers - forced to system
    /// mix by the planner) reads detected-but-system-mix; anything else (nothing playing, or a
    /// pinned app that is not live - the planner's fallback keeps plan.App = the requested image,
    /// hence the explicit live-ness check) reads the honest system-mix fallback. Explicit system
    /// mix is stated as such. Public static: tests drive every branch directly (no
    /// InternalsVisibleTo in this repo), and it holds no console state.</summary>
    public static string PreflightLine(IReadOnlyList<AudioSessionInfo> active, RemoteSetting remote)
    {
        if (remote.Mode == RemoteMode.SystemMix)
            return "System mix - all system audio will be recorded.";
        var plan = RemoteCapturePlanner.Plan(active, remote);
        if (plan.Mode == RemoteMode.PerProcess)
            return $"{AppKindResolver.FriendlyName(plan.App) ?? plan.App} detected - remote audio will be captured from it.";
        if (plan.App is { } image && RemoteCapturePlanner.IsFullMix(image)
            && active.Any(s => s.ProcessName.Contains(image, StringComparison.OrdinalIgnoreCase)))
            return $"{AppKindResolver.FriendlyName(image) ?? image} detected - will record system mix (shared-audio app).";
        return "No call app playing audio - will record system mix.";
    }

    /// <summary>Seeds the selection (and, per the old semantics, the override) from Settings.Remote
    /// WITHOUT going through the public setter: an untouched Auto/SystemMix selector leaves the
    /// override null so a background settings change still flows to capture; a PerProcess base arms
    /// the override with that app (equivalent to the pre-picker seeding).</summary>
    private void SeedSelectedFromSettings()
    {
        var r = _settings.Current.Remote;
        _selectedRemoteTarget = OptionFor(r);
        _remoteOverride.Override = r.Mode == RemoteMode.PerProcess && !string.IsNullOrEmpty(r.App)
            ? new RemoteSetting { Mode = RemoteMode.PerProcess, App = r.App } : null;
        OnPropertyChanged(nameof(SelectedRemoteTarget));
    }

    /// <summary>Off-UI-thread scan (WasapiSessionScanner enumerates COM endpoints) + engine-plan
    /// preview (the FIRST PreviewEnginePlan call may shell out to nvidia-smi - cached after; that is
    /// why both run inside Task.Run), then rebuild on the resumed context. Driven by LiveViewWindow's
    /// EXISTING 2 s visible-only poll + on-show + DropDownOpened refreshes, so the ready card's
    /// pre-flight line and engine chip stay fresh until Start with no new timer (design 2026-07-13
    /// section 5 items 4-5). Informational only - never gates Start. Best-effort - a scan hiccup
    /// must never disturb the console.</summary>
    public async Task RefreshRemoteTargetsAsync()
    {
        try
        {
            var (active, plan) = await Task.Run(() => (_scanner.Scan(), Session.PreviewEnginePlan));
            RebuildRemoteTargetOptions(active);
            PreflightSummary = PreflightLine(active, _remoteOverride.Apply(_settings.Current).Remote);
            EngineSummary = SessionViewModel.FormatEngineChip(plan);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"RefreshRemoteTargetsAsync failed: {ex}");
        }
    }

    /// <summary>The picker's selection handler (design 2026-07-12 section 4). Idempotent. While
    /// Recording a switch TO system mix is confirm-gated; the pick then applies the override and
    /// hot-swaps the remote leg, reverting the selection if the build fails. Idle/Paused apply the
    /// override only (Start/Resume adopt it).</summary>
    private async Task ChangeRemoteTargetAsync(RemoteTargetOption? option)
    {
        if (option is null || option == _selectedRemoteTarget) return;
        bool live = Session.State == SessionState.Recording;
        if (live && option.IsSystemMix && !_confirmSystemMix())
        {
            OnPropertyChanged(nameof(SelectedRemoteTarget));   // snap the ComboBox back to the current pick
            return;
        }
        var previous = _selectedRemoteTarget;
        SelectedRemoteTarget = option;                          // sets field + override
        if (live && !await Session.SwitchRemoteTargetAsync(option.Setting))
            SelectedRemoteTarget = previous;                    // build failed: revert
    }

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

    private void OnSessionChanged(object? _, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionViewModel.State)) return;
        if (Session.State == SessionState.Idle)
        {
            SeedSelectedFromSettings();      // next session reverts to the saved default
            _pickedMatterIds.Clear();
            _matterSelection.MatterIds = [];
            _micOverride.Override = null;
            _selectedMic = BuildSelectedFromSettings();
            OnPropertyChanged(nameof(SelectedMic));
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
            // Reseed only an UNTOUCHED selector (still equal to the option matching the old default),
            // so a user's in-flight per-session pick is never clobbered by a background save. A switch
            // to SystemMix always reseeds: any armed app override MUST be dropped (SystemMix has no
            // per-app target), exactly as the old free-text selector forced "" on SystemMix.
            var oldOption = OptionFor(oldSettings.Remote);
            if (newSettings.Remote.Mode == RemoteMode.SystemMix || _selectedRemoteTarget == oldOption)
                SeedSelectedFromSettings();

            if (_micOverride.Override is null)
            {
                _selectedMic = BuildSelectedFromSettings();
                OnPropertyChanged(nameof(SelectedMic));
            }
        });

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        Session.PropertyChanged -= OnSessionChanged;
    }
}
