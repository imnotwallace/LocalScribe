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
public sealed partial class MetadataEditorViewModel : ObservableObject
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
    [ObservableProperty] private string _newParticipantName = "";
    [ObservableProperty] private bool _newParticipantIsRemote = true;
    [ObservableProperty] private string? _rosterTargetMatterId;

    public ObservableCollection<MatterOption> MatterOptions { get; } = new();
    public ObservableCollection<MatterOption> TaggedMatters { get; } = new();
    public ObservableCollection<RosterPick> RosterPicks { get; } = new();
    public ObservableCollection<ParticipantRow> Participants { get; } = new();

    public IRelayCommand<MatterOption> ToggleMatterCommand { get; }
    public IAsyncRelayCommand AddFromRosterCommand { get; }
    public IRelayCommand AddFreeTextCommand { get; }
    public IAsyncRelayCommand AddToRosterAndSessionCommand { get; }
    public IRelayCommand<ParticipantRow> RemoveParticipantCommand { get; }

    public MetadataEditorViewModel(MaintenanceService maintenance, SessionViewModel session,
        IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time)
    {
        (_maintenance, _session, _errors, _dispatch, _time)
            = (maintenance, session, errors, dispatch, time);

        ToggleMatterCommand = new RelayCommand<MatterOption>(ToggleMatter);
        AddFromRosterCommand = new AsyncRelayCommand(
            () => SelectedRosterPick is { } pick
                ? AddFromRosterAsync(pick.MatterId, pick.MemberId)
                : Task.CompletedTask);
        AddFreeTextCommand = new RelayCommand(() =>
        {
            AddFreeText(NewParticipantName,
                NewParticipantIsRemote ? SourceKind.Remote : SourceKind.Local);
            NewParticipantName = "";
        });
        AddToRosterAndSessionCommand = new AsyncRelayCommand(async () =>
        {
            if (RosterTargetMatterId is not { Length: > 0 } matterId) return;
            await AddToRosterAndSessionAsync(matterId, NewParticipantName,
                NewParticipantIsRemote ? SourceKind.Remote : SourceKind.Local);
            NewParticipantName = "";
        });
        RemoveParticipantCommand = new RelayCommand<ParticipantRow>(r => { if (r is not null) Remove(r); });

        // SessionViewModel raises State changes already marshaled through ITS dispatch
        // (SessionViewModel.cs:56-62), so this handler runs on the UI thread.
        session.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(SessionViewModel.State)) RecomputeEditable(); };
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
        }
        finally { _loading = false; }
        SavedIndicator = false;
        RecomputeEditable();
        if (row is not null) _ = RefreshMatterDataAsync();
        else { MatterOptions.Clear(); TaggedMatters.Clear(); RosterPicks.Clear(); }
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
        Attach(item is null ? null : new SessionRowViewModel(item, _time));
    }

    /// <summary>Driven by a ~250 ms DispatcherTimer in production; tests call it directly.</summary>
    public void Tick()
    {
        if (SavedIndicator && _time.GetUtcNow() >= _savedIndicatorUntil) SavedIndicator = false;
    }

    /// <summary>Roster pick COPIES the member's id and name into the session snapshot -
    /// provenance only, never a live link (design 3.3). Side defaults to Remote (roster
    /// members are other people); remove-and-re-add corrects a wrong side.</summary>
    public async Task AddFromRosterAsync(string matterId, string rosterMemberId)
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
                { Id = member.Id, Name = member.Name, Side = SourceKind.Remote, Role = member.Role }));
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

    /// <summary>Inline person add (design L547-550): mint against the MATTER's roster ids,
    /// write through to the matter's roster, then snapshot id+name into this session.</summary>
    public async Task AddToRosterAndSessionAsync(string matterId, string name, SourceKind side)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0) return;
        try
        {
            var matter = await _maintenance.LoadMatterAsync(matterId, CancellationToken.None);
            if (matter is null)
            { _dispatch(() => _errors.Info("Tag a matter before adding to its roster.")); return; }
            string id = ParticipantId.Mint(trimmed, matter.Roster.Select(m => m.Id).ToArray());
            var member = new RosterMember { Id = id, Name = trimmed };
            await _maintenance.SaveMatterAsync(
                matter with { Roster = matter.Roster.Append(member).ToArray() },
                CancellationToken.None);
            _dispatch(() =>
            {
                Participants.Add(new ParticipantRow(new SessionParticipant
                { Id = id, Name = trimmed, Side = side }));
                RosterPicks.Add(new RosterPick(matterId, id, $"{trimmed} ({matter.Name})"));
                QueueSave();
            });
        }
        catch (Exception ex) { _dispatch(() => _errors.Report("Adding to roster", ex)); }
    }

    public void Remove(ParticipantRow row)
    {
        if (Participants.Remove(row)) QueueSave();
    }

    partial void OnTitleChanged(string value) => QueueSave();
    partial void OnDescriptionChanged(string value) => QueueSave();
    partial void OnSelectedMediumChanged(Medium value) => QueueSave();
    partial void OnArchivedChanged(bool value) => QueueSave();
    partial void OnLocalCountChanged(int value)
    { if (value < 1) { LocalCount = 1; return; } QueueSave(); }
    partial void OnRemoteCountChanged(int value)
    { if (value < 1) { RemoteCount = 1; return; } QueueSave(); }
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
        if (RosterTargetMatterId is null || !_selectedMatterIds.Contains(RosterTargetMatterId))
            RosterTargetMatterId = _selectedMatterIds.Count > 0 ? _selectedMatterIds[0] : null;
    }
}
