using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;

namespace LocalScribe.App.ViewModels;

/// <summary>One entry in the Matter filter ComboBox. Id: null = all sessions,
/// SessionsPageViewModel.NoMatterSentinel = untagged sessions, otherwise a matter id.</summary>
public sealed record MatterFilterOption(string? Id, string Label);

/// <summary>Payload for the whole-session delete confirmation dialog (design 3.4). MatterNames
/// are resolved from a fresh matters-index read; dangling ids render raw, matching SessionWriter.</summary>
public sealed record DeleteConfirmation(
    string Title, string DateDisplay, string DurationDisplay, IReadOnlyList<string> MatterNames);

/// <summary>Sessions page (design 3.1/3.2): catalog listing via MaintenanceService, in-memory
/// filtering over a cached full list, deterministic refresh triggers (navigation, RefreshCommand,
/// SessionViewModel.State reaching Idle). WPF-free; UI mutations marshal via the injected dispatch.</summary>
public sealed partial class SessionsPageViewModel : ObservableObject
{
    public const string NoMatterSentinel = "(none)";

    private readonly MaintenanceService _maintenance;
    private readonly SessionViewModel _session;
    private readonly WindowRegistry _registry;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;
    private readonly Action<string> _revealInExplorer;
    private readonly Func<string?>? _retranscribingSessionId;
    private IReadOnlyList<SessionRowViewModel> _all = [];

    // Sessions quick-filter content matching (design 2026-07-13 section 2.2 surface 2). Optional:
    // compositions without an index (existing tests) keep the exact pre-search behavior.
    private readonly SearchIndexService? _searchIndex;
    private readonly int _contentSearchDebounceMs;
    private Dictionary<string, string> _contentMatches = new(StringComparer.Ordinal);   // id -> snippet
    private CancellationTokenSource? _contentSearchCts;

    /// <summary>Test seam: the in-flight debounced content query, if any. Null when no index is
    /// composed or the filter is empty. Public: no InternalsVisibleTo exists in this repo, and
    /// tests call it.</summary>
    public Task? ContentFilterTask { get; private set; }

    // Id -> (Reference, Name) resolved from the matters index on every refresh (Stage 5.3 Task
    // 2). Feeds the filter-dropdown labels here and the per-row matter chips in Task 4; exposed
    // read-only since only this VM's refresh flow may mutate it.
    private readonly Dictionary<string, (string? Reference, string Name)> _matterLookup = new(StringComparer.Ordinal);

    /// <summary>Classic pager over the filtered list (design 2026-07-18 section 1). Rows holds
    /// only the current page; _filtered is the full post-filter list the pager windows over.</summary>
    public PagerViewModel Pager { get; } = new();
    private List<SessionRowViewModel> _filtered = [];

    public ObservableCollection<SessionRowViewModel> Rows { get; } = [];
    public ObservableCollection<MatterFilterOption> MatterFilterOptions { get; } = [];

    /// <summary>Read-only matter-catalog lookup, keyed by matter id (Stage 5.3 Task 2/4).
    /// Reloaded before every RebuildMatterOptions call; an id absent here (matter deleted, tag
    /// lingering) falls back to displaying the raw id.</summary>
    public IReadOnlyDictionary<string, (string? Reference, string Name)> MatterLookup => _matterLookup;

    [ObservableProperty] private SessionRowViewModel? _selectedRow;

    /// <summary>True exactly when a row is selected. Gates the Task 6 action-bar buttons
    /// (IsEnabled binding); OnSelectedRowChanged raises its change notification so the bound
    /// IsEnabled refreshes as selection comes and goes.</summary>
    public bool HasSelection => SelectedRow is not null;

    partial void OnSelectedRowChanged(SessionRowViewModel? value) => OnPropertyChanged(nameof(HasSelection));

    private string _filterText = "";
    [ObservableProperty] private string? _matterFilterId;
    // Stage 5.4 5.3 roll-out: live filter over the matter-filter OPTIONS (the editable
    // ComboBox's text). Narrows MatterFilterOptions only - never the grid; MatterFilterId
    // (single-select) remains the sole grid-filter input.
    [ObservableProperty] private string _matterFilterSearchText = "";
    [ObservableProperty] private bool _showArchived;
    [ObservableProperty] private int _unreadableCount;
    /// <summary>Set by the startup recovery-scan wiring (Task 24, around Task 23's
    /// orchestrator); this VM only exposes it.</summary>
    [ObservableProperty] private bool _isScanning;

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand<SessionRowViewModel> DeleteSessionCommand { get; }

    /// <summary>Raised instead of deleting directly. Contract: the window-layer handler shows a
    /// MODAL confirmation and invokes the Action synchronously (before returning) if the user
    /// confirms; the command then awaits close -> recycle -> refresh, so ExecuteAsync completes
    /// only when the whole flow is done. No subscriber means no delete - never delete silently.</summary>
    public event Action<DeleteConfirmation, Action>? ConfirmDeleteRequested;

    public IAsyncRelayCommand<SessionRowViewModel> ToggleArchiveCommand { get; }
    public IRelayCommand<SessionRowViewModel> RevealInExplorerCommand { get; }
    public IRelayCommand<SessionRowViewModel> OpenReadViewCommand { get; }
    public IRelayCommand<SessionRowViewModel> OpenSessionDetailsCommand { get; }
    public IRelayCommand<SessionRowViewModel> ExportSessionCommand { get; }

    /// <summary>Raised with the session id from the action bar / row context menu's "Export..." item
    /// (design 3.4); the window layer owns the Save-As seam and the ExportDialogViewModel/Window.
    /// Guarded exactly like the delete flow (live-recording, pending-recovery) since the export reads
    /// the session folder off disk.</summary>
    public event Action<string>? ExportRequested;

    /// <summary>Audio import (design 2026-07-13 section 4.4). False when FFmpeg was not found at
    /// startup (FfmpegLocator) - the Import button then stays visible but DISABLED with
    /// ImportTooltip pointing at the fetch script (the Diarizer-helper degrade pattern; nothing
    /// crashes). Fixed for the app's lifetime, like the diarizer path.</summary>
    public bool ImportAvailable { get; }

    public string ImportTooltip => ImportAvailable
        ? "Import an audio file (WAV, FLAC, MP3, M4A, WMA, OGG) as a new session"
        : "Import is unavailable - FFmpeg was not found. " + LocalScribe.Core.Import.FfmpegLocator.MissingMessage;

    public IRelayCommand ImportAudioCommand { get; }

    /// <summary>Raised from the action bar's "Import audio..." button; the window layer owns the
    /// ImportDialog (mirrors ExportRequested). Only raised when ImportAvailable and NO other
    /// engine is in flight - import loads its own Whisper engine, and the one-engine-at-a-time
    /// rule holds in every direction (the RequestExport guard's pattern): not while recording or
    /// a background finalize drains, and not while a re-transcription runs. The reverse direction
    /// (live/re-transcription refusing while an import transcribes) is the App.xaml.cs
    /// ExternalEngineBusy registration in the import wiring task.</summary>
    public event Action? ImportRequested;

    public IRelayCommand<SessionRowViewModel> RetranscribeSessionCommand { get; }

    /// <summary>Raised with the session id from the action bar / row context menu's
    /// "Re-transcribe..." item (design 2026-07-13 section 3.4); the window layer owns the shared
    /// RetranscribeDialog. Guarded like the export flow (live-recording, pending-recovery) plus
    /// the one-run-at-a-time chip state.</summary>
    public event Action<string>? RetranscribeRequested;

    /// <summary>Raised with the session id on row double-click/Open; the window layer owns
    /// creating or re-activating the ReadViewWindow (and registering it in WindowRegistry).</summary>
    public event Action<string>? OpenReadViewRequested;

    /// <summary>Raised with the session id from the context menu's "Open detail" item (Stage
    /// 5.2); the window layer owns creating or re-activating the SessionDetailsWindow. Unlike
    /// OpenReadViewRequested, this is NOT gated on IsPendingRecovery - details editing is allowed
    /// for any row, and the editor's own IsEditable gate handles the in-progress/locked case.</summary>
    public event Action<string>? OpenSessionDetailsRequested;

    /// <summary>revealInExplorer receives the SESSION ID; the composition root maps it to
    /// StoragePaths.SessionDir(id) and shells out, keeping this VM filesystem- and shell-free.</summary>
    public SessionsPageViewModel(MaintenanceService maintenance, SessionViewModel session,
        WindowRegistry registry, IUiErrorReporter errors, Action<Action> dispatch,
        TimeProvider time, Action<string> revealInExplorer,
        Func<string?>? retranscribingSessionId = null, bool importAvailable = false,
        SearchIndexService? searchIndex = null, int contentSearchDebounceMs = 250)
    {
        (_maintenance, _registry, _errors, _dispatch, _time, _revealInExplorer)
            = (maintenance, registry, errors, dispatch, time, revealInExplorer);
        _retranscribingSessionId = retranscribingSessionId;
        (_searchIndex, _contentSearchDebounceMs) = (searchIndex, contentSearchDebounceMs);

        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        _session = session;
        DeleteSessionCommand = new AsyncRelayCommand<SessionRowViewModel>(DeleteSessionAsync);
        ToggleArchiveCommand = new AsyncRelayCommand<SessionRowViewModel>(ToggleArchiveAsync);
        RevealInExplorerCommand = new RelayCommand<SessionRowViewModel>(RevealInExplorer);
        OpenReadViewCommand = new RelayCommand<SessionRowViewModel>(RequestOpenReadView);
        OpenSessionDetailsCommand = new RelayCommand<SessionRowViewModel>(RequestOpenSessionDetails);
        ExportSessionCommand = new RelayCommand<SessionRowViewModel>(RequestExport);
        RetranscribeSessionCommand = new RelayCommand<SessionRowViewModel>(RequestRetranscribe);
        ImportAvailable = importAvailable;
        ImportAudioCommand = new RelayCommand(ImportAudio);
        Pager.Changed += ApplyPage;              // user page/size moves re-slice only

        // 3.1 refresh trigger, upgraded for live auto-update (design 2026-07-12 section 3): landing
        // on Idle means a finalize just began. FinalizingSessionId (set at Stop before the Idle
        // transition) names the just-stopped session, so upsert just that row in place - it appears
        // immediately labeled "Finalizing..." with no scroll jump. Null id = the rare synchronous
        // fault-path Stop that reaches Idle with no background finalize -> fall back to a full
        // LoadAsync so the audio-only row still appears. Execute is fire-and-forget; UpsertRowAsync
        // and LoadAsync both catch everything, so nothing escapes as an unobserved exception.
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SessionViewModel.State) || session.State != SessionState.Idle)
                return;
            string? id = session.FinalizingSessionId;
            if (id is not null) _ = UpsertRowAsync(id);
            else RefreshCommand.Execute(null);
        };
    }

    public Task OnNavigatedToAsync() => LoadAsync();

    /// <summary>Registry passthrough for the window layer and the Task 17 delete flow.</summary>
    public bool IsReadViewOpen(string sessionId) => _registry.IsOpen(sessionId);

    private async Task LoadAsync()
    {
        try
        {
            var ct = CancellationToken.None;
            var result = await _maintenance.ListSessionsAsync(ct);
            // Same ct as the session refresh above (Stage 5.3 Task 2): the matters index is
            // resolved before rows are built below, so each row's matter chips (Task 4) resolve
            // against this refresh's data instead of degrading every chip to the raw id. This read
            // is secondary to the evidentiary session list above: a matters-index fault (corrupt
            // matters.json -> SchemaGuard/JsonException, or a NEWER schema -> SchemaGuard.
            // RejectIfNewer) degrades to an empty index (raw-id chip/filter fallback) rather than
            // dropping the whole session list behind the "Loading sessions" catch below (final-
            // review FIX 1 - a secondary-index fault must never hide the evidentiary session list).
            MattersIndex matters;
            try { matters = await _maintenance.ListMattersAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { matters = new MattersIndex(); _errors.Report("Loading matters", ex); }
            _dispatch(() =>
            {
                // Lookup FIRST (Task 2/4 ordering): row construction below reads MatterLookup
                // while building MatterChips, so the dictionary must already reflect this
                // refresh's matters snapshot before any row is constructed.
                _matterLookup.Clear();
                foreach (var m in matters.Matters) _matterLookup[m.Id] = (m.Reference, m.Name);
                string? finalizingId = _session.FinalizingSessionId;
                string? retranscribingId = _retranscribingSessionId?.Invoke();
                _all = result.Sessions
                    .OrderByDescending(s => s.Session.StartedAtUtc)
                    .ThenByDescending(s => s.Id, StringComparer.Ordinal)
                    .Select(s => new SessionRowViewModel(s, _time, MatterLookup,
                        isFinalizing: s.Id == finalizingId,
                        isRetranscribing: s.Id == retranscribingId))
                    .ToList();
                UnreadableCount = result.UnreadableCount;
                RebuildMatterOptions();
                ApplyFilters();
            });
        }
        catch (Exception ex) { _errors.Report("Loading sessions", ex); }
    }

    /// <summary>Manually implemented (not [ObservableProperty]): the generated setter gates on
    /// value equality, but reassigning the SAME text (e.g. re-submitting an already-empty filter)
    /// must still count as a filter change for the pager (design 2026-07-18 section 1) - Reset/
    /// ApplyFilters/ScheduleContentFilter always run on assignment, unconditionally.</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            OnPropertyChanging();
            _filterText = value;
            OnPropertyChanged();
            Pager.Reset();                         // a filter change always reads from page 1
            ApplyFilters();                        // instant title/metadata pass - unchanged behavior
            ScheduleContentFilter(value);          // debounced index consult (design 2026-07-13 2.2)
        }
    }

    partial void OnMatterFilterIdChanged(string? value) { Pager.Reset(); ApplyFilters(); }
    partial void OnMatterFilterSearchTextChanged(string value) => RebuildMatterOptions();
    partial void OnShowArchivedChanged(bool value) { Pager.Reset(); ApplyFilters(); }

    /// <summary>Recomputes the full filtered list from the cached _all, then re-slices the
    /// current page into Rows (3.2 in-memory filters + design 2026-07-18 pager). Callers that
    /// represent a FILTER CHANGE must Pager.Reset() first; refresh/upsert paths call this
    /// directly so the reader's current page survives (SetTotal clamps if it shrank).</summary>
    private void ApplyFilters()
    {
        var filtered = new List<SessionRowViewModel>();
        foreach (var row in _all)
        {
            // Content-snippet stamping rides the same pass (design 2026-07-13 2.2): matched rows
            // show one snippet line under the title; everything else shows none.
            row.ContentSnippet = _contentMatches.TryGetValue(row.Id, out string? snip) ? snip : null;
            if (PassesFilters(row)) filtered.Add(row);
        }
        _filtered = filtered;
        Pager.SetTotal(_filtered.Count);
        ApplyPage();
    }

    /// <summary>Slices the current page into Rows without a collection Reset (per-index
    /// replace/add/remove), so DataGrid scroll offset survives; selection is re-pointed by id
    /// and clears when the selected session is not on the current page.</summary>
    private void ApplyPage()
    {
        string? keepId = SelectedRow?.Id;
        var target = Pager.Slice(_filtered);
        for (int i = 0; i < target.Count; i++)
        {
            if (i >= Rows.Count) Rows.Add(target[i]);
            else if (!ReferenceEquals(Rows[i], target[i])) Rows[i] = target[i];
        }
        while (Rows.Count > target.Count) Rows.RemoveAt(Rows.Count - 1);
        SelectedRow = Rows.FirstOrDefault(r => r.Id == keepId);
    }

    /// <summary>The active-filter predicate (design 3.2), factored out of ApplyFilters so the
    /// in-place UpsertRowAsync path applies exactly the same rules: archived hidden unless
    /// ShowArchived, Title contains FilterText, and the single-select Matter filter (All / No matter
    /// / a specific id).</summary>
    private bool PassesFilters(SessionRowViewModel row)
    {
        if (!ShowArchived && row.IsArchived) return false;
        if (FilterText.Length > 0
            && !row.Title.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            && !_contentMatches.ContainsKey(row.Id)) return false;      // content match rescues the row
        if (MatterFilterId == NoMatterSentinel) return row.MatterIds.Count == 0;
        if (MatterFilterId is { } matterId && !row.MatterIds.Contains(matterId)) return false;
        return true;
    }

    private void RebuildMatterOptions()
    {
        string? current = MatterFilterId;
        string query = MatterFilterSearchText.Trim();
        MatterFilterOptions.Clear();
        MatterFilterOptions.Add(new MatterFilterOption(null, "All matters"));
        MatterFilterOptions.Add(new MatterFilterOption(NoMatterSentinel, "No matter"));
        foreach (string id in _all.SelectMany(r => r.MatterIds)
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            // The CURRENT selection always stays listed (selected-but-filtered-out is never
            // dropped - Stage 5.4 5.3, mirroring the Session Details picker); everything else
            // must match the search over Name + Reference + Id.
            if (id != current && query.Length > 0 && !MatchesSearch(id, query)) continue;
            MatterFilterOptions.Add(new MatterFilterOption(id, MatterLabel(id)));
        }
        // Sanctioned exception (design 2): this null cascades OnMatterFilterIdChanged ->
        // ApplyFilters -> Rows.Clear() (a Reset), the ONE case UpsertRowAsync's never-Reset
        // guarantee doesn't cover - the active specific-matter filter just lost its last
        // session, so it legitimately falls back to "All matters" and the list changes anyway.
        if (current is not null && MatterFilterOptions.All(o => o.Id != current))
            MatterFilterId = null;   // stale filter (matter no longer tagged anywhere) -> All
        else if (MatterFilterId != current)
            MatterFilterId = current;   // re-assert: a bound ComboBox can null selection on Clear()
    }

    /// <summary>Search over Id plus the looked-up Name/Reference; an id absent from the lookup
    /// (deleted matter, lingering tag) still matches by raw id.</summary>
    private bool MatchesSearch(string id, string query)
    {
        if (id.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        return _matterLookup.TryGetValue(id, out var m)
            && (m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (m.Reference?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    /// <summary>`{id}-{ref} {name}` when a reference is set, else `{id} {name}`; falls back to the
    /// raw id when the matter is absent from the lookup (deleted matter, lingering tag).</summary>
    private string MatterLabel(string id)
    {
        if (_matterLookup.TryGetValue(id, out var m))
            return m.Reference is { Length: > 0 } r ? $"{id}-{r} {m.Name}" : $"{id} {m.Name}";
        return id;
    }

    /// <summary>Flips meta.Archived through the maintenance queue via a read-current-then-write
    /// (SetArchivedAsync), NOT a whole-object overwrite of the stale load-time snapshot - so a
    /// concurrent detail-pane save (e.g. a just-typed Title) is never reverted. Tags are
    /// unchanged, so matter sessionCounts stay put. Never flips Edited/LastEditedAtUtc.</summary>
    private async Task ToggleArchiveAsync(SessionRowViewModel? row)
    {
        if (row is null || row.IsPendingRecovery) return;    // 3.1: pending rows are inert
        try
        {
            await _maintenance.SetArchivedAsync(row.Id, !row.IsArchived, CancellationToken.None);
            await LoadAsync();                               // 3.1: refresh after any edit
        }
        catch (Exception ex) { _errors.Report("Archiving session", ex); }
    }

    private void RevealInExplorer(SessionRowViewModel? row)
    {
        if (row is null) return;
        try { _revealInExplorer(row.Id); }
        catch (Exception ex) { _errors.Report("Opening session folder", ex); }
    }

    private void RequestOpenReadView(SessionRowViewModel? row)
    {
        if (row is null || row.IsPendingRecovery) return;    // 3.1: pending rows are inert
        OpenReadViewRequested?.Invoke(row.Id);
    }

    private void RequestOpenSessionDetails(SessionRowViewModel? row)
    {
        if (row is null) return;                            // details works for pending-recovery rows too
        OpenSessionDetailsRequested?.Invoke(row.Id);
    }

    /// <summary>Guarded exactly like DeleteSessionAsync's up-front checks (design 3.4): a live
    /// recording's folder is still being written, and a pending-recovery row's session.json may not
    /// even exist yet, so both are refused with an actionable Info message instead of racing the
    /// export against in-flight writes.</summary>
    private void RequestExport(SessionRowViewModel? row)
    {
        if (row is null) return;
        if (row.Id == _session.CurrentSessionId
            && _session.State is SessionState.Recording or SessionState.Paused or SessionState.Finalizing)
        {
            _errors.Info("Cannot export: this session is recording. Stop the recording first.");
            return;
        }
        if (row.IsPendingRecovery)
        {
            _errors.Info("Cannot export: this session is still being recovered. Try again once recovery completes.");
            return;
        }
        ExportRequested?.Invoke(row.Id);
    }

    /// <summary>Guarded exactly like RequestExport (design 2026-07-13 section 3.2): a live
    /// recording and a pending-recovery row are refused with an actionable Info; a row already
    /// being re-transcribed is refused (one run at a time). The runner re-checks every guard
    /// against disk truth, so this is UX, not the enforcement.</summary>
    private void RequestRetranscribe(SessionRowViewModel? row)
    {
        if (row is null) return;
        if (row.Id == _session.CurrentSessionId
            && _session.State is SessionState.Recording or SessionState.Paused or SessionState.Finalizing)
        {
            _errors.Info("Cannot re-transcribe: this session is recording. Stop the recording first.");
            return;
        }
        if (row.IsPendingRecovery)
        {
            _errors.Info("Cannot re-transcribe: this session is still being recovered. Try again once recovery completes.");
            return;
        }
        if (row.IsRetranscribing)
        {
            _errors.Info("A re-transcription of this session is already running.");
            return;
        }
        RetranscribeRequested?.Invoke(row.Id);
    }

    private void ImportAudio()
    {
        if (!ImportAvailable) return;
        if (_session.State is SessionState.Recording or SessionState.Paused or SessionState.Finalizing
            || _session.FinalizingSessionId is not null)
        {
            _errors.Info("Cannot import while a recording is in progress. Stop the recording first.");
            return;
        }
        if (_retranscribingSessionId?.Invoke() is not null)
        {
            _errors.Info("Cannot import while a re-transcription is running. Wait for it to finish first.");
            return;
        }
        ImportRequested?.Invoke();
    }

    private async Task DeleteSessionAsync(SessionRowViewModel? row)
    {
        if (row is null) return;

        // Live check FIRST: a live session also has endedAtUtc == null so it would otherwise fall
        // into the pending-recovery message; "stop the recording" is the actionable instruction.
        if (row.Id == _session.CurrentSessionId
            && _session.State is SessionState.Recording or SessionState.Paused or SessionState.Finalizing)
        {
            _errors.Info("Cannot delete: this session is recording. Stop the recording first.");
            return;
        }
        if (row.IsPendingRecovery)
        {
            _errors.Info("Cannot delete: this session is still being recovered. Try again once recovery completes.");
            return;
        }

        var handler = ConfirmDeleteRequested;
        if (handler is null) return;

        // Matter display names come fresh from the index (Task 16's ListMattersAsync passthrough);
        // a read failure degrades to raw ids rather than blocking the confirmation dialog.
        IReadOnlyList<MattersIndexEntry> mattersIndex;
        try { mattersIndex = (await _maintenance.ListMattersAsync(CancellationToken.None)).Matters; }
        catch { mattersIndex = []; }

        var matterNames = row.MatterIds.Select(mid =>
            mattersIndex.FirstOrDefault(m => m.Id == mid) is { } entry
                ? (string.IsNullOrEmpty(entry.Reference) ? entry.Name : $"{entry.Name} ({entry.Reference})")
                : mid).ToList();

        bool confirmed = false;
        handler(new DeleteConfirmation(row.Title, row.DateDisplay, row.DurationDisplay, matterNames),
            () => confirmed = true);
        if (!confirmed) return;

        try
        {
            // Order is load-bearing (design 3.4): close any open read views FIRST so their audio
            // file handles are released, or the shell recycle can fail on a sharing violation.
            // DeleteSessionAsync reads the CURRENT tags under its gate for the index decrement -
            // the stale row.MatterIds snapshot is not passed (it can drift from an editor re-tag).
            _registry.CloseAllFor(row.Id);
            await _maintenance.DeleteSessionAsync(row.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _errors.Report("Delete session", ex);
        }
        await RefreshCommand.ExecuteAsync(null);   // refresh even after a failure - show disk truth
    }

    /// <summary>Targeted single-row refresh after an out-of-band edit (Session Details Save, Stage
    /// 5.4 Task 3). Reloads just this session from disk, builds a FRESH immutable SessionRowViewModel
    /// (rows never mutate - a refresh replaces the object), swaps it into the cached full list, then
    /// rebuilds the matter-filter options and re-applies filters. ApplyFilters rebuilds Rows from
    /// _all and re-selects by id, so the current selection survives even when another row is
    /// selected. Falls back to a full LoadAsync when the session is gone from disk or was never in
    /// the cached list (e.g. a brand-new folder). Catches everything: the App.xaml.cs Saved/Closed
    /// wiring is fire-and-forget, so a stray refresh must never surface as an unobserved exception.</summary>
    public async Task RefreshRowAsync(string sessionId)
    {
        try
        {
            var item = await _maintenance.LoadSessionItemAsync(sessionId, CancellationToken.None);
            _dispatch(() =>
            {
                var list = _all.ToList();
                int i = list.FindIndex(r => r.Id == sessionId);
                if (item is null || i < 0) { _ = LoadAsync(); return; }   // gone / not cached -> full reload
                list[i] = new SessionRowViewModel(item, _time, MatterLookup,
                    isFinalizing: sessionId == _session.FinalizingSessionId,
                    isRetranscribing: sessionId == _retranscribingSessionId?.Invoke());
                _all = list;
                RebuildMatterOptions();
                ApplyFilters();                                            // rebuilds Rows + re-selects by id
            });
        }
        catch (Exception ex) { _errors.Report("Refreshing session", ex); }
    }

    /// <summary>Non-disruptive single-row upsert (design 2026-07-12 section 2): reloads one session
    /// from disk and reflects it into Rows WITHOUT a collection Reset, so the DataGrid keeps its
    /// scroll offset and selection - ApplyPage's per-index sync (Replace/Add/Remove) carries that
    /// guarantee now that slicing is pager-windowed. Replaces an existing row in place, inserts a
    /// brand-new row at the correct newest-first position in the cached _all list, or drops a
    /// vanished session; ApplyFilters then keeps the reader's current page (SetTotal clamps if it
    /// shrank). Rebuilds the matter-filter options afterward (touches only MatterFilterOptions).
    /// Marshals every UI mutation through _dispatch and catches everything: the wiring (State->Idle,
    /// SessionFinalizeCompleted, per-recovery) is fire-and-forget, so a stray upsert must never
    /// escape as an unobserved exception.</summary>
    public async Task UpsertRowAsync(string sessionId)
    {
        try
        {
            var item = await _maintenance.LoadSessionItemAsync(sessionId, CancellationToken.None);
            _dispatch(() =>
            {
                if (item is null)
                {
                    // Vanished session: drop from the cache; ApplyFilters re-slices in place.
                    _all = _all.Where(r => r.Id != sessionId).ToList();
                    RebuildMatterOptions();
                    ApplyFilters();
                    return;
                }
                var newRow = new SessionRowViewModel(item, _time, MatterLookup,
                    isFinalizing: sessionId == _session.FinalizingSessionId,
                    isRetranscribing: sessionId == _retranscribingSessionId?.Invoke());
                newRow.ContentSnippet = _contentMatches.TryGetValue(sessionId, out string? snip) ? snip : null;
                var list = _all.ToList();
                int i = list.FindIndex(r => r.Id == sessionId);
                if (i >= 0) list[i] = newRow;
                else list.Insert(SortedInsertIndex(list, newRow), newRow);
                _all = list;
                RebuildMatterOptions();
                ApplyFilters();                 // keeps the current page (SetTotal clamps)
            });
        }
        catch (Exception ex) { _errors.Report("Updating session", ex); }
    }

    /// <summary>Newest-first insert index into a sorted _all copy - identical order to LoadAsync's
    /// OrderByDescending(StartedAtUtc).ThenByDescending(Id, Ordinal).</summary>
    private static int SortedInsertIndex(List<SessionRowViewModel> sorted, SessionRowViewModel row)
    {
        int pos = sorted.FindIndex(r => CompareNewestFirst(row, r) < 0);
        return pos < 0 ? sorted.Count : pos;
    }

    private static int CompareNewestFirst(SessionRowViewModel a, SessionRowViewModel b)
    {
        int byDate = b.StartedAtUtc.CompareTo(a.StartedAtUtc);
        return byDate != 0 ? byDate : string.CompareOrdinal(b.Id, a.Id);
    }

    /// <summary>Debounced content consult of the search index (design 2026-07-13 section 2.2
    /// surface 2, ~250 ms). Each keystroke supersedes the previous query (CTS reset); an empty
    /// filter clears the match set immediately. The query itself runs off-thread (Task.Run) and
    /// marshals its result through _dispatch; PassesFilters then ORs the match set into the same
    /// filter pass, so title/metadata filtering behavior is unchanged. Stale matches from the
    /// previous text may keep a row visible for one debounce interval - replaced, never additive.</summary>
    private void ScheduleContentFilter(string filterText)
    {
        if (_searchIndex is null) return;                  // compositions without an index: old behavior
        _contentSearchCts?.Cancel();
        var cts = _contentSearchCts = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(filterText))
        {
            ContentFilterTask = null;
            if (_contentMatches.Count == 0) return;
            _contentMatches = new(StringComparer.Ordinal);
            Pager.Reset();
            ApplyFilters();
            return;
        }
        ContentFilterTask = RunContentFilterAsync(filterText, cts.Token);
    }

    private async Task RunContentFilterAsync(string filterText, CancellationToken ct)
    {
        try
        {
            // Math.Max(..., 1): a real (Timer-backed) minimum delay, not a raw
            // `if (_contentSearchDebounceMs > 0) await Task.Delay(...)` skip. Tests pass 0 to mean
            // "don't wait ~250ms" - NOT "let the query race the caller's own thread". A skipped
            // delay lets Task.Run's worker (with no intervening yield) sometimes finish - and run
            // this method's _dispatch/ApplyFilters tail inline on that worker thread - BEFORE the
            // property setter that scheduled it returns to its caller, once the ThreadPool is warm
            // (verified empirically: deterministic once a prior test's Task.Run round trips leave a
            // spin-waiting worker ready). A 1ms real timer closes that window; production behavior
            // at the real ~250ms debounce is unchanged (Math.Max(250, 1) == 250).
            await Task.Delay(Math.Max(_contentSearchDebounceMs, 1), ct);
            var results = await Task.Run(() => _searchIndex!.Query(new SearchQuery(filterText)), ct);
            if (ct.IsCancellationRequested) return;
            _dispatch(() =>
            {
                if (ct.IsCancellationRequested) return;    // a newer keystroke superseded this query
                _contentMatches = results.ToDictionary(r => r.Session.SessionId,
                    r => r.Hits.Count > 0 ? FormatContentSnippet(r.Hits[0]) : "",
                    StringComparer.Ordinal);
                Pager.Reset();
                ApplyFilters();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _errors.Report("Searching sessions", ex); }
    }

    /// <summary>"Speaker: ...snippet..." plus the original-text label when the hit lives only in
    /// the machine original (design 2.1: corrections never hide content from search).</summary>
    private static string FormatContentSnippet(LocalScribe.Core.Search.SearchHit hit)
        => (hit.Speaker.Length > 0 ? hit.Speaker + ": " : "") + hit.Snippet
           + (hit.MatchesOriginalOnly ? " (matches original text)" : "");
}
