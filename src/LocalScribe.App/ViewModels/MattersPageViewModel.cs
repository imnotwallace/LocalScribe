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
    private readonly MaintenanceService _maintenance;
    private readonly MatterDeleter _deleter;
    private readonly WindowRegistry _windows;
    private readonly IUiErrorReporter _reporter;
    private readonly Action<Action> _dispatch;
    private MattersIndex _index = new();
    private Matter? _loaded;                        // detail truth as last loaded/saved

    public ObservableCollection<MattersIndexEntry> Matters { get; } = new();
    public ObservableCollection<RosterMember> Roster { get; } = new();
    public ObservableCollection<TaggedSessionItem> TaggedSessions { get; } = new();

    /// <summary>Per-matter custom-vocabulary editor (Stage 6.2). Each add/remove saves the matter
    /// (via the same gated SaveMatterAsync the roster uses) and updates _loaded; it deliberately
    /// does NOT cascade - existing tagged sessions keep their current corrections until the
    /// "Re-render tagged sessions" button runs (mirrors the roster/description no-cascade rule).</summary>
    public VocabularyEditorViewModel Vocabulary { get; }

    [ObservableProperty] private bool _showArchived;
    // Stage 5.4 5.3 roll-out: live filter over the left matter list (Name + Reference + Id,
    // OrdinalIgnoreCase Contains), composing with ShowArchived. Display-only, never a save.
    [ObservableProperty] private string _searchText = "";
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

    /// <summary>Raised by the tagged-session "Open" (JumpToSession); App opens the Session
    /// Details window for this session id (design 5.2: Matters' "Open" reuses the same
    /// details window as Sessions, rather than the read view).</summary>
    public event Action<string>? OpenSessionDetailsRequested;

    /// <summary>Raised after an untag actually removed a tag on disk (design 5.4 grid
    /// coherence): App.xaml.cs routes this to SessionsPageViewModel.RefreshRowAsync so the
    /// Sessions grid's matter chips for that row update without a manual refresh (mirrors the
    /// Session Details Saved wiring). Never raised on the guard-refused or already-untagged
    /// no-op paths - the event means "disk changed".</summary>
    public event Action<string>? SessionUntagged;

    public IAsyncRelayCommand CreateMatterCommand { get; }
    public IAsyncRelayCommand CommitDetailCommand { get; }
    public IAsyncRelayCommand AddMemberCommand { get; }
    public IAsyncRelayCommand DeleteMatterCommand { get; }
    public IAsyncRelayCommand RepairIndexCommand { get; }

    public MattersPageViewModel(MaintenanceService maintenance, MatterDeleter deleter,
        WindowRegistry windows, IUiErrorReporter reporter, Action<Action> dispatch)
    {
        (_maintenance, _deleter, _windows, _reporter, _dispatch)
            = (maintenance, deleter, windows, reporter, dispatch);
        CreateMatterCommand = new AsyncRelayCommand(CreateMatterAsync);
        CommitDetailCommand = new AsyncRelayCommand(CommitDetailAsync);
        AddMemberCommand = new AsyncRelayCommand(AddMemberAsync);
        DeleteMatterCommand = new AsyncRelayCommand(DeleteMatterAsync);
        RepairIndexCommand = new AsyncRelayCommand(RepairIndexAsync);
        Vocabulary = new VocabularyEditorViewModel(SaveMatterVocabularyAsync, _reporter);
    }

    partial void OnShowArchivedChanged(bool value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    public async Task RefreshAsync()
    {
        try
        {
            // Gated read (design 4.3/7.3): racing the raw MatterStore against the gated matters.json
            // writers (startup rebuild, editor-save tag deltas) risks an AtomicFile File.Move sharing
            // violation / a lost update. Every consumer uses the maintenance passthrough.
            _index = await _maintenance.ListMattersAsync(CancellationToken.None);
            ApplyFilter();
        }
        catch (Exception ex) { _reporter.Report("List matters", ex); }
    }

    private void ApplyFilter() => _dispatch(() =>
    {
        string query = SearchText.Trim();
        Matters.Clear();
        foreach (var e in _index.Matters
                     .Where(e => ShowArchived || !e.Archived)
                     .Where(e => query.Length == 0 || MatterSearch.Matches(e, query))
                     .OrderBy(e => e.Id, StringComparer.Ordinal))
            Matters.Add(e);
    });

    public async Task SelectAsync(string? matterId)
    {
        SelectedMatterId = matterId;
        if (matterId is null) { _loaded = null; HasSelection = false; return; }
        try
        {
            // Gated load (design 4.3): also serializes the v1->v2 write-migration LoadAsync may
            // perform against every other index writer, instead of racing them on matters.json.tmp.
            var loaded = await _maintenance.LoadMatterAsync(matterId, CancellationToken.None);
            if (loaded is null) { _loaded = null; HasSelection = false; return; }
            _loaded = loaded;
            var sessions = await _maintenance.ListSessionsAsync(CancellationToken.None);
            _dispatch(() =>
            {
                EditName = loaded.Name;
                EditReference = loaded.Reference ?? "";
                EditDescription = loaded.Description ?? "";
                EditArchived = loaded.Archived;
                Vocabulary.Load(loaded.Vocabulary);
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

    /// <summary>Tagged-session "Open" entry point: opens the Session Details window by id
    /// (no longer navigates to the Sessions page - design 5.2).</summary>
    public void JumpToSession(string sessionId) => OpenSessionDetailsRequested?.Invoke(sessionId);

    /// <summary>Click-time untag guard (resolved decision 10.2): blocked while ANY window is
    /// open for the session. WindowRegistry does not distinguish window kinds (Session Details,
    /// read view, and Split-speakers all register), so this is a deliberately conservative
    /// superset of the spec's "Session Details open" case - the details window buffers unsaved
    /// tag edits an untag would clobber, and over-blocking the others is harmless and rare.
    /// Not a per-row binding: TaggedSessionItem is immutable and the registry has no change
    /// event, so a bound CanUntag would freeze at SelectAsync time.</summary>
    public bool CanUntag(string sessionId) => !_windows.IsOpen(sessionId);

    /// <summary>Untag the given session from the SELECTED matter (design 5.4, concern (8)) - the
    /// missing inverse of the Session Details tag path. Loads the session FRESH from disk so the
    /// previousMatterIds snapshot is on-disk truth (a stale TaggedSessionItem can never corrupt
    /// the index's -1 SessionCount delta); no-ops when the tag is already gone (double-click, or
    /// an editor save raced us); then refreshes the matter list and reselects so both the card's
    /// "N session(s)" count and the Tagged-sessions sublist update. Organizational only:
    /// meta.json is the ONLY file written, via MaintenanceService (transcript/audio untouched -
    /// evidentiary firewall holds). Guarded by CanUntag (design 5.4/decision 10.2): refused while
    /// any window is open for the session, reported via Info, no write attempted. Error-reported,
    /// never throws.</summary>
    public async Task UntagSessionAsync(string sessionId)
    {
        if (SelectedMatterId is not string matterId) return;
        if (!CanUntag(sessionId))
        {
            _reporter.Info("This session is open in another window (Session Details or read view). Close it first, then untag.");
            return;
        }
        try
        {
            var item = await _maintenance.LoadSessionItemAsync(sessionId, CancellationToken.None);
            if (item is null)                                    // session deleted underneath us:
            {                                                    // nothing to write, just re-sync
                await RefreshAsync();
                await SelectAsync(matterId);
                return;
            }
            var previous = item.Meta.MatterIds;
            if (!previous.Contains(matterId, StringComparer.Ordinal))
            {
                // Already untagged: write nothing, apply NO delta - re-sync the stale sublist only.
                await RefreshAsync();
                await SelectAsync(matterId);
                return;
            }
            var updated = item.Meta with
            {
                MatterIds = previous
                    .Where(id => !string.Equals(id, matterId, StringComparison.Ordinal)).ToList(),
            };
            // SaveMetaAsync applies the tag delta against the fresh on-disk previous set,
            // so the index decrement is exactly -1 for this matter (design 5.4).
            await _maintenance.SaveMetaAsync(sessionId, updated, previous, CancellationToken.None);
            SessionUntagged?.Invoke(sessionId);
            await RefreshAsync();
            await SelectAsync(matterId);
        }
        catch (Exception ex) { _reporter.Report("Untag session", ex); }
    }

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
            // Mint + save atomically under _indexGate (design 4.2): a rapid double-invoke cannot
            // read the same index twice and duplicate an M-YYYY-NNN id.
            var matter = await _maintenance.CreateMatterAsync(name, CancellationToken.None);
            NewMatterName = "";
            await RefreshAsync();
            await SelectAsync(matter.Id);
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

    /// <summary>Vocabulary persist callback (Stage 6.2): saves the edited Vocabulary onto the
    /// loaded matter via the same gated SaveMatterAsync the roster/detail commits use.
    /// Deliberately does NOT cascade - vocabulary is index-invisible, so a vocab-only save never
    /// changes tagged sessions' rendered projections until RerenderTaggedAsync runs explicitly.</summary>
    private async Task SaveMatterVocabularyAsync(Vocabulary vocab, CancellationToken ct)
    {
        if (_loaded is null) return;
        var updated = _loaded with { Vocabulary = vocab };
        await _maintenance.SaveMatterAsync(updated, ct);
        _loaded = updated;
    }

    private bool _rerendering;

    /// <summary>Explicit re-render of every session tagged with the selected matter, so a
    /// vocabulary change reaches already-recorded transcripts. Reuses CascadeMatterAsync + the
    /// CascadeStatus inline-progress the name/reference cascade uses; a busy guard blocks a
    /// concurrent run (the shared CascadeStatus field is owned by one cascade at a time).</summary>
    public async Task RerenderTaggedAsync()
    {
        if (_loaded is null || _rerendering) return;
        _rerendering = true;
        try
        {
            _dispatch(() => CascadeStatus = "Re-rendering tagged sessions...");
            var progress = new InlineProgress(n => _dispatch(() => CascadeStatus =
                string.Create(CultureInfo.InvariantCulture, $"Re-rendering tagged sessions... {n} done")));
            await _maintenance.CascadeMatterAsync(_loaded.Id, progress, CancellationToken.None);
        }
        catch (Exception ex) { _reporter.Report("Re-rendering tagged sessions", ex); }
        finally
        {
            _rerendering = false;
            _dispatch(() => CascadeStatus = "");
        }
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
            // Gated delete (design 4.3): the matters.json index removal runs under _indexGate,
            // not the bare deleter that races every other index writer on matters.json.tmp.
            await _maintenance.DeleteMatterAsync(id, CancellationToken.None);
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
