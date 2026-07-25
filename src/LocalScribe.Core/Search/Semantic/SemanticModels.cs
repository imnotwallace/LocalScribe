namespace LocalScribe.Core.Search.Semantic;

/// <summary>One embeddable chunk (design 2026-07-25): a greedy pack of consecutive non-marker
/// segments with speaker prefixes baked into Text. Anchors point at the FIRST segment so a hit
/// reuses the exact lexical click-through (ReadViewWindow.ShowFindAt(StartSeq, ...)); EndSeq/EndMs
/// bound the covered range for dedup against lexical hits and for the snippet timestamp.</summary>
public sealed record SemanticChunk(int StartSeq, int StartPartIndex, long StartMs,
    int EndSeq, long EndMs, string Text);

/// <summary>One session's semantic sidecar: staleness identity (Method + VersionId + Stamps -
/// the lexical freshness rule plus method gating) and parallel Chunks/Vectors lists
/// (Chunks.Count == Vectors.Count; every vector has length Dim, unit-normalized).</summary>
public sealed record SemanticSidecar(string Method, string VersionId,
    LocalScribe.Core.Search.SearchFreshnessStamps Stamps,
    int Dim, IReadOnlyList<SemanticChunk> Chunks, IReadOnlyList<float[]> Vectors);
