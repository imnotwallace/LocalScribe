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

    public ObservableCollection<SessionRowViewModel> Rows { get; } = [];
    public ObservableCollection<MatterFilterOption> MatterFilterOptions { get; } = [];

    [ObservableProperty] private SessionRowViewModel? _selectedRow;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private string? _matterFilterId;
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
    public IRelayCommand<SessionRowViewModel> DiariseCommand { get; }

    /// <summary>Raised with the session id on row double-click/Open; the window layer owns
    /// creating or re-activating the ReadViewWindow (and registering it in WindowRegistry).</summary>
    public event Action<string>? OpenReadViewRequested;

    /// <summary>Raised with the session id from the context menu's "Split speakers..." item; the
    /// window layer owns constructing the SplitSpeakersViewModel/Window (mirrors
    /// OpenReadViewRequested). The Diarised badge (SessionRowViewModel.IsDiarised) lights on its
    /// own once SaveDiarisationAsync flips session.Diarised and the next refresh re-lists it -
    /// nothing here needs to force a refresh.</summary>
    public event Action<string>? DiariseRequested;

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
        DiariseCommand = new RelayCommand<SessionRowViewModel>(RequestDiarise);

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
            var result = await _maintenance.ListSessionsAsync(CancellationToken.None);
            var rows = result.Sessions
                .OrderByDescending(s => s.Session.StartedAtUtc)
                .ThenByDescending(s => s.Id, StringComparer.Ordinal)
                .Select(s => new SessionRowViewModel(s, _time))
                .ToList();
            _dispatch(() =>
            {
                _all = rows;
                UnreadableCount = result.UnreadableCount;
                RebuildMatterOptions();
                ApplyFilters();
            });
        }
        catch (Exception ex) { _errors.Report("Loading sessions", ex); }
    }

    partial void OnFilterTextChanged(string value) => ApplyFilters();
    partial void OnMatterFilterIdChanged(string? value) => ApplyFilters();
    partial void OnShowArchivedChanged(bool value) => ApplyFilters();

    /// <summary>Recomputes Rows from the cached full list (3.2: in-memory filters only).</summary>
    private void ApplyFilters()
    {
        string? keepId = SelectedRow?.Id;
        Rows.Clear();
        foreach (var row in _all)
        {
            if (!ShowArchived && row.IsArchived) continue;
            if (FilterText.Length > 0
                && !row.Title.Contains(FilterText, StringComparison.OrdinalIgnoreCase)) continue;
            if (MatterFilterId == NoMatterSentinel)
            {
                if (row.MatterIds.Count > 0) continue;
            }
            else if (MatterFilterId is { } matterId && !row.MatterIds.Contains(matterId))
            {
                continue;
            }
            Rows.Add(row);
        }
        SelectedRow = Rows.FirstOrDefault(r => r.Id == keepId);
    }

    private void RebuildMatterOptions()
    {
        string? current = MatterFilterId;
        MatterFilterOptions.Clear();
        MatterFilterOptions.Add(new MatterFilterOption(null, "All matters"));
        MatterFilterOptions.Add(new MatterFilterOption(NoMatterSentinel, "No matter"));
        foreach (string id in _all.SelectMany(r => r.MatterIds)
                     .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            MatterFilterOptions.Add(new MatterFilterOption(id, id));
        if (current is not null && MatterFilterOptions.All(o => o.Id != current))
            MatterFilterId = null;   // stale filter (matter no longer tagged anywhere) -> All
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

    private void RequestDiarise(SessionRowViewModel? row)
    {
        if (row is null || row.IsPendingRecovery) return;    // 3.1: pending rows are inert
        DiariseRequested?.Invoke(row.Id);
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
}
