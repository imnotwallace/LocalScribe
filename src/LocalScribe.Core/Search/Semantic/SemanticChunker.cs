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
            // StartSeq/EndSeq are the Related-discussion click-through ANCHOR (ShowFindAt) and the
            // [StartSeq, EndSeq] range SemanticQueryEngine dedups lexical hits against, so both must
            // come from MACHINE segments (whole-branch review, 2026-08-11). A human-authored segment
            // carries a placeholder Seq of 0 and leads any merged row it joins (InsertedRank -1), so
            // an inserted first piece anchored the whole chunk at seq 0 - the top of the transcript -
            // and widened the dedup range to swallow a legitimate lexical hit on machine seq 0.
            // Resolving by insertion id instead would mean a schema change to the persisted sidecar
            // (SemanticIndexStore writes these ints positionally) for a surface whose snippet already
            // carries the passage; -1/-1 for an all-inserted chunk degrades to "opens the read view
            // without scrolling" at the seq < 0 gate, and covers no lexical seq, which is correct.
            var firstMachine = FirstMachine(pieces, start, end, step: 1);
            var lastMachine = FirstMachine(pieces, end, start, step: -1);
            chunks.Add(new SemanticChunk(firstMachine?.Seq ?? -1, firstMachine?.PartIndex ?? 0,
                first.StartMs, lastMachine?.Seq ?? -1, last.EndMs, sb.ToString().TrimEnd('\n')));
            if (end == pieces.Count - 1) break;               // tail reached - done
            i = end > start ? end : end + 1;                  // overlap by one; never re-pack a lone segment
        }
        return chunks;
    }

    /// <summary>The first machine (non-inserted) segment scanning from <paramref name="from"/>
    /// toward <paramref name="to"/> inclusive, or null when the whole span is human-authored.</summary>
    private static RowSegment? FirstMachine(List<(RowSegment Seg, string Speaker)> pieces,
        int from, int to, int step)
    {
        for (int i = from; step > 0 ? i <= to : i >= to; i += step)
            if (pieces[i].Seg.InsertionId is null) return pieces[i].Seg;
        return null;
    }
}
