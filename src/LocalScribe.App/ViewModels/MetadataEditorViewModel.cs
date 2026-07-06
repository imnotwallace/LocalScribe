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
/// (not just the display fields) keeps reserved fields like ClusterKey lossless when the
/// editor rebuilds meta.Participants from these rows. DisplayLabel is stamped by
/// RebuildSideLists on every wholesale rebuild (unnamed slots number per side, 1-based), so
/// it needs no INPC - the ItemsControls regenerate containers on each Clear+refill.</summary>
public sealed class ParticipantRow
{
    public ParticipantRow(SessionParticipant snapshot) => Snapshot = snapshot;
    public SessionParticipant Snapshot { get; }
    public string Id => Snapshot.Id;
    public string Name => Snapshot.Name;
    public SourceKind Side => Snapshot.Side;
    public string? Role => Snapshot.Role;
    public bool IsSelf => Snapshot.IsSelf;
    public ParticipantKind Kind => Snapshot.Kind;
    public bool IsUnnamed => Snapshot.Kind == ParticipantKind.Unnamed;
    public string SideDisplay => Side == SourceKind.Local ? "Local" : "Remote";
    /// <summary>What the slot row renders: a named slot's Name, or "Speaker N" where N is the
    /// slot's 1-based position among ITS side's unnamed slots. Stamped by RebuildSideLists.</summary>
    public string DisplayLabel { get; internal set; } = "";
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
    // Stage 5.4 5.1 attribution-warning seam, injected like _dispatch: the composition root
    // passes a MessageBox-based Yes/No dialog; tests pass a lambda. WPF stays out of this VM.
    private readonly Func<string, bool> _confirm;

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
    [ObservableProperty] private bool _archived;
    // Stage 5.4 5.3: live filter over the matter RESULTS list (Name + Reference + Id,
    // OrdinalIgnoreCase Contains). Display-only, never a save/MarkDirty. Empty text lists
    // ACTIVE matters only; a non-empty search also REVEALS matching archived matters
    // (suffixed "(archived)") - this replaces the retired ShowArchivedMatters checkbox.
    // _selectedMatterIds stays the selection truth: a tagged matter filtered out of the
    // results is never dropped (it keeps its TaggedMatters chip and survives Save).
    [ObservableProperty] private string _matterSearchText = "";
    // Stage 5.4 5.1: true whenever the working copy differs from the last commit. Drives the
    // persistent "Unsaved changes" indicator and both commands' CanExecute gates.
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _isEditable;
    [ObservableProperty] private string _lockHint = "";
    // Stage 5.4 5.2 (LOCKED): Split speakers reads counts from DISK (SplitSpeakersViewModel
    // loads meta.json), so launching it over a dirty buffer would diarise with STALE counts.
    // LockHint-style message shown beside the button while the gate below disables it.
    [ObservableProperty] private string _diariseHint = "";
    // Stage 5.4 5.2 (C1): INDEPENDENT per-side roster selections. The retired shared
    // SelectedRosterPick made "which column does my pick apply to" ambiguous - each column
    // now owns its selection and its Add button consumes only its own.
    [ObservableProperty] private RosterPick? _localSelectedRosterPick;
    [ObservableProperty] private RosterPick? _remoteSelectedRosterPick;
    // Per-side free-text add for the Session Details window's two-column speaker manager.
    [ObservableProperty] private string _newLocalName = "";
    [ObservableProperty] private string _newRemoteName = "";

    public ObservableCollection<MatterOption> MatterOptions { get; } = new();
    public ObservableCollection<MatterOption> TaggedMatters { get; } = new();
    public ObservableCollection<RosterPick> RosterPicks { get; } = new();
    public ObservableCollection<ParticipantRow> Participants { get; } = new();
    // Filtered views of Participants by Side (Task 6) - rebuilt wholesale by RebuildSideLists
    // whenever Participants changes, so they never drift from the source of truth.
    public ObservableCollection<ParticipantRow> LocalParticipants { get; } = new();
    public ObservableCollection<ParticipantRow> RemoteParticipants { get; } = new();

    public IRelayCommand<MatterOption> ToggleMatterCommand { get; }
    public IRelayCommand<ParticipantRow> RemoveParticipantCommand { get; }
    public IRelayCommand AddLocalNameCommand { get; }
    public IRelayCommand AddRemoteNameCommand { get; }
    // Stage 5.4 5.2 (C1): explicit unnamed slots - the system-mix "more voices than named
    // people" case is expressed as Kind=Unnamed slot rows instead of a raw integer count.
    public IRelayCommand AddLocalUnnamedCommand { get; }
    public IRelayCommand AddRemoteUnnamedCommand { get; }
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
        IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time,
        Func<string, bool> confirm)
    {
        (_maintenance, _session, _errors, _dispatch, _time, _confirm)
            = (maintenance, session, errors, dispatch, time, confirm);

        ToggleMatterCommand = new RelayCommand<MatterOption>(ToggleMatter);
        RemoveParticipantCommand = new RelayCommand<ParticipantRow>(r => { if (r is not null) Remove(r); });
        // Task 6: per-side add reuses AddFreeText(name, side) verbatim (same id-mint/auto-save/
        // error-handling) - only the fixed Side and which textbox gets cleared differ.
        AddLocalNameCommand = new RelayCommand(() => { AddFreeText(NewLocalName, SourceKind.Local); NewLocalName = ""; });
        AddRemoteNameCommand = new RelayCommand(() => { AddFreeText(NewRemoteName, SourceKind.Remote); NewRemoteName = ""; });
        AddLocalUnnamedCommand = new RelayCommand(() => AddUnnamed(SourceKind.Local));
        AddRemoteUnnamedCommand = new RelayCommand(() => AddUnnamed(SourceKind.Remote));
        // Task 7 introduced per-side roster add stamping the column's Side; C1 made the
        // SELECTION itself per-side too (LocalSelectedRosterPick/RemoteSelectedRosterPick),
        // so each column's Add consumes only its own pick.
        AddLocalFromRosterCommand = new AsyncRelayCommand(
            () => LocalSelectedRosterPick is { } p ? AddFromRoster(p.MatterId, p.MemberId, SourceKind.Local) : Task.CompletedTask);
        AddRemoteFromRosterCommand = new AsyncRelayCommand(
            () => RemoteSelectedRosterPick is { } p ? AddFromRoster(p.MatterId, p.MemberId, SourceKind.Remote) : Task.CompletedTask);
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
            // session's two-column split (and DisplayLabel stamping) is guaranteed correct
            // regardless of subscription ordering. It depends only on Participants (already
            // repopulated by LoadFieldsFromSaved), not on RefreshMatterDataAsync/RosterPicks, so
            // running it here, inside the try, is safe.
            RebuildSideLists();
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

    /// <summary>Split-speakers gate (Task 7/G7 + Stage 5.4 5.2): a pending/in-progress row, no
    /// attached row, or a DIRTY buffer disables the button - SplitSpeakersViewModel reads the
    /// per-side counts from disk, so it must only launch over a saved (clean) editor.</summary>
    private bool CanDiarise() => _row is not null && !_row.IsPendingRecovery && !IsDirty;

    /// <summary>Belt-and-braces early return in addition to the CanExecute gate above - defends
    /// against a stale command invocation racing an Attach or an edit that just disabled it.</summary>
    private void RequestDiarise()
    {
        if (_row is null || _row.IsPendingRecovery || IsDirty) return;
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

    /// <summary>Appends an explicit unnamed speaker slot (Stage 5.4 5.2): Kind=Unnamed, empty
    /// Name, session-scoped id minted against THIS session's participant ids (same minting as
    /// free-text adds at AddFreeText). Buffered - persists only on the explicit Save; the
    /// Snapshot round-trips Kind and ClusterKey losslessly through BuildMeta.</summary>
    public void AddUnnamed(SourceKind side)
    {
        AppendUnnamedSlot(side);
        MarkDirty();
    }

    /// <summary>Shared mint+construct for an Unnamed slot, factored out of AddUnnamed and the
    /// lazy-migration SynthesizeUnnamedSlots (below LoadFieldsFromSaved) - the two callers
    /// differ ONLY in whether the append also dirties the editor: AddUnnamed is a live user
    /// edit and marks dirty itself right after calling this; SynthesizeUnnamedSlots runs only
    /// under _loading and must never dirty, so it does not.</summary>
    private void AppendUnnamedSlot(SourceKind side)
    {
        string id = ParticipantId.Mint("Unnamed Speaker", Participants.Select(p => p.Id).ToArray());
        Participants.Add(new ParticipantRow(new SessionParticipant
        { Id = id, Name = "", Side = side, Kind = ParticipantKind.Unnamed }));
    }

    public void Remove(ParticipantRow row)
    {
        if (Participants.Remove(row)) MarkDirty();
    }

    /// <summary>Rebuilds LocalParticipants/RemoteParticipants wholesale from Participants,
    /// split by Side (Task 6's two-column speaker manager). A full clear+refill (not incremental
    /// diffing) is simplest given the list is a handful of people per session, and keeps each
    /// side's order identical to Participants' own order. Also stamps each row's DisplayLabel
    /// (a named row shows its Name; an unnamed row shows "Speaker N", numbered per side) - see
    /// ParticipantRow.DisplayLabel. Stage 5.4 5.2 (C1) retired the counts-follow-lists derivation
    /// that used to live here: persisted counts now derive from the slot lists at Save time
    /// (BuildMeta), not on every rebuild.</summary>
    private void RebuildSideLists()
    {
        LocalParticipants.Clear();
        RemoteParticipants.Clear();
        int localUnnamed = 0, remoteUnnamed = 0;
        foreach (var p in Participants)
        {
            bool isLocal = p.Side == SourceKind.Local;
            p.DisplayLabel = p.IsUnnamed
                ? $"Speaker {(isLocal ? ++localUnnamed : ++remoteUnnamed)}"
                : p.Name;
            (isLocal ? LocalParticipants : RemoteParticipants).Add(p);
        }
    }

    partial void OnTitleChanged(string value) => MarkDirty();
    partial void OnDescriptionChanged(string value) => MarkDirty();
    partial void OnSelectedMediumChanged(Medium value) => MarkDirty();
    partial void OnArchivedChanged(bool value) => MarkDirty();
    // Display-only filter (Stage 5.4 5.3: non-persisted, per-editor UI state) - never a save.
    partial void OnMatterSearchTextChanged(string value) => RebuildMatterOptions();

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
        DiariseCommand.NotifyCanExecuteChanged();
        DiariseHint = value ? "Save changes before splitting speakers." : "";
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
        // Stage 5.4 5.2: the persisted per-side counts are PIPELINE-FACING (diarisation's
        // ForcedClusterCount, NameResolver tier-2) and derive from the slot lists at commit
        // time - one slot, one voice. Safe against lowering a legacy declared count because
        // LoadFieldsFromSaved synthesizes Unnamed slots up to it first. Floor 1 preserves the
        // historical >=1 clamp: an EMPTY side still declares one voice, so downstream
        // count==1 logic keeps working for a side the user never populated.
        LocalCount = Math.Max(1, Participants.Count(p => p.Side == SourceKind.Local)),
        RemoteCount = Math.Max(1, Participants.Count(p => p.Side == SourceKind.Remote)),
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
        // Attribution guard (Stage 5.4 5.1): if this commit changes how transcript lines are
        // LABELED (projection only - transcript.jsonl is never touched), ask before writing.
        // Runs synchronously on the command's calling (UI) thread, BEFORE any disk work; a
        // decline returns with the edits intact and the editor still dirty.
        var changes = DescribeAttributionChanges(_savedMeta, meta);
        if (changes.Count > 0 && !_confirm(
                "This save changes how transcript lines are attributed ("
                + string.Join("; ", changes)
                + "). The transcript itself is not modified. Save anyway?"))
            return;
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

    /// <summary>The pending commit's rendered-attribution delta vs the last-saved baseline, as
    /// human-readable fragments (empty = no warning). Mirrors NameResolver exactly: tier-2
    /// labels ALL of a side's lines with its FIRST participant's name when that side's declared
    /// count is 1 (NameResolver.cs:26-31). Warns only when a PREVIOUSLY RENDERED label changes
    /// or disappears - naming a previously unlabeled side is additive and stays silent. Also
    /// flags removing/renaming a participant that OWNS a diarised cluster (ClusterKey set, only
    /// ever assigned by Split-speakers confirm): its name labels that voice's lines under the
    /// owned-cluster resolver tier (Stage 5.4 5.2).</summary>
    private static List<string> DescribeAttributionChanges(SessionMeta saved, SessionMeta pending)
    {
        var parts = new List<string>();
        foreach (var side in new[] { SourceKind.Local, SourceKind.Remote })
        {
            string? before = SideLabel(saved, side);
            string? after = SideLabel(pending, side);
            if (before is not null && !string.Equals(before, after, StringComparison.Ordinal))
                parts.Add($"{side} lines: \"{before}\" -> "
                    + (after is null ? "unnamed" : $"\"{after}\""));
        }
        foreach (var p in saved.Participants)
        {
            if (string.IsNullOrEmpty(p.ClusterKey) || string.IsNullOrEmpty(p.Name)) continue;
            var now = pending.Participants.FirstOrDefault(q => q.Id == p.Id);
            if (now is null)
                parts.Add($"\"{p.Name}\" no longer labels its detected voice");
            else if (!string.Equals(now.Name, p.Name, StringComparison.Ordinal))
                parts.Add($"detected voice relabeled: \"{p.Name}\" -> \"{now.Name}\"");
        }
        return parts;
    }

    /// <summary>NameResolver tier-2's effective whole-side label: the FIRST participant's
    /// non-empty name on a side whose declared count is exactly 1, else null (labels then come
    /// from speakers.json or the Me/Them baseline, which meta edits cannot change).</summary>
    private static string? SideLabel(SessionMeta meta, SourceKind side)
    {
        int declared = side == SourceKind.Local ? meta.LocalCount : meta.RemoteCount;
        if (declared != 1) return null;
        string? name = meta.Participants.FirstOrDefault(p => p.Side == side)?.Name;
        return string.IsNullOrEmpty(name) ? null : name;
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
        Archived = _savedMeta.Archived;
        _selectedMatterIds.Clear();
        _selectedMatterIds.AddRange(_savedMeta.MatterIds);
        Participants.Clear();
        foreach (var p in _savedMeta.Participants) Participants.Add(new ParticipantRow(p));
        // Stage 5.4 5.2 lazy migration (resolved decision 1): a legacy session persists named
        // participants plus a DECLARED integer count that may exceed them (system-mix). Express
        // the shortfall as explicit Unnamed slots so the editor shows one row per declared
        // voice. Runs only under _loading (Attach / RevertToSaved both wrap this method), so it
        // never marks dirty and never writes - the synthesized rows persist ONLY on the next
        // explicit Save, and a reopen-without-Save leaves meta.json byte-identical.
        SynthesizeUnnamedSlots(SourceKind.Local, _savedMeta.LocalCount);
        SynthesizeUnnamedSlots(SourceKind.Remote, _savedMeta.RemoteCount);
        RebuildMatterOptions();
    }

    /// <summary>Appends Unnamed slots on the given side until its slot count reaches the
    /// persisted DECLARED count. Deterministic id minting (same seed as AddUnnamed) keeps a
    /// Discard/reopen regeneration stable within the session. A declared count at or below
    /// the participant count synthesizes nothing (already-migrated and post-save sessions).</summary>
    private void SynthesizeUnnamedSlots(SourceKind side, int declaredCount)
    {
        while (Participants.Count(p => p.Side == side) < declaredCount)
            AppendUnnamedSlot(side);
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
        string query = MatterSearchText.Trim();
        foreach (var e in _matterEntries)
        {
            bool selected = _selectedMatterIds.Contains(e.Id);
            string display = string.IsNullOrEmpty(e.Reference) ? e.Name : $"{e.Name} ({e.Reference})";
            if (e.Archived) display += " (archived)";
            var option = new MatterOption(e.Id, display, e.Archived, selected);
            // Results list (design 5.3): empty search offers ACTIVE matters only; a non-empty
            // search matches Name + Reference + Id and REVEALS archived matters. A selected
            // matter hidden from the results stays tagged - _selectedMatterIds is the truth,
            // not this list; the chips row below always shows the full tagged set.
            bool listed = query.Length == 0 ? !e.Archived : MatchesSearch(e, query);
            if (listed) MatterOptions.Add(option);
            if (selected) TaggedMatters.Add(option);
        }
    }

    /// <summary>The app's Contains(OrdinalIgnoreCase) idiom over the three searchable fields.</summary>
    private static bool MatchesSearch(MattersIndexEntry e, string query)
        => e.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
           || (e.Reference?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
           || e.Id.Contains(query, StringComparison.OrdinalIgnoreCase);
}
