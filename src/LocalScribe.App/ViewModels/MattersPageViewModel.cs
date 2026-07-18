// src/LocalScribe.App/ViewModels/MattersPageViewModel.cs
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
namespace LocalScribe.App.ViewModels;

/// <summary>One row of the selected matter's tagged-sessions grid (design 2026-07-18 section 4).
/// DurationDisplay is built via SessionRowViewModel.FormatDuration, the shared single source of
/// the format (h:mm:ss over an hour, else mm:ss; "" while pending recovery). IsPendingRecovery =
/// EndedAtUtc is null (row opens Details but not the transcript).</summary>
public sealed record TaggedSessionItem(string SessionId, string Title, string DateDisplay,
    string DurationDisplay, bool IsPendingRecovery);

/// <summary>Matters page: CRUD + roster editor + tagged-sessions organizer (design section 4).
/// WPF-free; every disk mutation routes through MaintenanceService (design 7.3). Roster edits
/// never touch sessions and never cascade - session participants are snapshots (design 4.4).</summary>
public sealed partial class MattersPageViewModel : ObservableObject
{
    private readonly MaintenanceService _maintenance;
    private readonly MatterDeleter _deleter;
    private readonly WindowRegistry _windows;
    private readonly IUiErrorReporter _reporter;
    private readonly Func<SavePathRequest, string?> _pickSavePath;
    private readonly Action<string> _revealFile;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _exportCts;
    private MattersIndex _index = new();
    private Matter? _loaded;                        // detail truth as last loaded/saved

    public ObservableCollection<MattersIndexEntry> Matters { get; } = new();
    public ObservableCollection<RosterMember> Roster { get; } = new();
    public ObservableCollection<TaggedSessionItem> TaggedSessions { get; } = new();

    /// <summary>Pager + title filter over the tagged-sessions grid (design 2026-07-18 section 4).</summary>
    public PagerViewModel TaggedPager { get; } = new();
    private List<TaggedSessionItem> _taggedAll = [];
    private List<TaggedSessionItem> _taggedFiltered = [];

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
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private int _exportProgress;
    [ObservableProperty] private int _exportMax;
    [ObservableProperty] private string _taggedFilterText = "";
    [ObservableProperty] private TaggedSessionItem? _selectedTagged;
    [ObservableProperty] private string _headerSummary = "";
    [ObservableProperty] private string _headerCreatedDisplay = "";

    public bool HasTaggedSelection => SelectedTagged is not null;
    partial void OnSelectedTaggedChanged(TaggedSessionItem? value)
        => OnPropertyChanged(nameof(HasTaggedSelection));
    partial void OnTaggedFilterTextChanged(string value)
    {
        TaggedPager.Reset();
        ApplyTaggedFilter();
    }

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
        WindowRegistry windows, IUiErrorReporter reporter,
        Func<SavePathRequest, string?> pickSavePath, Action<string> revealFile, Action<Action> dispatch)
    {
        (_maintenance, _deleter, _windows, _reporter, _pickSavePath, _revealFile, _dispatch)
            = (maintenance, deleter, windows, reporter, pickSavePath, revealFile, dispatch);
        CreateMatterCommand = new AsyncRelayCommand(CreateMatterAsync);
        CommitDetailCommand = new AsyncRelayCommand(CommitDetailAsync);
        AddMemberCommand = new AsyncRelayCommand(AddMemberAsync);
        DeleteMatterCommand = new AsyncRelayCommand(DeleteMatterAsync);
        RepairIndexCommand = new AsyncRelayCommand(RepairIndexAsync);
        TaggedPager.Changed += ApplyTaggedPage;
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
                _taggedAll = sessions.Sessions
                    .Where(s => s.Meta.MatterIds.Contains(matterId))
                    .OrderByDescending(s => s.Session.StartedAtUtc)
                    .ThenByDescending(s => s.Id, StringComparer.Ordinal)
                    .Select(s => new TaggedSessionItem(s.Id, s.Meta.Title, DateDisplay(s.Session),
                        SessionRowViewModel.FormatDuration(s.Session), s.Session.EndedAtUtc is null))
                    .ToList();
                TaggedPager.Reset();
                // A title filter must not carry across matters (design 2026-07-18 UX round,
                // cleanup #2): clear it via the property (not the backing field, which keeps the
                // CommunityToolkit analyzer happy and updates the bound TextBox) now that
                // _taggedAll is already the NEW matter's list. When the filter was non-empty this
                // setter fires OnTaggedFilterTextChanged (Reset + ApplyTaggedFilter again) - a
                // negligible redundant in-memory filter pass; when it was already empty the
                // setter no-ops on the same value, so the explicit ApplyTaggedFilter() below still
                // covers that case.
                TaggedFilterText = "";
                ApplyTaggedFilter();
                HeaderSummary = loaded.Roster.FirstOrDefault(m =>
                        string.Equals(m.Role, "Client", StringComparison.OrdinalIgnoreCase)) is { } client
                    ? "Client: " + client.Name
                    : loaded.Roster.Count + " member(s)";
                HeaderCreatedDisplay = "created "
                    + loaded.DateCreatedUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                HasSelection = true;
            });
        }
        catch (Exception ex) { _reporter.Report("Open matter", ex); }
    }

    /// <summary>Secondary tagged-session "Details" action: opens the Session Details window by id
    /// (no longer navigates to the Sessions page - design 5.2). Superseded as the primary action
    /// by OpenTranscript (design 2026-07-18 section 4), which now opens the read view instead.</summary>
    public void JumpToSession(string sessionId) => OpenSessionDetailsRequested?.Invoke(sessionId);

    /// <summary>Primary tagged-session action (design 2026-07-18 section 4): opens the transcript
    /// read view. Reverses the Stage 5.2 details-only decision. Pending-recovery rows are refused
    /// with an actionable Info (same rule as the Sessions page's OpenReadView guard).</summary>
    public event Action<string>? OpenReadViewRequested;

    public void OpenTranscript(string sessionId)
    {
        if (_taggedAll.FirstOrDefault(t => t.SessionId == sessionId) is { IsPendingRecovery: true })
        {
            _reporter.Info("This session is still being recovered. Try again once recovery completes.");
            return;
        }
        OpenReadViewRequested?.Invoke(sessionId);
    }

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

    /// <summary>Raised after AddSessionsAsync actually tagged a session on disk (grid coherence,
    /// mirror of SessionUntagged): App.xaml.cs routes this to SessionsPageViewModel.RefreshRowAsync.</summary>
    public event Action<string>? SessionTagged;

    /// <summary>Candidates for the Add-sessions picker (design 2026-07-18 section 4): unarchived
    /// sessions not already tagged with the selected matter, newest-first. Pending-recovery rows
    /// ARE included - tagging writes meta.json only, which is legal for them (same rule as the
    /// Session Details picker).</summary>
    public async Task<IReadOnlyList<PickerSessionItem>> ListUntaggedSessionsAsync()
    {
        if (SelectedMatterId is not string matterId) return [];
        var sessions = await _maintenance.ListSessionsAsync(CancellationToken.None);
        return sessions.Sessions
            .Where(s => !s.Meta.Archived && !s.Meta.MatterIds.Contains(matterId, StringComparer.Ordinal))
            .OrderByDescending(s => s.Session.StartedAtUtc)
            .ThenByDescending(s => s.Id, StringComparer.Ordinal)
            .Select(s => new PickerSessionItem(s.Id, s.Meta.Title, DateDisplay(s.Session),
                s.Session.App.ToString()))
            .ToList();
    }

    /// <summary>Tags each selected session to the selected matter through the SAME
    /// SaveMetaAsync tag-delta path Session Details and UntagSessionAsync use, so index and
    /// search semantics stay byte-identical. Loads each session FRESH from disk (the stale
    /// picker snapshot never feeds the delta); already-tagged and vanished sessions are silent
    /// no-ops; a per-session failure is reported and does NOT abort the rest. Organizational
    /// only: meta.json is the ONLY file written (evidentiary firewall).</summary>
    public async Task AddSessionsAsync(IReadOnlyList<string> sessionIds)
    {
        if (SelectedMatterId is not string matterId) return;
        foreach (string sessionId in sessionIds)
        {
            try
            {
                var item = await _maintenance.LoadSessionItemAsync(sessionId, CancellationToken.None);
                if (item is null) continue;                              // deleted underneath us
                var previous = item.Meta.MatterIds;
                if (previous.Contains(matterId, StringComparer.Ordinal)) continue;   // raced: already tagged
                var updated = item.Meta with { MatterIds = previous.Append(matterId).ToList() };
                await _maintenance.SaveMetaAsync(sessionId, updated, previous, CancellationToken.None);
                SessionTagged?.Invoke(sessionId);
            }
            catch (Exception ex) { _reporter.Report("Tag session " + sessionId, ex); }
        }
        await RefreshAsync();                                            // matter counts changed
        await SelectAsync(matterId);                                     // rebuild the tagged list
    }

    // Session-offset date, same fallback chain as SessionWriter (machine zone only pre-v3).
    private static string DateDisplay(SessionRecord session)
    {
        var local = session.UtcOffsetMinutes is int offsetMin
            ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
            : session.StartedAtUtc.ToLocalTime();
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private void ApplyTaggedFilter()
    {
        string q = TaggedFilterText.Trim();
        _taggedFiltered = q.Length == 0
            ? _taggedAll.ToList()
            : _taggedAll.Where(t => t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        TaggedPager.SetTotal(_taggedFiltered.Count);
        ApplyTaggedPage();
    }

    // Every caller (the SelectAsync dispatched block, OnTaggedFilterTextChanged, and
    // TaggedPager.Changed) is already on the UI thread, so the _dispatch(...) wrapper here was a
    // redundant double-hop (design 2026-07-18 UX round, cleanup #3) - the other two hosts'
    // ApplyPage do not wrap either.
    private void ApplyTaggedPage()
    {
        string? keepId = SelectedTagged?.SessionId;
        TaggedSessions.Clear();
        foreach (var t in TaggedPager.Slice(_taggedFiltered)) TaggedSessions.Add(t);
        SelectedTagged = TaggedSessions.FirstOrDefault(t => t.SessionId == keepId);
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
            // SaveMatterAsync above is unconditional; only the cascade itself is guarded, so a
            // concurrent Re-render click just leaves the projections briefly stale rather than
            // racing this cascade (review fix, Task 5).
            if (cascade) await RunCascadeAsync(updated.Id);
        }
        catch (Exception ex) { _reporter.Report("Save matter", ex); }
    }

    /// <summary>Synchronous IProgress: Progress&lt;T&gt; posts to a captured SyncContext, which
    /// is nondeterministic headless - this reports inline and marshals via the dispatch seam.</summary>
    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    private bool _cascading;

    /// <summary>Single shared guard + status + cascade runner for BOTH cascade triggers - the
    /// Details name/reference auto-cascade and the explicit "Re-render tagged sessions" button
    /// (review fix, Task 5). Without one shared guard, committing a name change and then clicking
    /// Re-render mid-flight could run two concurrent CascadeMatterAsync on the same matter:
    /// interleaved CascadeStatus writes and potentially concurrent writes to the same session
    /// projection files. A no-op no-throw return when already running is safe here because the
    /// caller's own save (if any) already happened unconditionally before this runs.</summary>
    private async Task RunCascadeAsync(string matterId)
    {
        if (_cascading) return;              // a cascade (from either trigger) is already running
        _cascading = true;
        try
        {
            _dispatch(() => CascadeStatus = "Re-rendering tagged sessions...");
            var progress = new InlineProgress(n => _dispatch(() => CascadeStatus =
                string.Create(CultureInfo.InvariantCulture, $"Re-rendering tagged sessions... {n} done")));
            await _maintenance.CascadeMatterAsync(matterId, progress, CancellationToken.None);
        }
        finally { _dispatch(() => CascadeStatus = ""); _cascading = false; }
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

    /// <summary>Explicit re-render of every session tagged with the selected matter, so a
    /// vocabulary change reaches already-recorded transcripts. Reuses CascadeMatterAsync via
    /// RunCascadeAsync, the same guard + CascadeStatus inline-progress the Details name/reference
    /// auto-cascade uses (review fix, Task 5) - so this button can never overlap that cascade,
    /// not just a second click of itself.</summary>
    public async Task RerenderTaggedAsync()
    {
        if (_loaded is null) return;
        try { await RunCascadeAsync(_loaded.Id); }
        catch (Exception ex) { _reporter.Report("Re-rendering tagged sessions", ex); }
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

    /// <summary>Export the selected matter's tagged (finalized) sessions as a .zip (design 3.2/3.4):
    /// pick a destination, then run under a CTS with determinate IProgress&lt;int&gt; (SplitSpeakers
    /// pattern - never Progress&lt;T&gt;). Unfinalized sessions are skipped and reported.</summary>
    public async Task ExportMatterArchiveAsync()
    {
        if (IsExporting || _loaded is null) return;
        var request = new SavePathRequest(
            ExportFileNames.Sanitize(string.IsNullOrEmpty(_loaded.Reference) ? _loaded.Name : _loaded.Reference) + ".zip",
            "Zip archive (*.zip)|*.zip");
        string? dest = _pickSavePath(request);
        if (string.IsNullOrWhiteSpace(dest)) return;

        _exportCts = new CancellationTokenSource();
        var progress = new DispatchedProgress(_dispatch, n => ExportProgress = n);
        _dispatch(() => { ExportProgress = 0; ExportMax = _taggedAll.Count; IsExporting = true; });
        try
        {
            var result = await _maintenance.ExportMatterArchiveAsync(_loaded.Id, dest, progress, _exportCts.Token);
            string summary = string.Create(CultureInfo.InvariantCulture,
                $"Exported {result.Added} session(s) to {dest}");
            if (result.Skipped > 0)
                summary += string.Create(CultureInfo.InvariantCulture,
                    $" ({result.Skipped} skipped: recording or recovering)");
            _reporter.Info(summary);
            _revealFile(dest);
        }
        catch (OperationCanceledException) { _reporter.Info("Export cancelled."); }
        catch (Exception ex) { _reporter.Report("Export matter archive", ex); }
        finally { _exportCts = null; _dispatch(() => IsExporting = false); }
    }

    public void CancelExport() => _exportCts?.Cancel();

    /// <summary>Synchronous-dispatch IProgress (SplitSpeakers pattern): never System.Progress&lt;T&gt;,
    /// whose captured SyncContext post is nondeterministic headless.</summary>
    private sealed class DispatchedProgress(Action<Action> dispatch, Action<int> apply) : IProgress<int>
    {
        public void Report(int value) => dispatch(() => apply(value));
    }
}
