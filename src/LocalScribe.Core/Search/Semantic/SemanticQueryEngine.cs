namespace LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Search;

/// <summary>One semantic hit: the chunk's anchor (click-through = ShowFindAt(StartSeq)) plus a
/// truncated snippet of the chunk text and its cosine score.</summary>
public sealed record SemanticHit(int StartSeq, int StartPartIndex, long StartMs,
    string Snippet, float Score);

/// <summary>One matched session for the Related section: lexical metadata entry + hits ordered by
/// score. BestScore is the session's rank key.</summary>
public sealed record SemanticResult(SearchSessionEntry Session,
    IReadOnlyList<SemanticHit> Hits, float BestScore);

/// <summary>Pure semantic query semantics (design 2026-07-25). Facets come from the LEXICAL
/// entries via SearchQueryEngine.PassesFacets (identical behavior in both sections). Vectors are
/// unit-normalized, so cosine = dot. Chunks under MinScore are noise - the section stays empty
/// rather than padding. A chunk whose [StartSeq, EndSeq] covers a lexical hit seq in the same
/// session is dropped (never show the same passage twice); the session itself may appear in both
/// sections pointing at different passages. No IO, no mutation.</summary>
public static class SemanticQueryEngine
{
    public const float MinScore = 0.55f;   // tuning constant - calibrated in real-model smoke
    public const int MaxChunks = 40;
    public const int SnippetChars = 160;

    public static IReadOnlyList<SemanticResult> Run(float[] queryVector,
        IReadOnlyDictionary<string, SearchSessionEntry> metadata,
        IReadOnlyDictionary<string, SemanticSidecar> sidecars,
        SearchQuery query,
        IReadOnlyList<SearchResult> lexicalResults)
    {
        if (queryVector.Length == 0) return [];
        var lexicalSeqs = lexicalResults.ToDictionary(r => r.Session.SessionId,
            r => r.Hits.Where(h => h.Seq >= 0).Select(h => h.Seq).ToHashSet(),
            StringComparer.Ordinal);

        var scored = new List<(string SessionId, SemanticChunk Chunk, float Score)>();
        foreach (var (sessionId, sidecar) in sidecars)
        {
            if (!metadata.TryGetValue(sessionId, out var meta)) continue;      // not eligible
            if (!SearchQueryEngine.PassesFacets(meta, query)) continue;
            for (int i = 0; i < sidecar.Vectors.Count && i < sidecar.Chunks.Count; i++)
            {
                float score = Dot(queryVector, sidecar.Vectors[i]);
                if (score < MinScore) continue;
                var chunk = sidecar.Chunks[i];
                if (lexicalSeqs.TryGetValue(sessionId, out var seqs)
                    && seqs.Any(s => s >= chunk.StartSeq && s <= chunk.EndSeq))
                    continue;                                                   // shown as exact already
                scored.Add((sessionId, chunk, score));
            }
        }

        var top = scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.SessionId, StringComparer.Ordinal)
            .ThenBy(x => x.Chunk.StartSeq)
            .Take(MaxChunks)
            .ToList();

        return top
            .GroupBy(x => x.SessionId, StringComparer.Ordinal)
            .Select(g => new SemanticResult(metadata[g.Key],
                g.OrderByDescending(x => x.Score).ThenBy(x => x.Chunk.StartSeq)
                    .Select(x => new SemanticHit(x.Chunk.StartSeq, x.Chunk.StartPartIndex,
                        x.Chunk.StartMs, Snippet(x.Chunk.Text), x.Score))
                    .ToList(),
                g.Max(x => x.Score)))
            .OrderByDescending(r => r.BestScore)
            .ThenBy(r => r.Session.SessionId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Plain dot product (vectors are unit-normalized). ~40M mul-adds for a full corpus
    /// scan - tens of ms. If that ever bites, System.Numerics.Tensors is the escape hatch.</summary>
    public static float Dot(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        float s = 0;
        for (int i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }

    public static string Snippet(string text)
    {
        string flat = text.Replace('\n', ' ');
        return flat.Length <= SnippetChars ? flat : flat[..SnippetChars] + "…";
    }
}
