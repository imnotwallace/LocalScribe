using System.Collections.ObjectModel;
using System.Globalization;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
namespace LocalScribe.App.ViewModels;

public sealed record TranscriptLineViewModel(string Timestamp, string Speaker, string Text, bool IsMarker);

/// <summary>Observable projection of the live merger view (spec 5 live view): each finalized
/// line is inserted at the merger-computed sorted index - it may land BEHIND the newest line,
/// which is expected (the other stream's earlier utterance can finalize later). WPF-free.</summary>
public sealed class TranscriptLinesViewModel
{
    private readonly Action<Action> _dispatch;
    private SessionState _lastState = SessionState.Idle;

    public ObservableCollection<TranscriptLineViewModel> Lines { get; } = [];

    public TranscriptLinesViewModel(SessionController controller, Action<Action> dispatch)
    {
        _dispatch = dispatch;
        controller.LineInserted += (index, line) => _dispatch(() =>
            Lines.Insert(Math.Min(index, Lines.Count), Map(line)));
        controller.StateChanged += s => _dispatch(() =>
        {
            if (s == SessionState.Recording && _lastState == SessionState.Idle) Clear();
            _lastState = s;
        });
    }

    public void Clear() => Lines.Clear();

    private static TranscriptLineViewModel Map(TranscriptLine line)
    {
        var ts = TimeSpan.FromMilliseconds(line.StartMs);
        string stamp = ts.ToString(ts.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss", CultureInfo.InvariantCulture);
        return new TranscriptLineViewModel(stamp, line.SpeakerLabel ?? "",
            line.Text, line.Kind == TranscriptKind.Marker);
    }
}
