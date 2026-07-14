using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using LocalScribe.App.Services;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
namespace LocalScribe.App.ViewModels;

public sealed record TranscriptLineViewModel(string Timestamp, string Speaker, string Text, bool IsMarker);

/// <summary>Observable projection of the live merger view (design 5.4 4.2). On each finalized line
/// the whole sorted merger snapshot is re-grouped with the SAME SectionGrouper the read view uses,
/// so section boundaries match across live/read and a late out-of-order insert re-splits. The
/// snapshot is taken synchronously in the LineInserted handler (merger consumer thread) and applied
/// on the UI thread via the injected dispatch. WPF-free; display-only (transcript.jsonl untouched).</summary>
public sealed class TranscriptLinesViewModel : ObservableObject
{
    private readonly Action<Action> _dispatch;
    private readonly ISettingsService _settings;
    private SessionState _lastState = SessionState.Idle;

    public ObservableCollection<TranscriptLineViewModel> Lines { get; } = [];

    /// <summary>Empty-state hint (design 2026-07-13 section 5 item 1): true ONLY while the session
    /// is Recording and the live list has no lines yet. The XAML overlays "Listening - transcript
    /// appears a few seconds after speech." on the list and drops it at the FIRST line (segment or
    /// marker) or on Pause/Stop. Raised on every state flip and every list rebuild/clear.</summary>
    public bool ShowListeningHint => _lastState == SessionState.Recording && Lines.Count == 0;

    public TranscriptLinesViewModel(SessionController controller, ISettingsService settings, Action<Action> dispatch)
    {
        _dispatch = dispatch;
        _settings = settings;
        controller.LineInserted += (_, _) =>
        {
            var snapshot = controller.View.ToArray();     // consistent: taken on the consumer thread
            int gapMs = _settings.Current.SectionGapMs;
            _dispatch(() => RebuildFrom(snapshot, gapMs));
        };
        controller.StateChanged += s => _dispatch(() =>
        {
            // B1-6: update _lastState BEFORE Clear() so its own notification reflects the new state,
            // and raise ShowListeningHint exactly once. The old order raised a redundant no-op (with
            // the still-stale Idle) before the real flip. Clear() (which raises) covers the
            // enter-Recording case; every other transition raises here (section 5 item 1).
            bool enteringRecording = s == SessionState.Recording && _lastState == SessionState.Idle;
            _lastState = s;
            if (enteringRecording) Clear();                 // drops any stale lines + raises
            else OnPropertyChanged(nameof(ShowListeningHint));
        });
    }

    public void Clear()
    {
        Lines.Clear();
        OnPropertyChanged(nameof(ShowListeningHint));
    }

    /// <summary>Re-derives the entire live line list from a full sorted merger snapshot. Pure and
    /// directly testable; callers marshal it onto the UI thread via the dispatch seam.</summary>
    public void RebuildFrom(IReadOnlyList<TranscriptLine> view, int gapMs)
    {
        var pre = new List<PreRow>(view.Count);
        foreach (var l in view)
        {
            bool isMarker = l.Kind == TranscriptKind.Marker;
            pre.Add(new PreRow(l.StartMs, l.EndMs, 0, l.Seq,
                isMarker ? null : (l.SpeakerLabel ?? ""), l.Text, isMarker));
        }
        Lines.Clear();
        foreach (var r in SectionGrouper.Group(pre, gapMs))
            Lines.Add(MapRow(r));
        OnPropertyChanged(nameof(ShowListeningHint));   // the first line drops the hint
    }

    private static TranscriptLineViewModel MapRow(DisplayRow r)
    {
        var ts = TimeSpan.FromMilliseconds(r.StartMs);
        string stamp = ts.ToString(ts.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
        return new TranscriptLineViewModel(stamp, r.IsMarker ? "" : (r.DisplayName ?? ""), r.Text, r.IsMarker);
    }
}
