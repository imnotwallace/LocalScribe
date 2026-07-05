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
public sealed class TranscriptLinesViewModel
{
    private readonly Action<Action> _dispatch;
    private readonly ISettingsService _settings;
    private SessionState _lastState = SessionState.Idle;

    public ObservableCollection<TranscriptLineViewModel> Lines { get; } = [];

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
            if (s == SessionState.Recording && _lastState == SessionState.Idle) Clear();
            _lastState = s;
        });
    }

    public void Clear() => Lines.Clear();

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
    }

    private static TranscriptLineViewModel MapRow(DisplayRow r)
    {
        var ts = TimeSpan.FromMilliseconds(r.StartMs);
        string stamp = ts.ToString(ts.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
        return new TranscriptLineViewModel(stamp, r.IsMarker ? "" : (r.DisplayName ?? ""), r.Text, r.IsMarker);
    }
}
