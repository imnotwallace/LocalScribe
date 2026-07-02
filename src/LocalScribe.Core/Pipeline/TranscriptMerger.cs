using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
namespace LocalScribe.Core.Pipeline;

/// <summary>Sorted-insert merge by session clock (design; spec section 5). JSONL stays in
/// finalization (seq) order - the view is the render-time computation. Single-threaded
/// consumer: call only from the pipeline's consumer loop.</summary>
public sealed class TranscriptMerger
{
    private readonly TranscriptStore _store;
    private readonly List<TranscriptLine> _view = new();
    private int _nextSeq;

    public TranscriptMerger(TranscriptStore store) => _store = store;

    public IReadOnlyList<TranscriptLine> View => _view;
    public event Action<int, TranscriptLine>? LineInserted;

    public async Task InitializeAsync(CancellationToken ct)
        => _nextSeq = await _store.NextSeqAsync(ct);

    public async Task<TranscriptLine> AppendSegmentAsync(TranscribedSegment ts, CancellationToken ct)
    {
        var source = ts.Audio.Source == SourceKind.Local ? TranscriptSource.Local : TranscriptSource.Remote;
        string label = ts.Audio.Source == SourceKind.Local ? "Me" : "Them";
        var line = TranscriptLine.Segment(_nextSeq++, source, ts.Audio.StartMs, ts.Audio.EndMs,
            ts.Result.Text, label, ts.Result.DetectedLanguage, ts.Result.NoSpeechProb,
            Math.Round(SegmentAudio.RmsDb(ts.Audio.Pcm.Span), 1));
        await _store.AppendAsync(line, ct);
        Insert(line);
        return line;
    }

    public async Task<TranscriptLine> AppendMarkerAsync(string message, long atMs, CancellationToken ct)
    {
        var line = TranscriptLine.Marker(_nextSeq++, atMs, message);
        await _store.AppendAsync(line, ct);
        Insert(line);
        return line;
    }

    private void Insert(TranscriptLine line)
    {
        int i = FindInsertIndex(_view, line);
        _view.Insert(i, line);
        LineInserted?.Invoke(i, line);
    }

    /// <summary>Spec section 5 display order: startMs asc, then source (Local &lt; Remote &lt; System), then seq.</summary>
    public static int FindInsertIndex(IReadOnlyList<TranscriptLine> view, TranscriptLine line)
    {
        int lo = 0, hi = view.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (Compare(view[mid], line) <= 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static int Compare(TranscriptLine a, TranscriptLine b)
    {
        int c = a.StartMs.CompareTo(b.StartMs);
        if (c != 0) return c;
        c = Rank(a.Source).CompareTo(Rank(b.Source));
        return c != 0 ? c : a.Seq.CompareTo(b.Seq);
    }

    private static int Rank(TranscriptSource s)
        => s switch { TranscriptSource.Local => 0, TranscriptSource.Remote => 1, _ => 2 };
}
