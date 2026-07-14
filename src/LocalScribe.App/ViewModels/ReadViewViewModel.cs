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

    public ReadViewViewModel(MaintenanceService maintenance, StoragePaths paths,
        ISettingsService settings, IUiErrorReporter reporter, IDualAudioPlayer player,
        Action<Action> dispatch, TimeProvider time)
    {
        (_maintenance, _paths, _settings, _reporter, _dispatch, _time)
            = (maintenance, paths, settings, reporter, dispatch, time);
        Playback = new PlaybackViewModel(player, dispatch);
    }

    /// <summary>Called by the read-view window's ~150 ms timer: advance the transport, then
    /// recompute the highlighted section. Tests call it directly.</summary>
    public void TickPlayback()
    {
        Playback.Tick();
        PlayingSectionIndex = SectionAt(Playback.PositionMs);
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

    /// <summary>Click-to-jump: seek to the section's start and begin playing (design 4.1).</summary>
    public void JumpToSection(int index)
    {
        if (index < 0 || index >= Rows.Count) return;
        Playback.Seek(Rows[index].Data.StartMs);
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

    partial void OnFindTextChanged(string value) => RecomputeFindMatches(moveToFirst: true);

    partial void OnCurrentFindRowIndexChanged(int oldValue, int newValue)
    {
        if (oldValue >= 0 && oldValue < Rows.Count) Rows[oldValue].IsCurrentFindMatch = false;
        if (newValue >= 0 && newValue < Rows.Count) Rows[newValue].IsCurrentFindMatch = true;
        UpdateFindStatus();
    }

    /// <summary>Opens the find bar. No-op in Edit mode (the bar searches the READ list only).
    /// With initialText (the search page's click-through term) the text change recomputes matches;
    /// re-opening with the same text recomputes explicitly so flags land on the current rows.</summary>
    public void OpenFind(string? initialText = null)
    {
        if (IsEditMode) return;
        IsFindOpen = true;
        if (initialText is not null && initialText != FindText) FindText = initialText;
        else RecomputeFindMatches(moveToFirst: _findMatchRows.Count == 0);
    }

    public void CloseFind()
    {
        IsFindOpen = false;
        foreach (var r in Rows) { r.IsFindMatch = false; r.IsCurrentFindMatch = false; }
        _findMatchRows.Clear();
        CurrentFindRowIndex = -1;
        FindStatus = "";
        // FindText is deliberately kept so Ctrl+F re-opens on the same term.
    }

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

    /// <summary>Points the current match at the given row (search-page click-through). When the
    /// target row is itself a match it becomes the current match; otherwise - e.g. an
    /// original-text-only hit whose corrected text no longer contains the term - the current match
    /// advances to the first match AFTER the target, and is left unchanged only when no later match
    /// exists. Either way the caller still scrolls the window to the target row (B4-4: doc drift).</summary>
    public void MoveFindTo(int rowIndex)
    {
        if (_findMatchRows.Contains(rowIndex)) { CurrentFindRowIndex = rowIndex; return; }
        int after = _findMatchRows.FirstOrDefault(i => i > rowIndex, -1);
        if (after >= 0) CurrentFindRowIndex = after;
    }

    private void RecomputeFindMatches(bool moveToFirst)
    {
        foreach (var r in Rows) { r.IsFindMatch = false; r.IsCurrentFindMatch = false; }
        _findMatchRows.Clear();
        string needle = FindText.Trim();
        if (!IsFindOpen || needle.Length == 0)
        {
            CurrentFindRowIndex = -1;
            FindStatus = "";
            return;
        }
        for (int i = 0; i < Rows.Count; i++)
            if (Rows[i].Data.Text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                _findMatchRows.Add(i);
                Rows[i].IsFindMatch = true;
            }
        int current = -1;
        if (_findMatchRows.Count > 0)
            current = !moveToFirst && _findMatchRows.Contains(CurrentFindRowIndex)
                ? CurrentFindRowIndex
                : _findMatchRows[0];
        if (CurrentFindRowIndex == current)
        {
            // Unchanged index: the property setter won't fire, so re-stamp + refresh explicitly.
            if (current >= 0) Rows[current].IsCurrentFindMatch = true;
            UpdateFindStatus();
        }
        else CurrentFindRowIndex = current;
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
        var loaded = await SessionProjectionLoader.LoadAsync(_paths, settings, _time, sessionId, token);

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
    /// EditableSectionViewModel per non-marker row - markers have no segments to correct/split.</summary>
    public void EnterEditMode()
    {
        if (!CanEdit || IsEditMode) return;
        CloseFind();                          // the find bar searches the read list only (design 2.2)
        EditSections.Clear();
        foreach (var r in Rows)
            if (!r.Data.IsMarker) EditSections.Add(new EditableSectionViewModel(r.Data));
        IsEditMode = true;
    }

    /// <summary>Drops all in-progress section edits without writing anything (design §3.2).</summary>
    public void CancelEdit()
    {
        EditSections.Clear();
        IsEditMode = false;
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
        var batch = new TranscriptEditBatch(corrections, [], splits, splitReverts.ToList());
        try
        {
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
        catch (Exception ex) { _reporter.Report("Save transcript edits", ex); return; }
        IsEditMode = false;
        EditSections.Clear();
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
