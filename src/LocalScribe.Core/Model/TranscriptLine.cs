namespace LocalScribe.Core.Model;

/// <summary>One line of transcript.jsonl (source of truth, append-only). Two kinds
/// discriminated by <see cref="Kind"/>: transcribed segments and system markers (spec section 1.1).
/// An absent "kind" on read defaults to Segment (back-compat).</summary>
public sealed record TranscriptLine
{
    public int Seq { get; init; }
    public TranscriptKind Kind { get; init; } = TranscriptKind.Segment;
    public TranscriptSource Source { get; init; }
    public long StartMs { get; init; }
    public long EndMs { get; init; }
    public string Text { get; init; } = "";
    public string? SpeakerLabel { get; init; }
    public string? Lang { get; init; }
    public double? NoSpeechProb { get; init; }

    public static TranscriptLine Segment(int seq, TranscriptSource source, long startMs, long endMs,
        string text, string speakerLabel, string? lang = null, double? noSpeechProb = null)
        => new()
        {
            Seq = seq, Kind = TranscriptKind.Segment, Source = source,
            StartMs = startMs, EndMs = endMs, Text = text,
            SpeakerLabel = speakerLabel, Lang = lang, NoSpeechProb = noSpeechProb,
        };

    public static TranscriptLine Marker(int seq, long atMs, string message)
        => new()
        {
            Seq = seq, Kind = TranscriptKind.Marker, Source = TranscriptSource.System,
            StartMs = atMs, EndMs = atMs, Text = message,
        };
}
