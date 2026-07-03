// src/LocalScribe.App/ViewModels/MattersPageViewModel.cs
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
namespace LocalScribe.App.ViewModels;

/// <summary>One row of the selected matter's tagged-sessions sublist (design 4.1, the
/// two-level organizer's second level).</summary>
public sealed record TaggedSessionItem(string SessionId, string Title, string DateDisplay);

/// <summary>Matters page: CRUD + roster editor + tagged-sessions organizer (design section 4).
/// WPF-free; every disk mutation routes through MaintenanceService (design 7.3). Roster edits
/// never touch sessions and never cascade - session participants are snapshots (design 4.4).</summary>
public sealed partial class MattersPageViewModel : ObservableObject
{
    private readonly StoragePaths _paths;
    private readonly MaintenanceService _maintenance;
    private readonly MatterDeleter _deleter;
    private readonly IUiErrorReporter _reporter;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;
    private MattersIndex _index = new();
    private Matter? _loaded;                        // detail truth as last loaded/saved

    public ObservableCollection<MattersIndexEntry> Matters { get; } = new();
    public ObservableCollection<RosterMember> Roster { get; } = new();
    public ObservableCollection<TaggedSessionItem> TaggedSessions { get; } = new();

    [ObservableProperty] private bool _showArchived;
    [ObservableProperty] private string _newMatterName = "";
    [ObservableProperty] private string? _selectedMatterId;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editReference = "";
    [ObservableProperty] private string _editDescription = "";
    [ObservableProperty] private bool _editArchived;
    [ObservableProperty] private string _newMemberName = "";
    [ObservableProperty] private string _newMemberRole = "";
    [ObservableProperty] private string _cascadeStatus = "";   // "" = no cascade running

    /// <summary>Raised by JumpToSession; MainWindow navigates to the Sessions page and
    /// selects/opens the session.</summary>
    public event Action<string>? JumpToSessionRequested;

    public IAsyncRelayCommand CreateMatterCommand { get; }
    public IAsyncRelayCommand CommitDetailCommand { get; }
    public IAsyncRelayCommand AddMemberCommand { get; }
    public IAsyncRelayCommand DeleteMatterCommand { get; }
    public IAsyncRelayCommand RepairIndexCommand { get; }

    public MattersPageViewModel(StoragePaths paths, MaintenanceService maintenance,
        MatterDeleter deleter, IUiErrorReporter reporter, Action<Action> dispatch, TimeProvider time)
    {
        (_paths, _maintenance, _deleter, _reporter, _dispatch, _time)
            = (paths, maintenance, deleter, reporter, dispatch, time);
        CreateMatterCommand = new AsyncRelayCommand(CreateMatterAsync);
        CommitDetailCommand = new AsyncRelayCommand(CommitDetailAsync);
        AddMemberCommand = new AsyncRelayCommand(AddMemberAsync);
        DeleteMatterCommand = new AsyncRelayCommand(DeleteMatterAsync);
        RepairIndexCommand = new AsyncRelayCommand(RepairIndexAsync);
    }

    partial void OnShowArchivedChanged(bool value) => ApplyFilter();

    public async Task RefreshAsync()
    {
        try
        {
            _index = await new MatterStore(_paths.MattersDir).ListAsync(CancellationToken.None);
            ApplyFilter();
        }
        catch (Exception ex) { _reporter.Report("List matters", ex); }
    }

    private void ApplyFilter() => _dispatch(() =>
    {
        Matters.Clear();
        foreach (var e in _index.Matters
                     .Where(e => ShowArchived || !e.Archived)
                     .OrderBy(e => e.Id, StringComparer.Ordinal))
            Matters.Add(e);
    });

    public async Task SelectAsync(string? matterId)
    {
        SelectedMatterId = matterId;
        if (matterId is null) { _loaded = null; HasSelection = false; return; }
        try
        {
            var loaded = await new MatterStore(_paths.MattersDir).LoadAsync(matterId, CancellationToken.None);
            if (loaded is null) { _loaded = null; HasSelection = false; return; }
            _loaded = loaded;
            var sessions = await _maintenance.ListSessionsAsync(CancellationToken.None);
            _dispatch(() =>
            {
                EditName = loaded.Name;
                EditReference = loaded.Reference ?? "";
                EditDescription = loaded.Description ?? "";
                EditArchived = loaded.Archived;
                Roster.Clear();
                foreach (var m in loaded.Roster) Roster.Add(m);
                TaggedSessions.Clear();
                foreach (var s in sessions.Sessions.Where(s => s.Meta.MatterIds.Contains(matterId)))
                    TaggedSessions.Add(new TaggedSessionItem(s.Id, s.Meta.Title, DateDisplay(s.Session)));
                HasSelection = true;
            });
        }
        catch (Exception ex) { _reporter.Report("Open matter", ex); }
    }

    public void JumpToSession(string sessionId) => JumpToSessionRequested?.Invoke(sessionId);

    // Session-offset date, same fallback chain as SessionWriter (machine zone only pre-v3).
    private static string DateDisplay(SessionRecord session)
    {
        var local = session.UtcOffsetMinutes is int offsetMin
            ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
            : session.StartedAtUtc.ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private async Task CreateMatterAsync()
    {
        try
        {
            string name = NewMatterName.Trim();
            if (name.Length == 0) { _reporter.Info("Matter name is required."); return; }
            var index = await new MatterStore(_paths.MattersDir).ListAsync(CancellationToken.None);
            string id = MatterIdGenerator.Next(index, _paths.MattersDir, _time.GetUtcNow().Year);
            var matter = new Matter { Id = id, Name = name, DateCreatedUtc = _time.GetUtcNow() };
            await _maintenance.SaveMatterAsync(matter, CancellationToken.None);
            NewMatterName = "";
            await RefreshAsync();
            await SelectAsync(id);
        }
        catch (Exception ex) { _reporter.Report("Create matter", ex); }
    }

    private async Task CommitDetailAsync()
    {
        if (_loaded is null) return;
        try
        {
            string name = EditName.Trim();
            if (name.Length == 0)
            {
                _reporter.Info("Matter name is required.");
                EditName = _loaded.Name;                             // revert to loaded truth
                return;
            }
            string? reference = string.IsNullOrWhiteSpace(EditReference) ? null : EditReference.Trim();
            string? description = string.IsNullOrWhiteSpace(EditDescription) ? null : EditDescription;
            // Matter names/references resolve LIVE in session.txt renders (SessionWriter),
            // so those two fields - and only those - trigger the projection cascade AFTER
            // the save (design 4.4). Description/Archived are matter-local.
            bool cascade = name != _loaded.Name || reference != _loaded.Reference;
            bool changed = cascade || description != _loaded.Description || EditArchived != _loaded.Archived;
            if (!changed) return;                                    // auto-save fires on every commit; no-op when clean

            var updated = _loaded with
            {
                Name = name, Reference = reference, Description = description, Archived = EditArchived,
            };
            await _maintenance.SaveMatterAsync(updated, CancellationToken.None);
            _loaded = updated;
            await RefreshAsync();
            if (cascade)
            {
                _dispatch(() => CascadeStatus = "Re-rendering tagged sessions...");
                var progress = new InlineProgress(n => _dispatch(() => CascadeStatus =
                    string.Create(CultureInfo.InvariantCulture, $"Re-rendering tagged sessions... {n} done")));
                await _maintenance.CascadeMatterAsync(updated.Id, progress, CancellationToken.None);
                _dispatch(() => CascadeStatus = "");
            }
        }
        catch (Exception ex) { _reporter.Report("Save matter", ex); }
    }

    /// <summary>Synchronous IProgress: Progress&lt;T&gt; posts to a captured SyncContext, which
    /// is nondeterministic headless - this reports inline and marshals via the dispatch seam.</summary>
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private async Task AddMemberAsync()
    {
        if (_loaded is null) return;
        try
        {
            string name = NewMemberName.Trim();
            if (name.Length == 0) { _reporter.Info("Member name is required."); return; }
            string? role = string.IsNullOrWhiteSpace(NewMemberRole) ? null : NewMemberRole.Trim();
            string id = ParticipantId.Mint(name, _loaded.Roster.Select(m => m.Id).ToList());
            var roster = _loaded.Roster.Append(new RosterMember { Id = id, Name = name, Role = role }).ToList();
            await SaveRosterAsync(_loaded with { Roster = roster });
            _dispatch(() => { NewMemberName = ""; NewMemberRole = ""; });
        }
        catch (Exception ex) { _reporter.Report("Add roster member", ex); }
    }

    public async Task RenameMemberAsync(string memberId, string newName)
    {
        if (_loaded is null) return;
        try
        {
            string name = newName.Trim();
            if (name.Length == 0) { _reporter.Info("Member name is required."); return; }
            if (_loaded.Roster.FirstOrDefault(m => m.Id == memberId) is not { } member || member.Name == name)
                return;                                              // unknown id or unchanged: no write
            var roster = _loaded.Roster.Select(m => m.Id == memberId ? m with { Name = name } : m).ToList();
            await SaveRosterAsync(_loaded with { Roster = roster });
        }
        catch (Exception ex) { _reporter.Report("Rename roster member", ex); }
    }

    public async Task RemoveMemberAsync(string memberId)
    {
        if (_loaded is null) return;
        try
        {
            var roster = _loaded.Roster.Where(m => m.Id != memberId).ToList();
            if (roster.Count == _loaded.Roster.Count) return;
            await SaveRosterAsync(_loaded with { Roster = roster });
        }
        catch (Exception ex) { _reporter.Report("Remove roster member", ex); }
    }

    /// <summary>Roster saves go through the same serialized maintenance save but deliberately
    /// trigger NO cascade (design 4.4): session participants are snapshot copies and no
    /// projection ever reads the roster - renames only change future picks.</summary>
    private async Task SaveRosterAsync(Matter updated)
    {
        await _maintenance.SaveMatterAsync(updated, CancellationToken.None);
        _loaded = updated;
        _dispatch(() =>
        {
            Roster.Clear();
            foreach (var m in updated.Roster) Roster.Add(m);
        });
    }

    private async Task DeleteMatterAsync()
    {
        if (_loaded is null) return;
        string id = _loaded.Id;
        try
        {
            // Organizational-data deletion only (design 4.1): blocked-while-referenced
            // guarantees no session content references the matter, so the evidentiary
            // invariant (whole-session delete as the only session-data deletion) holds.
            int count = await _deleter.CountReferencesAsync(id, CancellationToken.None);
            if (count > 0)
            {
                _reporter.Info(string.Create(CultureInfo.InvariantCulture,
                    $"Cannot delete this matter: {count} session(s) are tagged to it. Archive it instead."));
                return;
            }
            await _deleter.DeleteAsync(id, CancellationToken.None);
            await SelectAsync(null);
            await RefreshAsync();
        }
        catch (Exception ex) { _reporter.Report("Delete matter", ex); }
    }

    private async Task RepairIndexAsync()
    {
        try
        {
            await _maintenance.RebuildIndexAsync(CancellationToken.None);
            await RefreshAsync();
        }
        catch (Exception ex) { _reporter.Report("Repair matters index", ex); }
    }
}
