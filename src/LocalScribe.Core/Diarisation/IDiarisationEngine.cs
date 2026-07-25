using LocalScribe.Core.Audio;

namespace LocalScribe.Core.Diarisation;

public interface IDiarisationEngine
{
    Task<DiarisationResult> DiariseAsync(
        DiarisationRequest request, IProgress<double> progress, CancellationToken ct);
}

public sealed record DiarisationRequest(
    string FlacPath, SourceKind Source,
    string SegmentationModelPath, string EmbeddingModelPath,
    int? ForcedClusterCount,
    bool EmitEmbeddings = false);

public sealed record DiarisedSegment(long StartMs, long EndMs, int Cluster);

public sealed record DiarisationResult(
    IReadOnlyList<DiarisedSegment> Segments, int ClusterCount, string Method,
    IReadOnlyDictionary<string, float[]>? ClusterEmbeddings = null,   // cluster id ("0") -> vector
    string? EmbeddingMethod = null);

/// <summary>Backfill embedding extraction (voiceprint design 2026-07-25): mean speaker embedding
/// over explicit ranges of a FLAC leg, for enrolling from sessions diarised before embeddings.json
/// existed. Same helper process as diarisation.</summary>
public interface IEmbeddingEngine
{
    Task<EmbedResult> EmbedAsync(EmbedRequest request, CancellationToken ct);
}

public sealed record EmbedRequest(
    string FlacPath, IReadOnlyList<EmbedRange> Ranges, string EmbeddingModelPath);

public sealed record EmbedResult(float[] Embedding, string Method);
