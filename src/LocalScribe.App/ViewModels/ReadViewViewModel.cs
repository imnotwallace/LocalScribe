// src/LocalScribe.App/ViewModels/ReadViewViewModel.cs
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.App.ViewModels;

/// <summary>One entry in the read-view version dropdown (design 2026-07-13 section 3.4):
/// Id is "v1" or a TranscriptVersion.Id; Label is the badge form: short id, middle dot, model.</summary>
public sealed record VersionOption(string Id, string Label);

/// <summary>Read-only session view (design section 5). Rows come from the canonical
/// TranscriptProjection - the same pipeline as transcript.md/.txt and session.txt. The load
/// pipeline mirrors SessionWriter.RegenerateProjectionsAsync (load order, meta fallback,
/// vocabulary provider construction) so what the window shows is what the files say. Known
/// deliberate divergence: the 3b live view renders raw merger lines with no projection pass,
/// so this view may differ from what was seen live. WPF-free; all reads run inside the
/// maintenance per-session queue so a load cannot interleave with recovery or a cascade.</summary>
public sealed partial class ReadViewViewModel : ObservableObject, IDisposable
{
    private readonly MaintenanceService _maintenance;
    private readonly StoragePaths _paths;
    private readonly ISettingsService _settings;
    private readonly IUiErrorReporter _reporter;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;

    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _dateDisplay = "";
    [ObservableProperty] private string _durationDisplay = "";
    [ObservableProperty] private bool _recovered;
    [ObservableProperty] private bool _edited;
    [ObservableProperty] private bool _systemMix;
    [ObservableProperty] private bool _hasDegradedMarker;
    [ObservableProperty] private string _modelBackendFooter = "";
    /// <summary>Gates the "Split speakers..." button (Stage 5 design 4.1): true only when the
    /// session is finalized/recovered AND at least one side both declares more than one speaker
    /// and still has its leg retained on disk - i.e. mirrors SplitSpeakersViewModel's own
    /// splittable-source gating, so the button is never enabled for a session the dialog would
    /// then offer nothing for.</summary>
    [ObservableProperty] private bool _canDiarise;

    /// <summary>Stage 6.1 read-view Edit mode (design §3.2/§3.4): whole-session correction/split
    /// editing, gated the same way as CanDiarise (finalized/recovered only). EditSections mirrors
    /// Rows' non-marker entries while editing; SaveEditsAsync assembles one TranscriptEditBatch
    /// from every section and writes it through MaintenanceService, then reloads.</summary>
    [ObservableProperty] private bool _isEditMode;

    /// <summary>Edit-mode save failure surfaced IN the read-view window (bound to its InfoBar).
    /// The shared IUiErrorReporter routes to MainWindow's InfoBar, which the separate read-view
    /// window can't show - so a failed SaveEditsAsync used to fail silently here and leave the
    /// user stuck in edit mode with no feedback (2026-08-02 gold-edit smoke). Set on failure,
    /// cleared on entering edit / a clean save / cancel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    private string? _saveError;

    /// <summary>The read-view InfoBar's IsOpen binds here (a computed OneWay flag, since IsOpen
    /// can't bind a null-check directly).</summary>
    public bool HasSaveError => SaveError is not null;
    public ObservableCollection<EditableSectionViewModel> EditSections { get; } = new();
    // Task 14: was a plain auto-property; the read-view's Edit button visibility binds to this,
    // and a plain property never raises PropertyChanged when ApplyRows flips it after the initial
    // (always-false) binding evaluation, so the button would stay permanently hidden even once a
    // session finishes loading. Promoted to [ObservableProperty] to match CanDiarise's sibling
    // gate, which already does this correctly.
    [ObservableProperty] private bool _canEdit;

    /// <summary>Version badge + switcher (design 2026-07-13 section 3.4). Rebuilt by every
    /// ApplyRows under _syncingVersions so the programmatic selection never re-triggers a
    /// switch; a USER pick flows through OnSelectedVersionOptionChanged -> SwitchVersionAsync.</summary>
    public ObservableCollection<VersionOption> VersionOptions { get; } = new();
    [ObservableProperty] private VersionOption? _selectedVersionOption;
    [ObservableProperty] private bool _hasVersions;
    private bool _syncingVersions;

    partial void OnSelectedVersionOptionChanged(VersionOption? value)
    {
        if (_syncingVersions || value is null || !IsLoaded) return;
        _ = SwitchVersionAsync(value.Id, CancellationToken.None);     // fire-and-forget; catches inside
    }

    /// <summary>Persists ActiveVersion then reloads rows/edits/speakers/badges from disk via the
    /// gated ReloadRowsAsync - deliberately NOT LoadAsync: playback must not re-resolve
    /// (DualMediaPlayer.Load re-subscribes per call) and the audio legs are version-independent
    /// (design section 3.3). Public so tests and the dropdown share one deterministic path.</summary>
    public async Task SwitchVersionAsync(string versionId, CancellationToken ct)
    {
        try
        {
            if (await _maintenance.SetActiveVersionAsync(SessionId, versionId, ct))
                await ReloadRowsAsync(ct);
        }
        catch (Exception ex) { _reporter.Report("Switch transcript version", ex); }
    }

    public ObservableCollection<ReadRow> Rows { get; } = new();
    public ObservableCollection<string> MatterDisplays { get; } = new();
    public ObservableCollection<string> ParticipantDisplays { get; } = new();
    public string SessionId { get; private set; } = "";
    public string TimestampsMode { get; private set; } = "relative";   // read by the window's stamp converter
    public DateTimeOffset StartedAtLocal { get; private set; }

    /// <summary>Dual-leg audio transport (design section 5). Created eagerly so window
    /// bindings are stable; IsAvailable stays false until LoadAsync resolves real files.</summary>
    public PlaybackViewModel Playback { get; }

    /// <summary>Index of the "now playing" transcript section (design 4.1), recomputed each
    /// Tick from Playback.PositionMs over the rows' [StartMs, nextStart / last EndMs] windows.
    /// -1 before the first section starts or after the media truly ends. Mirrored into
    /// Playback.PlayingIndex so the transport layer sees the same value.</summary>
    [ObservableProperty] private int _playingSectionIndex = -1;

    /// <summary>Tracks the last index this method flipped IsNowPlaying for, so the transition
    /// can clear the old row and set the new one in O(1) without scanning Rows.</summary>
    private int _nowPlayingRowIndex = -1;

    // ITEM 5: the precise "now playing" cursor at SEGMENT granularity, (rowIndex, segIndex). Kept
    // alongside the row-level _nowPlayingRowIndex so the row can still drive scroll-into-view while
    // the visible tint lands on the exact segment under the playhead.
    private int _nowPlayingSegRow = -1;
    private int _nowPlayingSegIndex = -1;

    /// <summary>Loaded-truth snapshots the Stage 6.1 editor factories need (candidate lists,
    /// pin ownership). Refreshed by every LoadAsync/ReloadRowsAsync under the same gate.</summary>
    private SessionMeta? _loadedMeta;
    private Speakers? _loadedSpeakers;
    /// <summary>F1 fix (whole-branch review): the version THIS load/reload actually read from
    /// disk (LoadedProjection.VersionId), refreshed by every ApplyRows call. Every content-write
    /// below snapshots this into a local before use and passes it explicitly to
    /// MaintenanceService, instead of letting the write re-resolve ActiveVersion at write time -
    /// so a version switched (or a background re-transcription completing) between load/edit-entry
    /// and Save can never silently redirect a correction/pin into the wrong version's overlay. The
    /// read-view version ComboBox is disabled for the whole duration of Edit mode (ReadViewWindow.xaml),
    /// so this field cannot change out from under an in-progress SaveEditsAsync call.</summary>
    private string _loadedVersionId = TranscriptVersions.Root;

    // Stage 5.4 smoke-fix: the moving highlight lives on each ReadRow.IsNowPlaying, NOT
    // ListView.SelectedIndex - binding the highlight to SelectedIndex meant the VM and the
    // user's own click both wrote the same property (last-wins, silently discarding a real
    // selection) and fired a UIA selection-changed announcement every time the section advanced.
    partial void OnPlayingSectionIndexChanged(int value)
    {
        Playback.PlayingIndex = value;
        if (_nowPlayingRowIndex >= 0 && _nowPlayingRowIndex < Rows.Count)
            Rows[_nowPlayingRowIndex].IsNowPlaying = false;
        if (value >= 0 && value < Rows.Count)
            Rows[value].IsNowPlaying = true;
        _nowPlayingRowIndex = value;
    }

    /// <summary>findDebounceMs: item 1's edit-typing find recompute delay; tests pass 0 for a
    /// synchronous, deterministic recompute (the SearchPageViewModel debounce-seam pattern).</summary>
    public ReadViewViewModel(MaintenanceService maintenance, StoragePaths paths,
        ISettingsService settings, IUiErrorReporter reporter, IDualAudioPlayer player,
        Action<Action> dispatch, TimeProvider time, int findDebounceMs = 250)
    {
        (_maintenance, _paths, _settings, _reporter, _dispatch, _time)
            = (maintenance, paths, settings, reporter, dispatch, time);
        _findDebounceMs = findDebounceMs;
        Playback = new PlaybackViewModel(player, dispatch);
    }

    /// <summary>Called by the read-view window's ~150 ms timer: advance the transport, then
    /// recompute the highlighted section. Tests call it directly.</summary>
    public void TickPlayback()
    {
        Playback.Tick();
        PlayingSectionIndex = SectionAt(Playback.PositionMs);
        UpdatePlayingSegment(PlayingSectionIndex, Playback.PositionMs);
    }

    private int SectionAt(long positionMs)
    {
        int idx = -1;
        for (int i = 0; i < Rows.Count; i++)
        {
            long start = Rows[i].Data.StartMs;
            long end = i + 1 < Rows.Count ? Rows[i + 1].Data.StartMs : Rows[i].Data.EndMs;
            if (positionMs >= start && positionMs <= end) idx = i;   // greatest match wins at a boundary
        }
        return idx;
    }

    /// <summary>The segment within a row whose window contains <paramref name="positionMs"/>, using
    /// the same greatest-match-wins-at-a-boundary rule as <see cref="SectionAt"/>: each segment owns
    /// [StartMs, nextSegStartMs); the last segment runs through its EndMs, and a position past the
    /// last EndMs (the trailing intra-row gap before the next turn) holds the last segment so the
    /// highlight does not flicker off. -1 when the row has no segments.</summary>
    private int SegmentAt(int rowIndex, long positionMs)
    {
        if (rowIndex < 0 || rowIndex >= Rows.Count) return -1;
        var segs = Rows[rowIndex].Segments;
        if (segs.Count == 0) return -1;
        int idx = -1;
        for (int i = 0; i < segs.Count; i++)
        {
            long start = segs[i].StartMs;
            long end = i + 1 < segs.Count ? segs[i + 1].StartMs : segs[i].EndMs;
            if (positionMs >= start && positionMs <= end) idx = i;
        }
        if (idx < 0 && positionMs > segs[^1].EndMs) idx = segs.Count - 1;
        return idx;
    }

    /// <summary>Moves the single per-segment IsNowPlaying flag to the segment under the playhead,
    /// clearing the previously-lit one (including when the playing row changed). O(1) via the
    /// (row, seg) cursor - no scan.</summary>
    private void UpdatePlayingSegment(int rowIndex, long positionMs)
    {
        int segIndex = SegmentAt(rowIndex, positionMs);
        if (rowIndex == _nowPlayingSegRow && segIndex == _nowPlayingSegIndex) return;

        if (_nowPlayingSegRow >= 0 && _nowPlayingSegRow < Rows.Count)
        {
            var prev = Rows[_nowPlayingSegRow].Segments;
            if (_nowPlayingSegIndex >= 0 && _nowPlayingSegIndex < prev.Count)
                prev[_nowPlayingSegIndex].IsNowPlaying = false;
        }
        if (rowIndex >= 0 && rowIndex < Rows.Count && segIndex >= 0)
        {
            var cur = Rows[rowIndex].Segments;
            if (segIndex < cur.Count) cur[segIndex].IsNowPlaying = true;
        }
        _nowPlayingSegRow = rowIndex;
        _nowPlayingSegIndex = segIndex;
    }

    /// <summary>Click-to-jump: seek to the section's start and begin playing (design 4.1).</summary>
    public void JumpToSection(int index)
    {
        if (index < 0 || index >= Rows.Count) return;
        Playback.Seek(Rows[index].Data.StartMs);
        if (!Playback.IsPlaying) Playback.PlayPauseCommand.Execute(null);
    }

    /// <summary>Per-segment click-to-jump (ITEM 5): seek to a specific segment's start and begin
    /// playing. Mirrors <see cref="JumpToSection"/> but takes an absolute ms so the read view can
    /// target any inline within a merged turn, not only the turn's first segment.</summary>
    public void SeekSegment(long startMs)
    {
        Playback.Seek(startMs);
        if (!Playback.IsPlaying) Playback.PlayPauseCommand.Execute(null);
    }

    // ---- Ctrl+F find bar (design 2026-07-13 section 2.2 surface 3) ---------------------------
    // Searches the VISIBLE corrected text of the loaded version only (Rows[i].Data.Text - the
    // projected text: vocabulary + edits overlay + splits). Machine RAW text is deliberately not
    // searched here (that is the cross-session index's job, with its original-text labelling);
    // marker rows ARE searched - this is find-on-page over what the reader can see.

    [ObservableProperty] private bool _isFindOpen;
    [ObservableProperty] private string _findText = "";
    [ObservableProperty] private string _findStatus = "";
    [ObservableProperty] private int _currentFindRowIndex = -1;
    private readonly List<int> _findMatchRows = new();

    private readonly int _findDebounceMs;
    private CancellationTokenSource? _findRecomputeCts;

    /// <summary>Test seam (the SearchPageViewModel.PendingSearch precedent): the in-flight
    /// debounced edit-typing recompute, if any. Null until the first schedule.</summary>
    public Task? PendingFindRecompute { get; private set; }

    /// <summary>Item 1: every EditedText keystroke (via EditableSectionViewModel.LiveTextChanged)
    /// supersedes the previous pending recompute - counts refresh as the user types without a
    /// per-keystroke full scan. No-op while the bar is closed or in read mode.</summary>
    private void ScheduleFindRecompute()
    {
        if (!IsFindOpen || !IsEditMode) return;
        _findRecomputeCts?.Cancel();
        var cts = _findRecomputeCts = new CancellationTokenSource();
        PendingFindRecompute = RunFindRecomputeAsync(cts.Token);
    }

    private async Task RunFindRecomputeAsync(CancellationToken ct)
    {
        try
        {
            if (_findDebounceMs > 0) await Task.Delay(_findDebounceMs, ct);
            if (ct.IsCancellationRequested) return;
            _dispatch(() =>
            {
                if (ct.IsCancellationRequested || !IsEditMode) return;   // superseded / mode left
                RecomputeFindMatches(moveToFirst: false);
            });
        }
        catch (TaskCanceledException) { }
    }

    /// <summary>Detach every section's LiveTextChanged and kill any pending recompute - called on
    /// both exits from Edit mode, right before EditSections is cleared.</summary>
    private void UnwireEditSections()
    {
        foreach (var s in EditSections) s.LiveTextChanged -= ScheduleFindRecompute;
        _findRecomputeCts?.Cancel();
    }

    partial void OnFindTextChanged(string value) => RecomputeFindMatches(moveToFirst: true);

    partial void OnCurrentFindRowIndexChanged(int oldValue, int newValue)
    {
        if (IsEditMode)
        {
            if (oldValue >= 0 && oldValue < EditSections.Count) EditSections[oldValue].IsCurrentFindMatch = false;
            if (newValue >= 0 && newValue < EditSections.Count) EditSections[newValue].IsCurrentFindMatch = true;
        }
        else
        {
            if (oldValue >= 0 && oldValue < Rows.Count) Rows[oldValue].IsCurrentFindMatch = false;
            if (newValue >= 0 && newValue < Rows.Count) Rows[newValue].IsCurrentFindMatch = true;
        }
        UpdateFindStatus();
    }

    /// <summary>Opens the find bar - in BOTH read and edit mode (item 1, UX round 2026-08-02: the
    /// old edit-mode refusal is gone; matches land on whichever list is visible). With initialText
    /// (the search page's click-through term) the text change recomputes matches; re-opening with
    /// the same text recomputes explicitly so flags land on the current rows.</summary>
    public void OpenFind(string? initialText = null)
    {
        IsFindOpen = true;
        if (initialText is not null && initialText != FindText) FindText = initialText;
        else RecomputeFindMatches(moveToFirst: _findMatchRows.Count == 0);
    }

    public void CloseFind()
    {
        IsFindOpen = false;
        foreach (var r in Rows) { r.IsFindMatch = false; r.IsCurrentFindMatch = false; }
        foreach (var s in EditSections) { s.IsFindMatch = false; s.IsCurrentFindMatch = false; }
        _findMatchRows.Clear();
        CurrentFindRowIndex = -1;
        FindStatus = "";
        // FindText is deliberately kept so Ctrl+F re-opens on the same term.
    }

    /// <summary>Find-bar escalation (design 2026-07-18 section 3): the window layer navigates the
    /// main window to the Search page pre-filled with this term, facets reset to their defaults
    /// (all matters / all apps / all dates - never inherited from this session).</summary>
    public event Action<string>? SearchAllSessionsRequested;

    public void RequestSearchAllSessions() => SearchAllSessionsRequested?.Invoke(FindText);

    public void FindNext()
    {
        if (_findMatchRows.Count == 0) return;
        int pos = _findMatchRows.IndexOf(CurrentFindRowIndex);
        CurrentFindRowIndex = _findMatchRows[(pos + 1) % _findMatchRows.Count];   // pos -1 -> first
    }

    public void FindPrevious()
    {
        if (_findMatchRows.Count == 0) return;
        int pos = _findMatchRows.IndexOf(CurrentFindRowIndex);
        CurrentFindRowIndex = _findMatchRows[pos <= 0 ? _findMatchRows.Count - 1 : pos - 1];
    }

    /// <summary>Index of the read-list row whose grouped turn contains the seq; -1 when the seq is
    /// dedup-hidden or absent. The first row containing the seq is the scroll target (split parts
    /// of one seq can group into different rows; the first is fine for targeting).</summary>
    public int RowIndexOfSeq(int seq)
    {
        for (int i = 0; i < Rows.Count; i++)
            if (Rows[i].Data.Segments.Any(s => s.Seq == seq)) return i;
        return -1;
    }

    /// <summary>The EditSections index for a Rows index, falling FORWARD past markers (a marker
    /// has no section; the next speaker turn is the natural landing spot). -1 when nothing maps
    /// (out of range, or a trailing marker) or in read mode before any sections exist.</summary>
    public int EditSectionIndexOfRow(int rowIndex)
    {
        for (int i = rowIndex; i >= 0 && i < Rows.Count; i++)
        {
            int si = EditSectionIndexOf(Rows[i].Data);
            if (si >= 0) return si;
        }
        return -1;
    }

    /// <summary>Row-space input -> current-mode find/scroll index (item 1 free rider: search-page
    /// and assistant-citation click-through stop no-oping during edit). Read mode: the row index
    /// itself. Edit mode: the mapped section index. The window scrolls whatever this returns via
    /// its mode-aware helper.</summary>
    public int FindScrollTargetForRow(int rowIndex)
        => IsEditMode ? EditSectionIndexOfRow(rowIndex) : rowIndex;

    /// <summary>Points the current match at the given ROW (search-page click-through - the input
    /// is always a Rows index). In edit mode the row maps forward to its section first (item 1).
    /// When the target is itself a match it becomes the current match; otherwise - e.g. an
    /// original-text-only hit whose corrected text no longer contains the term - the current match
    /// advances to the first match AFTER the target, and is left unchanged only when no later match
    /// exists. Either way the caller still scrolls the window to the target (B4-4: doc drift).</summary>
    public void MoveFindTo(int rowIndex)
    {
        int target = IsEditMode ? EditSectionIndexOfRow(rowIndex) : rowIndex;
        if (target < 0) return;
        if (_findMatchRows.Contains(target)) { CurrentFindRowIndex = target; return; }
        int after = _findMatchRows.FirstOrDefault(i => i > target, -1);
        if (after >= 0) CurrentFindRowIndex = after;
    }

    /// <summary>Mode-aware (item 1): read mode scans Rows (markers included - find-on-page over
    /// what the reader sees); edit mode scans EditSections' SearchText (live buffer for expanded
    /// sections, loaded text for collapsed; markers are absent there, so they drop out of the
    /// count). _findMatchRows and CurrentFindRowIndex are Rows-space indices in read mode and
    /// EditSections-space indices in edit mode - they NEVER transfer across a mode switch, the
    /// transition callers re-map by row identity instead.</summary>
    private void RecomputeFindMatches(bool moveToFirst)
    {
        foreach (var r in Rows) { r.IsFindMatch = false; r.IsCurrentFindMatch = false; }
        foreach (var s in EditSections) { s.IsFindMatch = false; s.IsCurrentFindMatch = false; }
        _findMatchRows.Clear();
        string needle = FindText.Trim();
        if (!IsFindOpen || needle.Length == 0)
        {
            CurrentFindRowIndex = -1;
            FindStatus = "";
            return;
        }
        int count = IsEditMode ? EditSections.Count : Rows.Count;
        for (int i = 0; i < count; i++)
        {
            bool hit = IsEditMode
                ? EditSections[i].SearchText.Contains(needle, StringComparison.OrdinalIgnoreCase)
                : Rows[i].Data.Text.Contains(needle, StringComparison.OrdinalIgnoreCase);
            if (!hit) continue;
            _findMatchRows.Add(i);
            if (IsEditMode) EditSections[i].IsFindMatch = true;
            else Rows[i].IsFindMatch = true;
        }
        int current = -1;
        if (_findMatchRows.Count > 0)
            current = !moveToFirst && _findMatchRows.Contains(CurrentFindRowIndex)
                ? CurrentFindRowIndex
                : _findMatchRows[0];
        if (CurrentFindRowIndex == current)
        {
            // Unchanged index: the property setter won't fire, so re-stamp + refresh explicitly.
            if (current >= 0)
            {
                if (IsEditMode) EditSections[current].IsCurrentFindMatch = true;
                else Rows[current].IsCurrentFindMatch = true;
            }
            UpdateFindStatus();
        }
        else CurrentFindRowIndex = current;
    }

    /// <summary>Index of the section wrapping this EXACT DisplayRow instance. ReferenceEquals is
    /// mandatory: DisplayRow is a record (value equality) and two different rows can compare
    /// equal - the section wraps the same instance EnterEditMode read out of Rows.</summary>
    private int EditSectionIndexOf(DisplayRow data)
    {
        for (int i = 0; i < EditSections.Count; i++)
            if (ReferenceEquals(EditSections[i].Row, data)) return i;
        return -1;
    }

    private void UpdateFindStatus()
        => FindStatus = _findMatchRows.Count == 0
            ? (FindText.Trim().Length == 0 || !IsFindOpen ? "" : "0/0")
            : $"{_findMatchRows.IndexOf(CurrentFindRowIndex) + 1}/{_findMatchRows.Count}";

    private sealed record LoadedView(SessionRecord Session, SessionMeta Meta, Speakers? Speakers,
        IReadOnlyList<string> MatterDisplays, IReadOnlyList<DisplayRow> Rows,
        bool HasDegraded, DateTimeOffset StartedLocal, string VersionId);

    public async Task LoadAsync(string sessionId, CancellationToken ct)
    {
        SessionId = sessionId;
        try
        {
            var settings = _settings.Current;
            var view = await _maintenance.RunForSessionAsync(sessionId,
                token => LoadViewAsync(sessionId, settings, token), ct);
            _dispatch(() => Apply(view, settings));
        }
        catch (Exception ex) { _reporter.Report("Open read view", ex); }
    }

    /// <summary>Stage 6.1: refresh the transcript rows (and everything derived from truth files)
    /// after an in-window correction/pin save - WITHOUT re-running Playback.Resolve, which would
    /// re-subscribe MediaPlayer events (DualMediaPlayer.Load adds handlers per call) and restart
    /// the playing position. Playback and window chrome keep their state; rows, the Edited badge,
    /// and the editor snapshots come back fresh from disk under the same per-session gate.</summary>
    public async Task ReloadRowsAsync(CancellationToken ct)
    {
        try
        {
            var settings = _settings.Current;
            var view = await _maintenance.RunForSessionAsync(SessionId,
                token => LoadViewAsync(SessionId, settings, token), ct);
            _dispatch(() => ApplyRows(view, settings));
        }
        catch (Exception ex) { _reporter.Report("Refresh read view", ex); }
    }

    private async Task<LoadedView> LoadViewAsync(string sessionId, Settings settings,
        CancellationToken token)
    {
        var loaded = await SessionProjectionLoader.LoadAsync(_paths, settings, _time, sessionId, ct: token);

        // Mid-session degradation exists only as a transcript marker (design 3.2/5) - the list
        // badge cannot see it, so the read view surfaces it. Read off loaded.Lines (the raw
        // transcript.jsonl) to preserve the exact prior semantics.
        bool degraded = loaded.Lines.Any(l =>
            l.Kind == TranscriptKind.Marker && l.Text == Markers.DegradedSystemAudioLoopback);

        return new LoadedView(loaded.Session, loaded.Meta, loaded.Speakers, loaded.MatterDisplays,
            loaded.Rows, degraded, loaded.StartedLocal, loaded.VersionId);
    }

    private void Apply(LoadedView view, Settings settings)
    {
        Title = view.Meta.Title;
        DateDisplay = view.StartedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var span = TimeSpan.FromMilliseconds(view.Session.DurationMs);
        DurationDisplay = span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss",
            CultureInfo.InvariantCulture);
        Recovered = view.Session.Recovered;
        // Same rule as the Task 15 list badge: chosen systemMix has identical bleed
        // characteristics to a fallback (design 3.2).
        SystemMix = view.Session.Devices.Remote.Mode == RemoteMode.SystemMix
                    || view.Session.Devices.Remote.FellBackToSystemMix;
        HasDegradedMarker = view.HasDegraded;
        TimestampsMode = settings.Timestamps;
        StartedAtLocal = view.StartedLocal;
        ApplyRows(view, settings);
        Playback.Resolve(_paths, SessionId, view.Session.RetainedAudioSources, settings.AudioFormat);
        IsLoaded = true;
    }

    /// <summary>The truth-derived half of Apply, shared with ReloadRowsAsync: rows, badges,
    /// display lists, editor snapshots, diarise gate - everything EXCEPT playback resolution
    /// and the load-once header fields.</summary>
    private void ApplyRows(LoadedView view, Settings settings)
    {
        _loadedMeta = view.Meta;
        _loadedSpeakers = view.Speakers;
        _loadedVersionId = view.VersionId;
        Edited = view.Meta.Edited;
        MatterDisplays.Clear();
        foreach (string m in view.MatterDisplays) MatterDisplays.Add(m);
        ParticipantDisplays.Clear();
        foreach (var p in view.Meta.Participants)
            ParticipantDisplays.Add(string.IsNullOrEmpty(p.Role)
                ? $"{p.Name} ({p.Side})" : $"{p.Name} ({p.Role}, {p.Side})");        // SessionWriter's format
        Rows.Clear();
        foreach (var r in view.Rows) Rows.Add(new ReadRow(r));
        RestoreNowPlaying();
        if (IsFindOpen) RecomputeFindMatches(moveToFirst: false);   // flags live on the NEW rows
        CanDiarise = view.Session.EndedAtUtc is not null &&
            ((view.Meta.LocalCount > 1 && LegRetainedOnDisk(SourceKind.Local,
                    view.Session.RetainedAudioSources, settings.AudioFormat))
                || (view.Meta.RemoteCount > 1 && LegRetainedOnDisk(SourceKind.Remote,
                    view.Session.RetainedAudioSources, settings.AudioFormat)));
        CanEdit = view.Session.EndedAtUtc is not null;

        // Version badge + switcher + footer (design 2026-07-13 section 3.4): options are v1 (the
        // root original) + every recorded version; the footer shows the ACTIVE version's actuals.
        _syncingVersions = true;
        try
        {
            var session = view.Session;
            VersionOptions.Clear();
            VersionOptions.Add(new VersionOption(TranscriptVersions.Root, $"v1 \u00B7 {session.Model}"));
            foreach (var v in session.Versions)
                VersionOptions.Add(new VersionOption(v.Id,
                    $"{TranscriptVersions.ShortId(v.Id)} \u00B7 {v.Model}"));
            HasVersions = session.Versions.Count > 0;
            SelectedVersionOption = VersionOptions.FirstOrDefault(o => o.Id == session.ActiveVersion)
                ?? VersionOptions[0];
            var active = session.Versions.FirstOrDefault(v => v.Id == session.ActiveVersion);
            ModelBackendFooter = active is null
                ? $"{session.Model} \u00B7 {session.Backend}"
                : $"{active.Model} \u00B7 {active.Backend}";
        }
        finally { _syncingVersions = false; }
    }

    /// <summary>Enters Edit mode (design §3.2): gated on CanEdit and not already editing, so a
    /// stray second call is a no-op rather than clobbering in-progress section edits. Builds one
    /// EditableSectionViewModel per non-marker row - markers have no segments to correct/split.
    /// Item 1 (UX round 2026-08-02): the find bar now SURVIVES the mode switch; matches recompute
    /// in EditSections space and the current match maps across by row identity.</summary>
    public void EnterEditMode()
    {
        if (!CanEdit || IsEditMode) return;
        SaveError = null;                     // clear any stale failure from a prior session
        var anchorData = CurrentFindRowIndex >= 0 && CurrentFindRowIndex < Rows.Count
            ? Rows[CurrentFindRowIndex].Data : null;
        EditSections.Clear();
        foreach (var r in Rows)
            if (!r.Data.IsMarker)
            {
                var section = new EditableSectionViewModel(r.Data);
                section.LiveTextChanged += ScheduleFindRecompute;   // item 1: live-corpus refresh
                EditSections.Add(section);
            }
        IsEditMode = true;
        if (IsFindOpen)
        {
            RecomputeFindMatches(moveToFirst: true);
            if (anchorData is not null)
            {
                int si = EditSectionIndexOf(anchorData);
                if (si >= 0 && _findMatchRows.Contains(si)) CurrentFindRowIndex = si;
            }
        }
    }

    /// <summary>Drops all in-progress section edits without writing anything (design §3.2). The
    /// find bar stays open (item 1); the current match maps back to the read row by identity -
    /// Rows was untouched, so the DisplayRow references are still live.</summary>
    public void CancelEdit()
    {
        SaveError = null;
        var anchorData = CurrentFindRowIndex >= 0 && CurrentFindRowIndex < EditSections.Count
            ? EditSections[CurrentFindRowIndex].Row : null;
        UnwireEditSections();
        EditSections.Clear();
        IsEditMode = false;
        if (IsFindOpen)
        {
            RecomputeFindMatches(moveToFirst: true);
            if (anchorData is not null)
                for (int i = 0; i < Rows.Count; i++)
                    if (ReferenceEquals(Rows[i].Data, anchorData) && _findMatchRows.Contains(i))
                    {
                        CurrentFindRowIndex = i;
                        break;
                    }
        }
    }

    /// <summary>Assembles one TranscriptEditBatch from every editing section's corrections/splits/
    /// split-reverts and writes it through MaintenanceService.SaveTranscriptEditsAsync (design
    /// §3.4), then reloads rows so the window shows the saved result. CollectCorrections already
    /// compares against ProjectedText (Task 11), so no extra vocabulary-diff threading is needed
    /// here. CorrectionReverts is always empty - the editor never produces a standalone correction
    /// revert. Whole-section speaker pins are Task 15's concern, not this batch. On failure the
    /// error is reported and Edit mode is left exactly as the user had it, so nothing is lost.
    ///
    /// Task 15: after the text/split batch lands, walk every editing section's UNSPLIT segments
    /// and pin any whose dropdown selection resolves to a real target (ToPinTarget non-null; the
    /// leading "(unchanged)" choice yields null and is a deliberate no-op). Split children never
    /// pin here - their speaker choice (if any) already rides along inside the SplitPartEdit the
    /// batch above wrote via CollectSplits/EditStore.ApplySplitAsync.</summary>
    public async Task SaveEditsAsync(CancellationToken ct)
    {
        // F1 fix (whole-branch review): snapshot the version this WHOLE edit session was
        // authored against, once, up front - every write below targets exactly this version,
        // never whatever ActiveVersion happens to be on disk when each individual write lands
        // (the switcher is disabled for the whole of Edit mode, so this cannot drift mid-save).
        string versionId = _loadedVersionId;
        var corrections = new Dictionary<int, string>();
        var splits = new List<SplitEdit>();
        var splitReverts = new HashSet<int>();
        foreach (var sec in EditSections.Where(s => s.IsEditing))
        {
            foreach (var kv in sec.CollectCorrections()) corrections[kv.Key] = kv.Value;
            splits.AddRange(sec.CollectSplits());
            foreach (int seq in sec.CollectSplitReverts()) splitReverts.Add(seq);
        }
        try
        {
            // Reassemble whole-seq splits before writing: a multi-speaker split's parts are shown
            // across several display sections (grouped by speaker), so a per-section CollectSplits
            // yields a PARTIAL slice - and a slice of only tail parts starts past the machine start,
            // which EditStore rejects, trapping the save. Merge each edited seq's parts over its
            // persisted full split by StartMs so the whole, machine-start-anchored split is written
            // (2026-08-02 gold-edit smoke, seq 69).
            var persistedSplits = await LoadPersistedSplitsAsync(versionId, ct);
            var wholeSplits = ReassembleWholeSeqSplits(splits, splitReverts, persistedSplits);
            var batch = new TranscriptEditBatch(corrections, [], wholeSplits, splitReverts.ToList());
            await _maintenance.SaveTranscriptEditsAsync(SessionId, batch, versionId, ct);
            foreach (var sec in EditSections.Where(s => s.IsEditing))
                foreach (var seg in sec.Segments.Where(x => !x.IsSplitChild))
                {
                    // Only write when the dropdown actually CHANGED from the pre-selected current
                    // speaker (compared by target, not display), so pre-selection never causes a
                    // redundant re-pin/regen on an untouched line. "Automatic (Me / Them)" removes
                    // the pin (baseline); a named target pins; RemoveSpeakerPinsAsync is a no-op when
                    // the seq isn't pinned.
                    if (SameSpeakerTarget(seg.Speaker, seg.OriginalSpeaker)) continue;
                    if (seg.Speaker is null || seg.Speaker.IsUnassign)
                        await _maintenance.RemoveSpeakerPinsAsync(SessionId, seg.Source, [seg.Seq], versionId, ct);
                    else if (seg.Speaker.ToPinTarget() is { } target)
                        await _maintenance.SaveSpeakerPinsAsync(SessionId, seg.Source, [seg.Seq], target, versionId, ct);
                }
            await ReloadRowsAsync(ct);
        }
        catch (Exception ex)
        {
            // Surface IN this window and stay in edit mode so nothing is lost. Previously the only
            // report went to MainWindow's InfoBar (invisible from here), so the failure looked
            // silent and the user was stuck with no reason (2026-08-02 gold-edit smoke).
            SaveError = "Couldn't save your transcript edits: " + ex.Message +
                        " Your edits are still here - fix the flagged segment and Save again, or Cancel.";
            return;
        }
        SaveError = null;
        IsEditMode = false;
        UnwireEditSections();
        EditSections.Clear();
        // Item 1: ApplyRows' own recompute (inside ReloadRowsAsync above) ran while IsEditMode was
        // still true, i.e. against the now-discarded sections - recompute once more in read space
        // so flags/status land on the reloaded rows. Rows were REBUILT, so identity mapping is
        // impossible here; moveToFirst:false keeps the index when it is still a match.
        if (IsFindOpen) RecomputeFindMatches(moveToFirst: false);
    }

    /// <summary>The persisted splits for the version being edited, keyed by seq, read fresh at save
    /// time - the authoritative baseline the per-section edited parts are merged onto.</summary>
    private async Task<IReadOnlyDictionary<int, SplitEntry>> LoadPersistedSplitsAsync(
        string versionId, CancellationToken ct)
    {
        var edits = await new EditStore(_paths.SessionDir(SessionId), _time,
            contentDir: _paths.VersionDir(SessionId, versionId)).LoadAsync(ct);
        return edits is null
            ? new Dictionary<int, SplitEntry>()
            : edits.Splits.ToDictionary(kv => int.Parse(kv.Key, CultureInfo.InvariantCulture), kv => kv.Value);
    }

    /// <summary>Combine each edited seq's collected parts (possibly just one display section's slice
    /// of a multi-speaker split) with the seq's persisted parts, keyed by StartMs (stable and unique
    /// within a seq): persisted is the baseline, edited overrides/adds, then order by start and
    /// re-anchor so the first part is the non-derived machine start. A reverted seq is skipped (its
    /// split is dropped wholesale). A seq with no persisted split (a fresh in-session split, whose
    /// parts all live in one section) passes through complete.</summary>
    private static IReadOnlyList<SplitEdit> ReassembleWholeSeqSplits(
        IReadOnlyList<SplitEdit> collected, IReadOnlyCollection<int> reverts,
        IReadOnlyDictionary<int, SplitEntry> persisted)
    {
        var result = new List<SplitEdit>();
        foreach (var group in collected.GroupBy(s => s.Seq))
        {
            if (reverts.Contains(group.Key)) continue;
            var byStart = new SortedDictionary<long, SplitPartEdit>();
            if (persisted.TryGetValue(group.Key, out var entry))
                foreach (var p in entry.Parts)
                    byStart[p.StartMs] = new SplitPartEdit(p.Text, p.StartMs, p.DerivedStart,
                        p.SpeakerParticipantId, p.SpeakerClusterKey);
            foreach (var edited in group.SelectMany(s => s.Parts))
                byStart[edited.StartMs] = edited;                       // override / add by StartMs

            var parts = byStart.Values.ToList();
            for (int i = 0; i < parts.Count; i++)
                parts[i] = parts[i] with { DerivedStart = i > 0 };     // first = machine start, rest derived
            result.Add(new SplitEdit(group.Key, group.First().Source, parts));
        }
        return result;
    }

    /// <summary>Rows were rebuilt wholesale: the old IsNowPlaying flag lives on discarded
    /// objects. Re-stamp the current PlayingSectionIndex onto the new row (guarded - a
    /// correction can change the row count) so the highlight survives a reload; the next
    /// 150 ms tick recomputes it anyway.</summary>
    private void RestoreNowPlaying()
    {
        _nowPlayingRowIndex = -1;
        int idx = PlayingSectionIndex;
        if (idx >= 0 && idx < Rows.Count)
        {
            Rows[idx].IsNowPlaying = true;
            _nowPlayingRowIndex = idx;
        }
    }

    // Mirrors SplitSpeakersViewModel.ProbeLeg / PlaybackViewModel.Resolve's probe: retained +
    // on-disk format (preferred, then the other format), so a session recorded before a format
    // change still counts as splittable.
    private bool LegRetainedOnDisk(SourceKind kind, IReadOnlyList<SourceKind> retained, AudioFormat preferred)
    {
        if (!retained.Contains(kind)) return false;
        if (File.Exists(_paths.AudioFile(SessionId, kind, preferred))) return true;
        var other = preferred == AudioFormat.Flac ? AudioFormat.Wav : AudioFormat.Flac;
        return File.Exists(_paths.AudioFile(SessionId, kind, other));
    }

    /// <summary>Stage 6.1 dialog factories: null for an out-of-range index, a marker row (no
    /// segments), or before the first load. The window shows the returned VM in a modal plain
    /// Window and calls ReloadRowsAsync when it reports success.</summary>
    public CorrectTextViewModel? CreateCorrectionEditor(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= Rows.Count) return null;
        var segments = Rows[rowIndex].Data.Segments;
        if (segments.Count == 0) return null;
        // F1 fix: the dialog is modal over this window, so the switcher cannot fire while it is
        // open, but a background re-transcription completing mid-dialog still could - capture the
        // currently-loaded version now and thread it through, rather than letting the dialog's
        // Save re-resolve ActiveVersion at write time.
        return new CorrectTextViewModel(_maintenance, _reporter, SessionId, segments,
            TimestampsMode, StartedAtLocal, _loadedVersionId);
    }

    public ReassignSpeakerViewModel? CreateReassignEditor(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= Rows.Count || _loadedMeta is null) return null;
        var segments = Rows[rowIndex].Data.Segments;
        if (segments.Count == 0) return null;
        return new ReassignSpeakerViewModel(_maintenance, _reporter, SessionId,
            segments[0].Source, segments, _loadedMeta, _loadedSpeakers,
            TimestampsMode, StartedAtLocal, _loadedVersionId);
    }

    /// <summary>Bulk "reassign all of this speaker" (2026-07-30, broadened 2026-07-31): seeds the
    /// dialog with EVERY segment currently shown under the clicked row's speaker across the WHOLE
    /// transcript, not just this row, so one pass relabels a whole speaker at once. Two gather modes:
    /// an ASSIGNED row (a diarisation cluster OR a manual pin - both write Assignments[source][seq])
    /// gathers every seq mapped to the SAME clusterKey; an UNASSIGNED row (e.g. an import that
    /// detected "one voice", so every line renders under the default "Me"/"Them" with no overlay
    /// entry) has no key to gather by, so it falls back to the DISPLAYED LABEL - every line shown
    /// under the same name on this side. That fallback is what makes an all-"one-voice" import
    /// triageable: reopen per speaker, tick their lines, assign. Null only for a marker row (no
    /// segments / null label) or before the first load.</summary>
    public ReassignSpeakerViewModel? CreateReassignClusterEditor(int rowIndex)
    {
        // Only _loadedMeta is required (matching CreateReassignEditor). _loadedSpeakers is NULL until
        // a session has its first pin/diarisation overlay - the by-label fallback below needs none of
        // it, so requiring it here wrongly refused the whole feature on a pin-less "one voice" import.
        if (rowIndex < 0 || rowIndex >= Rows.Count || _loadedMeta is null)
            return null;
        var clickedRow = Rows[rowIndex].Data;
        var rowSegments = clickedRow.Segments;
        if (rowSegments.Count == 0) return null;
        var source = rowSegments[0].Source;

        List<RowSegment> gathered;
        if (_loadedSpeakers is not null
            && _loadedSpeakers.Assignments.TryGetValue(source.ToString(), out var bySeq)
            && bySeq.TryGetValue(rowSegments[0].Seq.ToString(), out var clusterKey))
        {
            // Assigned: every segment on this side whose seq maps to the same clusterKey, in
            // transcript order (Rows is already ordered). Covers both diarisation clusters and pins.
            gathered = Rows
                .SelectMany(r => r.Data.Segments)
                .Where(s => s.Source == source
                            && bySeq.TryGetValue(s.Seq.ToString(), out var k) && k == clusterKey)
                .ToList();
        }
        else
        {
            // Unassigned: no overlay key, so gather by the displayed label - every non-marker row on
            // this side currently shown under the same name (design 2026-07-31: bulk-triage a
            // "one voice" import). A null label is a marker and has nothing to gather.
            if (clickedRow.DisplayName is not { } label) return null;
            gathered = Rows
                .Where(r => !r.Data.IsMarker && r.Data.DisplayName == label)
                .SelectMany(r => r.Data.Segments)
                .Where(s => s.Source == source)
                .ToList();
        }
        if (gathered.Count == 0) return null;
        return new ReassignSpeakerViewModel(_maintenance, _reporter, SessionId,
            source, gathered, _loadedMeta, _loadedSpeakers,
            TimestampsMode, StartedAtLocal, _loadedVersionId);
    }

    /// <summary>Test seams (Task 12): the Edit-mode dropdown's candidate list for each side, built
    /// from the same loaded meta/speakers CreateReassignEditor uses.</summary>
    internal IReadOnlyList<SpeakerChoice> SpeakerChoicesForRemote() =>
        SpeakerChoices.Build(_loadedMeta!, _loadedSpeakers, TranscriptSource.Remote);
    internal IReadOnlyList<SpeakerChoice> SpeakerChoicesForLocal() =>
        SpeakerChoices.Build(_loadedMeta!, _loadedSpeakers, TranscriptSource.Local);

    /// <summary>Task 15: public source-dispatching wrapper over the two seams above, so the window's
    /// OnEditRowActivated can hand each expanded section the correct side's candidate list without
    /// caring which source a given segment carries. Only safe to call once loaded (relies on
    /// _loadedMeta!) - the Edit-mode dropdown that consumes this only ever renders after CanEdit,
    /// which requires a completed load, so that invariant always holds by the time this is called.</summary>
    public IReadOnlyList<SpeakerChoice> SpeakerChoicesForSource(TranscriptSource source) =>
        source == TranscriptSource.Local ? SpeakerChoicesForLocal() : SpeakerChoicesForRemote();

    /// <summary>The choice a line is currently attributed to, so BeginEdit pre-selects the dropdown
    /// to what's already there instead of blanking. Passed as BeginEdit's currentSpeaker resolver.</summary>
    public SpeakerChoice? CurrentSpeakerFor(int seq, TranscriptSource source,
        IReadOnlyList<SpeakerChoice> choices) =>
        _loadedMeta is null ? null : SpeakerChoices.CurrentFor(seq, source, choices, _loadedMeta, _loadedSpeakers);

    /// <summary>Two choices point at the SAME attribution target (participant / cluster / automatic
    /// baseline), ignoring display text - so a renamed participant (same id, new name) reads as
    /// "unchanged" and a rename never triggers a redundant re-pin.</summary>
    private static bool SameSpeakerTarget(SpeakerChoice? a, SpeakerChoice? b) =>
        (a?.IsUnassign ?? false) == (b?.IsUnassign ?? false)
        && string.Equals(a?.ParticipantId, b?.ParticipantId, StringComparison.Ordinal)
        && string.Equals(a?.ClusterKey, b?.ClusterKey, StringComparison.Ordinal);

    /// <summary>Task 17 live roster sync (design section 4): rebuild the loaded meta/speakers (and
    /// thus the speaker-choice lists) after Session Details changes the roster for THIS session,
    /// without a reopen. Reuses the gated reload (LoadViewAsync, under the maintenance per-session
    /// queue, same as LoadAsync/ReloadRowsAsync). Not in Edit mode: a full ReloadRowsAsync is safe
    /// (there is no in-progress edit state to protect) and also refreshes ParticipantDisplays/rows
    /// speaker labels. In Edit mode: EditSections must survive untouched (in-progress
    /// text/split edits would otherwise be silently discarded), so only _loadedMeta/_loadedSpeakers
    /// and each already-materialized segment's SpeakerChoices are refreshed.</summary>
    public async Task RefreshRosterAsync(CancellationToken ct)
    {
        if (!IsEditMode) { await ReloadRowsAsync(ct); return; }
        try
        {
            var settings = _settings.Current;
            var view = await _maintenance.RunForSessionAsync(SessionId,
                token => LoadViewAsync(SessionId, settings, token), ct);
            _dispatch(() =>
            {
                _loadedMeta = view.Meta;
                _loadedSpeakers = view.Speakers;
                var remoteChoices = SpeakerChoicesForRemote();
                var localChoices = SpeakerChoicesForLocal();
                foreach (var section in EditSections)
                    section.RefreshSpeakerChoices(remoteChoices, localChoices);
            });
        }
        catch (Exception ex) { _reporter.Report("Refresh roster", ex); }
    }

    /// <summary>Unpin every pinned segment of the row, grouped per source (a mixed-source turn
    /// unpins both streams). The window confirms first and reloads rows after.</summary>
    public async Task RemovePinsAsync(int rowIndex, CancellationToken ct)
    {
        if (rowIndex < 0 || rowIndex >= Rows.Count) return;
        string versionId = _loadedVersionId;    // F1 fix: target the currently-loaded version.
        try
        {
            foreach (var group in Rows[rowIndex].Data.Segments
                         .Where(s => s.IsPinned).GroupBy(s => s.Source))
                await _maintenance.RemoveSpeakerPinsAsync(SessionId, group.Key,
                    group.Select(s => s.Seq).ToList(), versionId, ct);
        }
        catch (Exception ex) { _reporter.Report("Remove speaker pin", ex); }
    }

    public void Dispose() => Playback.Dispose();
}
