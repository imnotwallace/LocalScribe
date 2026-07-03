using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Live;

namespace LocalScribe.App.ViewModels;

/// <summary>One entry in the Matter filter ComboBox. Id: null = all sessions,
/// SessionsPageViewModel.NoMatterSentinel = untagged sessions, otherwise a matter id.</summary>
public sealed record MatterFilterOption(string? Id, string Label);

/// <summary>Sessions page (design 3.1/3.2): catalog listing via MaintenanceService, in-memory
/// filtering over a cached full list, deterministic refresh triggers (navigation, RefreshCommand,
/// SessionViewModel.State reaching Idle). WPF-free; UI mutations marshal via the injected dispatch.</summary>
public sealed partial class SessionsPageViewModel : ObservableObject
{
    public const string NoMatterSentinel = "(none)";

    private readonly MaintenanceService _maintenance;
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
    public IAsyncRelayCommand<SessionRowViewModel> ToggleArchiveCommand { get; }
    public IRelayCommand<SessionRowViewModel> RevealInExplorerCommand { get; }
    public IRelayCommand<SessionRowViewModel> OpenReadViewCommand { get; }

    /// <summary>Raised with the session id on row double-click/Open; the window layer owns
    /// creating or re-activating the ReadViewWindow (and registering it in WindowRegistry).</summary>
    public event Action<string>? OpenReadViewRequested;

    /// <summary>revealInExplorer receives the SESSION ID; the composition root maps it to
    /// StoragePaths.SessionDir(id) and shells out, keeping this VM filesystem- and shell-free.</summary>
    public SessionsPageViewModel(MaintenanceService maintenance, SessionViewModel session,
        WindowRegistry registry, IUiErrorReporter errors, Action<Action> dispatch,
        TimeProvider time, Action<string> revealInExplorer)
    {
        (_maintenance, _registry, _errors, _dispatch, _time, _revealInExplorer)
            = (maintenance, registry, errors, dispatch, time, revealInExplorer);

        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        ToggleArchiveCommand = new AsyncRelayCommand<SessionRowViewModel>(ToggleArchiveAsync);
        RevealInExplorerCommand = new RelayCommand<SessionRowViewModel>(RevealInExplorer);
        OpenReadViewCommand = new RelayCommand<SessionRowViewModel>(RequestOpenReadView);

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

    /// <summary>Flips meta.Archived through the maintenance queue. previousMatterIds = current
    /// (tags unchanged, so matter sessionCounts stay put). Never flips Edited/LastEditedAtUtc.</summary>
    private async Task ToggleArchiveAsync(SessionRowViewModel? row)
    {
        if (row is null || row.IsPendingRecovery) return;    // 3.1: pending rows are inert
        try
        {
            var updated = row.Item.Meta with { Archived = !row.Item.Meta.Archived };
            await _maintenance.SaveMetaAsync(row.Id, updated, row.Item.Meta.MatterIds,
                CancellationToken.None);
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
}
