using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;

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
    private IReadOnlyList<SessionRowViewModel> _all = [];

    // Id -> (Reference, Name) resolved from the matters index on every refresh (Stage 5.3 Task
    // 2). Feeds the filter-dropdown labels here and the per-row matter chips in Task 4; exposed
    // read-only since only this VM's refresh flow may mutate it.
    private readonly Dictionary<string, (string? Reference, string Name)> _matterLookup = new(StringComparer.Ordinal);

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

    [ObservableProperty] private string _filterText = "";
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
        TimeProvider time, Action<string> revealInExplorer)
    {
        (_maintenance, _registry, _errors, _dispatch, _time, _revealInExplorer)
            = (maintenance, registry, errors, dispatch, time, revealInExplorer);

        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        _session = session;
        DeleteSessionCommand = new AsyncRelayCommand<SessionRowViewModel>(DeleteSessionAsync);
        ToggleArchiveCommand = new AsyncRelayCommand<SessionRowViewModel>(ToggleArchiveAsync);
        RevealInExplorerCommand = new RelayCommand<SessionRowViewModel>(RevealInExplorer);
        OpenReadViewCommand = new RelayCommand<SessionRowViewModel>(RequestOpenReadView);
        OpenSessionDetailsCommand = new RelayCommand<SessionRowViewModel>(RequestOpenSessionDetails);
        ExportSessionCommand = new RelayCommand<SessionRowViewModel>(RequestExport);

        // 3.1 refresh trigger: State reaching Idle means a finalize just completed and a new
        // folder is on disk. PropertyChanged only fires on actual change, so landing on Idle
        // is exactly the transition of interest. Execute is fire-and-forget; LoadAsync
        // catches everything, so nothing can escape as an unobserved exception.
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionViewModel.State) && session.State == SessionState.Idle)
                RefreshCommand.Execute(null);
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
                _all = result.Sessions
                    .OrderByDescending(s => s.Session.StartedAtUtc)
                    .ThenByDescending(s => s.Id, StringComparer.Ordinal)
                    .Select(s => new SessionRowViewModel(s, _time, MatterLookup,
                        isFinalizing: s.Id == finalizingId))
                    .ToList();
                UnreadableCount = result.UnreadableCount;
                RebuildMatterOptions();
                ApplyFilters();
            });
        }
        catch (Exception ex) { _errors.Report("Loading sessions", ex); }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilters();
    partial void OnMatterFilterIdChanged(string? value) => ApplyFilters();
    partial void OnMatterFilterSearchTextChanged(string value) => RebuildMatterOptions();
    partial void OnShowArchivedChanged(bool value) => ApplyFilters();

    /// <summary>Recomputes Rows from the cached full list (3.2: in-memory filters only). The full
    /// LoadAsync/Refresh path; UpsertRowAsync (design 2026-07-12) avoids this Clear-and-refill so
    /// scroll+selection survive.</summary>
    private void ApplyFilters()
    {
        string? keepId = SelectedRow?.Id;
        Rows.Clear();
        foreach (var row in _all)
            if (PassesFilters(row)) Rows.Add(row);
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
            && !row.Title.Contains(FilterText, StringComparison.OrdinalIgnoreCase)) return false;
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
                    isFinalizing: sessionId == _session.FinalizingSessionId);
                _all = list;
                RebuildMatterOptions();
                ApplyFilters();                                            // rebuilds Rows + re-selects by id
            });
        }
        catch (Exception ex) { _errors.Report("Refreshing session", ex); }
    }

    /// <summary>Non-disruptive single-row upsert (design 2026-07-12 section 2): reloads one session
    /// from disk and reflects it into Rows WITHOUT a collection Reset, so the DataGrid keeps its
    /// scroll offset and selection. Replaces an existing row in place (ObservableCollection Replace),
    /// inserts a brand-new row at the correct newest-first position iff it passes the active filters,
    /// removes a now-filtered/deleted row in place. Rebuilds the matter-filter options afterward
    /// (touches only MatterFilterOptions, never Rows). Marshals every UI mutation through _dispatch
    /// and catches everything: the wiring (State->Idle, SessionFinalizeCompleted, per-recovery) is
    /// fire-and-forget, so a stray upsert must never escape as an unobserved exception.</summary>
    public async Task UpsertRowAsync(string sessionId)
    {
        try
        {
            var item = await _maintenance.LoadSessionItemAsync(sessionId, CancellationToken.None);
            _dispatch(() =>
            {
                if (item is null) { RemoveRowInPlace(sessionId); RebuildMatterOptions(); return; }
                var newRow = new SessionRowViewModel(item, _time, MatterLookup,
                    isFinalizing: sessionId == _session.FinalizingSessionId);
                var list = _all.ToList();
                int i = list.FindIndex(r => r.Id == sessionId);
                if (i >= 0) list[i] = newRow;
                else list.Insert(SortedInsertIndex(list, newRow), newRow);
                _all = list;
                UpsertIntoRows(newRow);
                RebuildMatterOptions();
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

    /// <summary>Reflects a freshly built row into the bound Rows collection in place - Replace when it
    /// is already shown and still passes filters, Remove when it no longer passes, Insert at the
    /// matching sorted position when newly visible. Never Clears (no Reset), so scroll+selection hold;
    /// re-points SelectedRow to the replacement when the upserted row is the selected one.</summary>
    private void UpsertIntoRows(SessionRowViewModel row)
    {
        bool wasSelected = SelectedRow?.Id == row.Id;
        int existing = IndexInRows(row.Id);
        bool passes = PassesFilters(row);
        if (existing >= 0)
        {
            if (passes) Rows[existing] = row;
            else Rows.RemoveAt(existing);
        }
        else if (passes)
        {
            Rows.Insert(RowsInsertPosition(row), row);
        }
        if (wasSelected) SelectedRow = Rows.FirstOrDefault(r => r.Id == row.Id);
    }

    /// <summary>Rows preserves _all's newest-first order, so the insert index is the count of
    /// currently-shown rows that sort before this one in _all (which already contains it).</summary>
    private int RowsInsertPosition(SessionRowViewModel row)
    {
        int idx = 0;
        foreach (var r in _all)
        {
            if (r.Id == row.Id) break;
            if (PassesFilters(r)) idx++;
        }
        return idx;
    }

    private int IndexInRows(string id)
    {
        for (int k = 0; k < Rows.Count; k++)
            if (Rows[k].Id == id) return k;
        return -1;
    }

    /// <summary>A vanished session (LoadSessionItemAsync returned null): drop it from the cache and
    /// the bound list in place, no Reset.</summary>
    private void RemoveRowInPlace(string id)
    {
        if (_all.Any(r => r.Id == id)) _all = _all.Where(r => r.Id != id).ToList();
        int ri = IndexInRows(id);
        if (ri >= 0) Rows.RemoveAt(ri);
    }
}
