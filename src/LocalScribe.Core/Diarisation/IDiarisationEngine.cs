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
    int? ForcedClusterCount);

public sealed record DiarisedSegment(long StartMs, long EndMs, int Cluster);

public sealed record DiarisationResult(
    IReadOnlyList<DiarisedSegment> Segments, int ClusterCount, string Method);
