using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.App.ViewModels;

public sealed record MatterOption(string Id, string Display, bool Archived, bool IsSelected);
public sealed record RosterPick(string MatterId, string MemberId, string Display);
public sealed record MediumOption(Medium Value, string Display);

/// <summary>Immutable list row over a session-participant snapshot. Carrying the whole record
/// (not just the five display fields) keeps reserved fields like ClusterKey lossless when the
/// editor rebuilds meta.Participants from these rows.</summary>
public sealed class ParticipantRow
{
    public ParticipantRow(SessionParticipant snapshot) => Snapshot = snapshot;
    public SessionParticipant Snapshot { get; }
    public string Id => Snapshot.Id;
    public string Name => Snapshot.Name;
    public SourceKind Side => Snapshot.Side;
    public string? Role => Snapshot.Role;
    public bool IsSelf => Snapshot.IsSelf;
    public string SideDisplay => Side == SourceKind.Local ? "Local" : "Remote";
}

/// <summary>The Sessions page's detail-pane editor (design 3.3). Edits meta.json ONLY, via
/// MaintenanceService.SaveMetaAsync; auto-saves on every committed change (no Save button);
/// NEVER flips Edited/LastEditedAtUtc (they flow through from the last-saved meta untouched);
/// locks for the live session and for rows awaiting recovery. WPF-free; timers are Tick().</summary>
public sealed partial class MetadataEditorViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SavedIndicatorDuration = TimeSpan.FromSeconds(2);

    private readonly MaintenanceService _maintenance;
    private readonly SessionViewModel _session;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;

    private SessionRowViewModel? _row;
    private SessionMeta _savedMeta = new();                 // last successfully saved copy (revert target)
    private IReadOnlyList<MattersIndexEntry> _matterEntries = [];
    private readonly List<string> _selectedMatterIds = new();
    // Tag set as of the last SUCCESSFUL save, per session - SaveMetaAsync computes its
    // incremental sessionCount delta against this (design 3.3). Guarded by its own lock:
    // Attach runs on the UI thread, PersistAsync on the save chain.
    private readonly Dictionary<string, string[]> _lastSavedTags = new(StringComparer.Ordinal);
    // Last meta WE saved, keyed by ROW OBJECT. A list reload mints a FRESH row (absent here), so
    // its Item.Meta - which reflects external writes like the archive action - wins on Attach; the
    // SAME row object re-attached after our save is stale on disk, so our saved copy wins. This
    // stops a re-Attach re-seeding the revert base from the possibly stale listing snapshot.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SessionRowViewModel, SessionMeta> _savedByRow = new();
    private Task _saveChain = Task.CompletedTask;           // serializes saves; deltas stay ordered
    private long _saveSeq;           // most recently QueueSave-enqueued position (see PersistAsync doc)
    private DateTimeOffset _savedIndicatorUntil;
    private bool _loading;
    private bool _disposed;

    public static IReadOnlyList<MediumOption> MediumOptions { get; } =
        Enum.GetValues<Medium>()
            .Select(m => new MediumOption(m, m == Medium.InPerson ? "In-person" : m.ToString()))
            .ToArray();

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private Medium _selectedMedium = Medium.Other;
    [ObservableProperty] private int _localCount = 1;
    [ObservableProperty] private int _remoteCount = 1;
    [ObservableProperty] private bool _archived;
    [ObservableProperty] private bool _showArchivedMatters;
    [ObservableProperty] private bool _savedIndicator;
    [ObservableProperty] private bool _isEditable;
    [ObservableProperty] private string _lockHint = "";
    [ObservableProperty] private RosterPick? _selectedRosterPick;
    // Per-side free-text add for the Session Details window's two-column speaker manager.
    [ObservableProperty] private string _newLocalName = "";
    [ObservableProperty] private string _newRemoteName = "";
    // Stage 5.2 Task 7: speaker counts DERIVE from the two side lists by default; a manual override
    // (system-mix: declared speaker count != number of named participants) keeps LocalCount/
    // RemoteCount fully typed. UI-only per-editor state - not persisted, never set during load.
    [ObservableProperty] private bool _countsFollowLists = true;

    public ObservableCollection<MatterOption> MatterOptions { get; } = new();
    public ObservableCollection<MatterOption> TaggedMatters { get; } = new();
    public ObservableCollection<RosterPick> RosterPicks { get; } = new();
    public ObservableCollection<ParticipantRow> Participants { get; } = new();
    // Filtered views of Participants by Side (Task 6) - rebuilt wholesale by RebuildSideLists
    // whenever Participants changes, so they never drift from the source of truth.
    public ObservableCollection<ParticipantRow> LocalParticipants { get; } = new();
    public ObservableCollection<ParticipantRow> RemoteParticipants { get; } = new();

    /// <summary>Inverse of CountsFollowLists for the Session Details window's count TextBoxes'
    /// IsEnabled binding (editable only under the manual/system-mix override). Avoids a value
    /// converter - the app declares only BooleanToVisibilityConverter. Its change is raised from
    /// OnCountsFollowListsChanged.</summary>
    public bool CountsAreManual => !CountsFollowLists;

    public IRelayCommand<MatterOption> ToggleMatterCommand { get; }
    public IRelayCommand<ParticipantRow> RemoveParticipantCommand { get; }
    public IRelayCommand AddLocalNameCommand { get; }
    public IRelayCommand AddRemoteNameCommand { get; }
    // Task 7: per-side ROSTER add (each column's button stamps its own Side).
    public IAsyncRelayCommand AddLocalFromRosterCommand { get; }
    public IAsyncRelayCommand AddRemoteFromRosterCommand { get; }
    // Stage 5.3 Task 7: Split speakers relocates here from the Sessions-list context menu (the
    // dead Sessions-page DiariseCommand/DiariseRequested/RequestDiarise were removed from
    // SessionsPageViewModel in this same task). G7 requires the button to DISABLE (not just
    // no-op) for a pending/in-progress row, so this is a real CanExecute gate, not just an
    // early-return - see CanDiarise/RequestDiarise below.
    public IRelayCommand DiariseCommand { get; }

    /// <summary>Raised with the session id when the Speakers section's "Split speakers..."
    /// button is invoked on a finalized (non-pending) row; the window layer (App.xaml.cs'
    /// openSessionDetails factory) owns constructing the SplitSpeakersViewModel/Window.</summary>
    public event Action<string>? DiariseRequested;

    /// <summary>Raised with the session id on a SETTLED successful persist (Stage 5.4 4.4): the
    /// current fields are now on meta.json AND nothing newer is queued behind this save (the same
    /// seq == _saveSeq gate that lights SavedIndicator). The Sessions grid subscribes to refresh
    /// just this row (App.xaml.cs). Phase 1 fires from the auto-save success continuation; Phase 2
    /// will fire from the explicit Save commit with identical grid-side wiring.</summary>
    public event Action<string>? Saved;

    public MetadataEditorViewModel(MaintenanceService maintenance, SessionViewModel session,
        IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time)
    {
        (_maintenance, _session, _errors, _dispatch, _time)
            = (maintenance, session, errors, dispatch, time);

        ToggleMatterCommand = new RelayCommand<MatterOption>(ToggleMatter);
        RemoveParticipantCommand = new RelayCommand<ParticipantRow>(r => { if (r is not null) Remove(r); });
        // Task 6: per-side add reuses AddFreeText(name, side) verbatim (same id-mint/auto-save/
        // error-handling) - only the fixed Side and which textbox gets cleared differ.
        AddLocalNameCommand = new RelayCommand(() => { AddFreeText(NewLocalName, SourceKind.Local); NewLocalName = ""; });
        AddRemoteNameCommand = new RelayCommand(() => { AddFreeText(NewRemoteName, SourceKind.Remote); NewRemoteName = ""; });
        // Task 7: per-side roster add stamps the column's Side (fixes the Remote-hardcoded roster
        // add). SelectedRosterPick is a single shared selection - each column's button picks its side.
        AddLocalFromRosterCommand = new AsyncRelayCommand(
            () => SelectedRosterPick is { } p ? AddFromRoster(p.MatterId, p.MemberId, SourceKind.Local) : Task.CompletedTask);
        AddRemoteFromRosterCommand = new AsyncRelayCommand(
            () => SelectedRosterPick is { } p ? AddFromRoster(p.MatterId, p.MemberId, SourceKind.Remote) : Task.CompletedTask);
        // Task 7: real CanExecute gate (not just an early-return) so the button DISABLES for a
        // pending/in-progress row (G7) instead of staying enabled-but-no-op.
        DiariseCommand = new RelayCommand(RequestDiarise, CanDiarise);
        // Keeps LocalParticipants/RemoteParticipants as filtered views of Participants: any add,
        // remove, or reload (LoadFieldsFromSaved's Clear+refill) fires this.
        Participants.CollectionChanged += (_, _) => RebuildSideLists();

        // SessionViewModel raises State changes already marshaled through ITS dispatch
        // (SessionViewModel.cs:56-62), so this handler runs on the UI thread. Named (not a
        // lambda) so Dispose can detach it - _session is long-lived and shared, so an
        // undetached subscription would root every per-window editor that ever attaches here
        // (Stage 5.2 Task 4's SessionDetailsWindow factory mints one per open).
        session.PropertyChanged += OnSessionPropertyChanged;
    }

    private void OnSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SessionViewModel.State)) RecomputeEditable(); }

    /// <summary>Detaches the _session.PropertyChanged subscription taken in the ctor - the only
    /// external-object subscription this VM makes. Without this, every SessionDetailsWindow's
    /// editor opened-then-closed would stay rooted by the shared, app-lifetime SessionViewModel
    /// (unbounded leak). Idempotent - a second Dispose() is a safe no-op.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.PropertyChanged -= OnSessionPropertyChanged;
    }

    /// <summary>Bound to SessionsPageViewModel.SelectedRow by the page code-behind. Loads the
    /// editable copy from the row's meta snapshot; null detaches and disables the pane.</summary>
    public void Attach(SessionRowViewModel? row)
    {
        _row = row;
        _loading = true;
        try
        {
            // Prefer the meta WE last saved for THIS row object over its (possibly stale) listing
            // snapshot; a fresh row from a list reload is absent from the cache and uses disk truth.
            _savedMeta = row is null ? new SessionMeta()
                : _savedByRow.TryGetValue(row, out var lastSaved) ? lastSaved
                : row.Item.Meta;
            if (row is not null)
            {
                lock (_lastSavedTags)
                {
                    // Only seed on first sight: a re-attach must keep the delta base at the
                    // last SAVE, not the possibly stale listing snapshot.
                    if (!_lastSavedTags.ContainsKey(row.Id))
                        _lastSavedTags[row.Id] = _savedMeta.MatterIds.ToArray();
                }
            }
            LoadFieldsFromSaved();
            // Belt-and-braces: LoadFieldsFromSaved's Clear+refill above already drove this via the
            // CollectionChanged subscription, but call it once more explicitly so a freshly loaded
            // session's two-column split is guaranteed correct regardless of subscription ordering.
            // MUST run HERE, inside the try (under _loading==true), so the Task 7 count derivation
            // in RebuildSideLists is SKIPPED on load - a load keeps the persisted (DECLARED) counts;
            // only post-load edits re-derive. It depends only on Participants (already repopulated
            // by LoadFieldsFromSaved), not on RefreshMatterDataAsync/RosterPicks, so this earlier
            // placement is safe. (Running it after the finally would re-derive on load and could
            // silently overwrite a declared count that exceeds the number of named speakers.)
            RebuildSideLists();
            // Task 7 (fix wave 1): the counts-follow toggle MIRRORS whether the loaded data already
            // matches the lists. A leg whose DECLARED count exceeds its named speakers (system-mix)
            // opens with follow OFF, so a later reopen+edit never silently re-derives that declared
            // count down to the named count. Global (single) toggle: if EITHER side mismatches it
            // goes OFF and BOTH declared counts are protected until the user re-ticks the box. Set
            // ONLY for a loaded session (row != null); for detach (row == null) the pane is disabled
            // and the lists are empty, so leave the default. Runs under _loading==true, so the
            // OnCountsFollowListsChanged -> RebuildSideLists it may trigger does NOT re-derive (the
            // !_loading guard holds) and does NOT persist - a harmless list-only rebuild.
            if (row is not null)
                CountsFollowLists = LocalCount == LocalParticipants.Count
                                 && RemoteCount == RemoteParticipants.Count;
        }
        finally { _loading = false; }
        SavedIndicator = false;
        RecomputeEditable();
        if (row is not null) _ = RefreshMatterDataAsync();
        else { MatterOptions.Clear(); TaggedMatters.Clear(); RosterPicks.Clear(); }
        // Task 7: refresh the Split-speakers gate for the newly attached (or detached) row -
        // Attach(null) disables it, a pending row disables it, a finalized row enables it.
        DiariseCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Split-speakers gate (Task 7/G7): a pending/in-progress row - same condition the
    /// retired Sessions-list RequestDiarise used - or no attached row at all disables the button.</summary>
    private bool CanDiarise() => _row is not null && !_row.IsPendingRecovery;

    /// <summary>Belt-and-braces early return in addition to the CanExecute gate above - defends
    /// against a stale command invocation racing an Attach that just disabled it.</summary>
    private void RequestDiarise()
    {
        if (_row is null || _row.IsPendingRecovery) return;
        DiariseRequested?.Invoke(_row.Id);
    }

    /// <summary>Id-first entry point for the Session Details window (Stage 5.2). Loads the session
    /// item by id and Attaches a freshly-built row, preserving the row-identity model Attach relies
    /// on (CWT revert-base + live-lock checks). A missing session detaches (disables the pane).
    /// LoadSessionItemAsync THROWS on a present-but-corrupt session.json (Task 1's locked contract:
    /// a corrupt evidentiary record must stay distinguishable from a deleted one) - this method is
    /// awaited from the details window's Loaded handler, so a load failure is reported and the pane
    /// detached here instead of becoming an unhandled dispatcher exception. Cancellation is not
    /// caught: it propagates like SessionCatalog.ListAsync's own exception filter.</summary>
    public async Task LoadAsync(string sessionId, CancellationToken ct)
    {
        SessionListItem? item;
        try { item = await _maintenance.LoadSessionItemAsync(sessionId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _dispatch(() => { _errors.Report("Loading session details", ex); Attach(null); });
            return;
        }
        _dispatch(() => Attach(item is null ? null : new SessionRowViewModel(item, _time)));
    }

    /// <summary>Driven by a ~250 ms DispatcherTimer in production; tests call it directly.</summary>
    public void Tick()
    {
        if (SavedIndicator && _time.GetUtcNow() >= _savedIndicatorUntil) SavedIndicator = false;
    }

    /// <summary>Roster pick COPIES the member's id and name into the session snapshot -
    /// provenance only, never a live link (design 3.3). Remote is just the DEFAULT here; the
    /// Session Details window's per-side commands call AddFromRoster(..., side) directly to stamp
    /// Local or Remote. Kept as the public Remote-default entry point (exercised by tests);
    /// remove-and-re-add still corrects a wrong side.</summary>
    public Task AddFromRosterAsync(string matterId, string rosterMemberId)
        => AddFromRoster(matterId, rosterMemberId, SourceKind.Remote);

    /// <summary>Per-side roster add: identical COPY-into-snapshot semantics to the legacy
    /// Remote-only path, but stamps the caller's Side. Fixes the "everything is remote" bug where
    /// roster-add hard-coded SourceKind.Remote (Task 6 made free-text per-side but left roster-add
    /// flat). Private - the only public entry is AddFromRosterAsync (Remote) + the two commands.</summary>
    private async Task AddFromRoster(string matterId, string rosterMemberId, SourceKind side)
    {
        try
        {
            var matter = await _maintenance.LoadMatterAsync(matterId, CancellationToken.None);
            var member = matter?.Roster.FirstOrDefault(m => m.Id == rosterMemberId);
            if (member is null)
            { _dispatch(() => _errors.Info("That roster member no longer exists.")); return; }
            _dispatch(() =>
            {
                if (Participants.Any(p => p.Id == member.Id))
                { _errors.Info($"{member.Name} is already a participant."); return; }
                Participants.Add(new ParticipantRow(new SessionParticipant
                { Id = member.Id, Name = member.Name, Side = side, Role = member.Role }));
                QueueSave();
            });
        }
        catch (Exception ex) { _dispatch(() => _errors.Report("Adding participant", ex)); }
    }

    /// <summary>Free-text participant: id minted against THIS session's participant ids
    /// (session-scoped, design 4.2).</summary>
    public void AddFreeText(string name, SourceKind side)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        string id = ParticipantId.Mint(trimmed, Participants.Select(p => p.Id).ToArray());
        Participants.Add(new ParticipantRow(new SessionParticipant
        { Id = id, Name = trimmed, Side = side }));
        QueueSave();
    }

    public void Remove(ParticipantRow row)
    {
        if (Participants.Remove(row)) QueueSave();
    }

    /// <summary>Rebuilds LocalParticipants/RemoteParticipants wholesale from Participants,
    /// split by Side (Task 6's two-column speaker manager). A full clear+refill (not incremental
    /// diffing) is simplest given the list is a handful of people per session, and keeps each
    /// side's order identical to Participants' own order. Task 7: after the split, DERIVE
    /// LocalCount/RemoteCount from the list sizes when CountsFollowLists (default). The two guards
    /// are evidentiary-critical - see inline.</summary>
    private void RebuildSideLists()
    {
        LocalParticipants.Clear();
        RemoteParticipants.Clear();
        foreach (var p in Participants)
            (p.Side == SourceKind.Local ? LocalParticipants : RemoteParticipants).Add(p);
        // Derivation guards:
        //  - !_loading: a session LOAD keeps its persisted (DECLARED) meta counts - Attach runs
        //    this under _loading==true, so loading never re-derives; only post-load user edits
        //    (CollectionChanged) or a CountsFollowLists toggle re-derive.
        //  - Count > 0: an EMPTY side keeps its current/loaded count (never forced to 0). This
        //    preserves the count==1 NameResolver tier-2 precondition (one named local speaker ->
        //    LocalCount==1) AND never silently lowers a DECLARED count that exceeds the number of
        //    named speakers (the system-mix / ForcedClusterCount evidentiary case).
        if (CountsFollowLists && !_loading)
        {
            if (LocalParticipants.Count > 0) LocalCount = LocalParticipants.Count;
            if (RemoteParticipants.Count > 0) RemoteCount = RemoteParticipants.Count;
        }
    }

    partial void OnTitleChanged(string value) => QueueSave();
    partial void OnDescriptionChanged(string value) => QueueSave();
    partial void OnSelectedMediumChanged(Medium value) => QueueSave();
    partial void OnArchivedChanged(bool value) => QueueSave();
    partial void OnLocalCountChanged(int value)
    { if (value < 1) { LocalCount = 1; return; } QueueSave(); }
    partial void OnRemoteCountChanged(int value)
    { if (value < 1) { RemoteCount = 1; return; } QueueSave(); }
    // Task 7: toggling ON immediately re-derives from the lists; OFF is a harmless list-only
    // rebuild (the derivation self-guards on CountsFollowLists). CountsFollowLists is UI-only
    // state, never set during load, so this never fires under _loading. Also raises CountsAreManual
    // for the window's count-TextBox IsEnabled binding.
    partial void OnCountsFollowListsChanged(bool value)
    {
        OnPropertyChanged(nameof(CountsAreManual));
        RebuildSideLists();
    }
    // Display-only filter (design 4.1: non-persisted, per-pane UI state) - never a save.
    partial void OnShowArchivedMattersChanged(bool value) => RebuildMatterOptions();

    private void ToggleMatter(MatterOption? option)
    {
        if (option is null || _loading) return;
        if (!_selectedMatterIds.Remove(option.Id)) _selectedMatterIds.Add(option.Id);
        RebuildMatterOptions();
        QueueSave();
        _ = RefreshMatterDataAsync();                       // roster picks follow the tagged set
    }

    /// <summary>Snapshot the fields on the caller (UI) thread, then append the write to the
    /// save chain. The chain serializes saves so each SaveMetaAsync sees the delta base left
    /// by the previous one; per-session ordering on disk is Task 9's single-flight queue.
    /// _saveSeq stamps this save's place in the queue so PersistAsync can tell whether a NEWER
    /// edit was queued behind it (see PersistAsync doc) before it lights the indicator.</summary>
    private void QueueSave()
    {
        if (_loading || _row is null || !IsEditable) return;
        var row = _row;
        var meta = BuildMeta();
        long seq = ++_saveSeq;
        _saveChain = _saveChain.ContinueWith(_ => PersistAsync(row, meta, seq),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private SessionMeta BuildMeta() => _savedMeta with
    {
        // Edited / LastEditedAtUtc / Summary* flow through from the last-saved meta UNTOUCHED:
        // metadata saves never flip the correction flags (design 3.3, evidentiary invariant).
        Title = Title,
        Description = Description,
        Medium = SelectedMedium,
        MatterIds = _selectedMatterIds.ToArray(),
        Participants = Participants.Select(p => p.Snapshot).ToArray(),
        LocalCount = LocalCount,
        RemoteCount = RemoteCount,
        Archived = Archived,
    };

    /// <summary>Persists one queued snapshot. seq is this save's QueueSave-time position; the
    /// chain (Task.ContinueWith above) guarantees saves run and complete strictly in enqueue
    /// order, but a save further down the SAME chain can already have been enqueued (though not
    /// yet finished) by the time THIS one's disk write completes - e.g. two edits committed back
    /// to back before either save reaches disk. Lighting SavedIndicator on every intermediate
    /// completion would let an observer see "Saved" while a newer edit is still mid-flight (the
    /// disk briefly holds a stale snapshot). Only flip the indicator when seq is still the most
    /// recently enqueued one - i.e. nothing newer is queued behind this save - so "Saved" always
    /// means the CURRENT fields are the ones on disk, not just "a save happened".</summary>
    private async Task PersistAsync(SessionRowViewModel row, SessionMeta meta, long seq)
    {
        string[] previous;
        lock (_lastSavedTags)
            previous = _lastSavedTags.TryGetValue(row.Id, out var p) ? p : [];
        try
        {
            await _maintenance.SaveMetaAsync(row.Id, meta, previous, CancellationToken.None)
                .ConfigureAwait(false);
            lock (_lastSavedTags) _lastSavedTags[row.Id] = meta.MatterIds.ToArray();
            _dispatch(() =>
            {
                // Remember what we wrote for THIS row object even if the user moved on, so a
                // later re-Attach to it re-seeds from disk truth, not the stale listing snapshot.
                _savedByRow.AddOrUpdate(row, meta);
                if (!ReferenceEquals(_row, row)) return;    // user moved on mid-save
                _savedMeta = meta;
                if (seq != Interlocked.Read(ref _saveSeq)) return;   // a newer edit is queued behind this one
                SavedIndicator = true;
                _savedIndicatorUntil = _time.GetUtcNow() + SavedIndicatorDuration;
                Saved?.Invoke(row.Id);                      // Stage 5.4 4.4: settled-save grid refresh hook
            });
        }
        catch (Exception ex)
        {
            _dispatch(() =>
            {
                _errors.Report("Saving session details", ex);
                if (ReferenceEquals(_row, row)) RevertToSaved();
            });
        }
    }

    private void RevertToSaved()
    {
        _loading = true;
        try { LoadFieldsFromSaved(); }
        finally { _loading = false; }
        SavedIndicator = false;
    }

    private void LoadFieldsFromSaved()
    {
        Title = _savedMeta.Title;
        Description = _savedMeta.Description;
        SelectedMedium = _savedMeta.Medium;
        LocalCount = _savedMeta.LocalCount;
        RemoteCount = _savedMeta.RemoteCount;
        Archived = _savedMeta.Archived;
        _selectedMatterIds.Clear();
        _selectedMatterIds.AddRange(_savedMeta.MatterIds);
        Participants.Clear();
        foreach (var p in _savedMeta.Participants) Participants.Add(new ParticipantRow(p));
        RebuildMatterOptions();
    }

    private void RecomputeEditable()
    {
        bool liveLocked = _row is not null
            && _row.Id == _session.CurrentSessionId
            && _session.State is SessionState.Recording or SessionState.Paused or SessionState.Finalizing;
        IsEditable = _row is not null && !_row.IsPendingRecovery && !liveLocked;
        LockHint = _row is null || IsEditable ? ""
            : _row.IsPendingRecovery ? "Available after recovery completes."
            : "Available when recording stops.";
    }

    private async Task RefreshMatterDataAsync()
    {
        try
        {
            var index = await _maintenance.ListMattersAsync(CancellationToken.None);
            // Roster picks: union of the TAGGED matters' rosters (design 3.3).
            var picks = new List<RosterPick>();
            foreach (string mid in _selectedMatterIds.ToArray())
            {
                var matter = await _maintenance.LoadMatterAsync(mid, CancellationToken.None);
                if (matter is null) continue;
                foreach (var member in matter.Roster)
                    picks.Add(new RosterPick(mid, member.Id, $"{member.Name} ({matter.Name})"));
            }
            _dispatch(() =>
            {
                _matterEntries = index.Matters;
                RebuildMatterOptions();
                RosterPicks.Clear();
                foreach (var p in picks) RosterPicks.Add(p);
            });
        }
        catch (Exception ex) { _dispatch(() => _errors.Report("Loading matters", ex)); }
    }

    private void RebuildMatterOptions()
    {
        MatterOptions.Clear();
        TaggedMatters.Clear();
        foreach (var e in _matterEntries)
        {
            bool selected = _selectedMatterIds.Contains(e.Id);
            string display = string.IsNullOrEmpty(e.Reference) ? e.Name : $"{e.Name} ({e.Reference})";
            if (e.Archived) display += " (archived)";
            var option = new MatterOption(e.Id, display, e.Archived, selected);
            // Archived matters are OFFERED only under the toggle (design 4.1); a hidden
            // selected tag stays tagged - _selectedMatterIds is the truth, not this list.
            if (!e.Archived || ShowArchivedMatters) MatterOptions.Add(option);
            if (selected) TaggedMatters.Add(option);
        }
    }
}
