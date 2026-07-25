using System.Text;
using LocalScribe.Core.Projection;

namespace LocalScribe.Core.Search.Semantic;

/// <summary>Pure projection-rows -> chunks (design 2026-07-25). Greedy pack up to ~TargetChars
/// with ONE-SEGMENT overlap between adjacent chunks (a thought spanning a boundary is never
/// invisible to both); a single oversized segment becomes its own chunk (the model window, 2K
/// tokens, truncates at embed time - effectively never). Markers and whitespace-only segments
/// are excluded, matching SearchIndexBuilder's marker rule.</summary>
public static class SemanticChunker
{
    public const int TargetChars = 700;

    public static IReadOnlyList<SemanticChunk> Chunk(IReadOnlyList<DisplayRow> rows)
    {
        var pieces = new List<(RowSegment Seg, string Speaker)>();
        foreach (var row in rows)
        {
            if (row.IsMarker) continue;
            foreach (var seg in row.Segments)
                if (!string.IsNullOrWhiteSpace(seg.ProjectedText))
                    pieces.Add((seg, row.DisplayName ?? ""));
        }
        if (pieces.Count == 0) return [];

        var chunks = new List<SemanticChunk>();
        int i = 0;
        while (i < pieces.Count)
        {
            var sb = new StringBuilder();
            string? lastSpeaker = null;
            int start = i, end = i;
            for (int j = i; j < pieces.Count; j++)
            {
                var (seg, speaker) = pieces[j];
                string prefix = speaker.Length > 0 && speaker != lastSpeaker ? speaker + ": " : "";
                string piece = prefix + seg.ProjectedText.Trim() + "\n";
                sb.Append(piece);
                lastSpeaker = speaker;
                end = j;
                if (sb.Length > TargetChars) break;
            }
            var first = pieces[start].Seg;
            var last = pieces[end].Seg;
            chunks.Add(new SemanticChunk(first.Seq, first.PartIndex, first.StartMs,
                last.Seq, last.EndMs, sb.ToString().TrimEnd('\n')));
            if (end == pieces.Count - 1) break;               // tail reached - done
            i = end > start ? end : end + 1;                  // overlap by one; never re-pack a lone segment
        }
        return chunks;
    }
}
