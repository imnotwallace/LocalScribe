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

    private sealed record LoadedView(SessionRecord Session, SessionMeta Meta, Speakers? Speakers,
        IReadOnlyList<string> MatterDisplays, IReadOnlyList<DisplayRow> Rows,
        bool HasDegraded, DateTimeOffset StartedLocal);

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
            loaded.Rows, degraded, loaded.StartedLocal);
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
        ModelBackendFooter = $"{view.Session.Model} \u00B7 {view.Session.Backend}";   // middle dot
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
        CanDiarise = view.Session.EndedAtUtc is not null &&
            ((view.Meta.LocalCount > 1 && LegRetainedOnDisk(SourceKind.Local,
                    view.Session.RetainedAudioSources, settings.AudioFormat))
                || (view.Meta.RemoteCount > 1 && LegRetainedOnDisk(SourceKind.Remote,
                    view.Session.RetainedAudioSources, settings.AudioFormat)));
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
        return new CorrectTextViewModel(_maintenance, _reporter, SessionId, segments,
            TimestampsMode, StartedAtLocal);
    }

    public ReassignSpeakerViewModel? CreateReassignEditor(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= Rows.Count || _loadedMeta is null) return null;
        var segments = Rows[rowIndex].Data.Segments;
        if (segments.Count == 0) return null;
        return new ReassignSpeakerViewModel(_maintenance, _reporter, SessionId,
            segments[0].Source, segments, _loadedMeta, _loadedSpeakers,
            TimestampsMode, StartedAtLocal);
    }

    /// <summary>Unpin every pinned segment of the row, grouped per source (a mixed-source turn
    /// unpins both streams). The window confirms first and reloads rows after.</summary>
    public async Task RemovePinsAsync(int rowIndex, CancellationToken ct)
    {
        if (rowIndex < 0 || rowIndex >= Rows.Count) return;
        try
        {
            foreach (var group in Rows[rowIndex].Data.Segments
                         .Where(s => s.IsPinned).GroupBy(s => s.Source))
                await _maintenance.RemoveSpeakerPinsAsync(SessionId, group.Key,
                    group.Select(s => s.Seq).ToList(), ct);
        }
        catch (Exception ex) { _reporter.Report("Remove speaker pin", ex); }
    }

    public void Dispose() => Playback.Dispose();
}
