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

/// <summary>The Session Details editor (Stage 5.4 5.1). Edits meta.json ONLY, via
/// MaintenanceService.SaveMetaAsync; BUFFERS every edit in the VM's working copy and persists
/// only on the explicit SaveCommand (DiscardCommand reverts to the last-saved baseline);
/// NEVER flips Edited/LastEditedAtUtc (they flow through from the last-saved meta untouched);
/// locks for the live session and for rows awaiting recovery. WPF-free.</summary>
public sealed partial class MetadataEditorViewModel : ObservableObject, IDisposable
{
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
    // Attach runs on the UI thread, SaveAsync on its own awaited continuation.
    private readonly Dictionary<string, string[]> _lastSavedTags = new(StringComparer.Ordinal);
    // Last meta WE saved, keyed by ROW OBJECT. A list reload mints a FRESH row (absent here), so
    // its Item.Meta - which reflects external writes like the archive action - wins on Attach; the
    // SAME row object re-attached after our save is stale on disk, so our saved copy wins. This
    // stops a re-Attach re-seeding the revert base from the possibly stale listing snapshot.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SessionRowViewModel, SessionMeta> _savedByRow = new();
    // Bumped on every MarkDirty; SaveAsync snapshots it so an edit landing DURING the awaited
    // disk write keeps the editor dirty (the only race left now that saves are explicit and
    // AsyncRelayCommand refuses concurrent executions - the Phase 1 _saveChain/_saveSeq queue
    // machinery is retired with the auto-save it serialized).
    private long _dirtyGen;
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
    // Stage 5.4 5.1: true whenever the working copy differs from the last commit. Drives the
    // persistent "Unsaved changes" indicator and both commands' CanExecute gates.
    [ObservableProperty] private bool _isDirty;
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

    // Stage 5.4 5.1: the explicit-commit pair. SaveCommand is the ONLY disk write this editor
    // performs; DiscardCommand reverts to the last-saved baseline. Both are dead (greyed) when
    // clean; Save additionally requires the IsEditable lock to be open.
    public IAsyncRelayCommand SaveCommand { get; }
    public IRelayCommand DiscardCommand { get; }

    /// <summary>Raised with the session id when the Speakers section's "Split speakers..."
    /// button is invoked on a finalized (non-pending) row; the window layer (App.xaml.cs'
    /// openSessionDetails factory) owns constructing the SplitSpeakersViewModel/Window.</summary>
    public event Action<string>? DiariseRequested;

    /// <summary>Raised with the session id when an EXPLICIT Save commits successfully (Stage
    /// 5.4 5.1): the working copy is now on meta.json. The Sessions grid subscribes to refresh
    /// just this row (App.xaml.cs) - wiring is unchanged from the Phase 1 auto-save-settle
    /// source, only the trigger moved to the explicit commit.</summary>
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
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => IsDirty && IsEditable);
        DiscardCommand = new RelayCommand(Discard, () => IsDirty);
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
        IsDirty = false;                                    // a fresh attach starts clean
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
                MarkDirty();
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
        MarkDirty();
    }

    public void Remove(ParticipantRow row)
    {
        if (Participants.Remove(row)) MarkDirty();
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

    partial void OnTitleChanged(string value) => MarkDirty();
    partial void OnDescriptionChanged(string value) => MarkDirty();
    partial void OnSelectedMediumChanged(Medium value) => MarkDirty();
    partial void OnArchivedChanged(bool value) => MarkDirty();
    partial void OnLocalCountChanged(int value)
    { if (value < 1) { LocalCount = 1; return; } MarkDirty(); }
    partial void OnRemoteCountChanged(int value)
    { if (value < 1) { RemoteCount = 1; return; } MarkDirty(); }
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
        MarkDirty();
        _ = RefreshMatterDataAsync();                       // roster picks follow the tagged set
    }

    /// <summary>Replaces the retired QueueSave in EVERY field/collection/tag change path
    /// (Stage 5.4 5.1): edits buffer in the VM's working copy and only SaveCommand writes
    /// disk. Same guards QueueSave had - a load-time repopulation, no attached row, and the
    /// IsEditable lock (live-recording / pending-recovery) never dirty the editor.</summary>
    private void MarkDirty()
    {
        if (_loading || _row is null || !IsEditable) return;
        _dirtyGen++;
        IsDirty = true;
    }

    partial void OnIsDirtyChanged(bool value)
    {
        SaveCommand.NotifyCanExecuteChanged();
        DiscardCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsEditableChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

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

    /// <summary>Explicit commit (Stage 5.4 5.1): projects the working copy through BuildMeta -
    /// Edited/LastEditedAtUtc/Summary* flow through UNTOUCHED - and writes ONCE via
    /// MaintenanceService.SaveMetaAsync with the matter-tag delta computed against
    /// _lastSavedTags at commit time. At most one save is ever in flight (AsyncRelayCommand's
    /// default disallows concurrent execution), so there is no queue to serialize; _dirtyGen
    /// covers the one residual race - an edit landing mid-write keeps the editor dirty. On
    /// failure the edits are KEPT (still dirty, retry or Discard): the old auto-save rollback
    /// would now destroy work the user explicitly asked to persist.</summary>
    private async Task SaveAsync()
    {
        if (_row is null || !IsEditable || !IsDirty) return;
        var row = _row;
        var meta = BuildMeta();
        long gen = _dirtyGen;
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
                // later re-Attach re-seeds from what we saved, not the stale listing snapshot.
                _savedByRow.AddOrUpdate(row, meta);
                if (!ReferenceEquals(_row, row)) return;    // user moved on mid-save
                _savedMeta = meta;
                if (gen == _dirtyGen) IsDirty = false;      // an edit during the write stays dirty
                Saved?.Invoke(row.Id);                      // explicit-commit grid refresh hook
            });
        }
        catch (Exception ex)
        {
            _dispatch(() => _errors.Report("Saving session details", ex));
        }
    }

    /// <summary>Explicit revert (Stage 5.4 5.1): reloads every field/collection from
    /// _savedMeta (the last successful commit, or the load snapshot) and clears the dirty
    /// flag. RevertToSaved repopulates under _loading, so the reload cannot re-mark dirty.</summary>
    private void Discard()
    {
        RevertToSaved();
        IsDirty = false;
    }

    private void RevertToSaved()
    {
        _loading = true;
        try { LoadFieldsFromSaved(); }
        finally { _loading = false; }
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
