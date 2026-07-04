using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalScribe.Core.Diarisation;

public sealed record DiarisationJob(
    string FlacPath,
    string Source,                 // "Local" / "Remote" (TranscriptSource string)
    string SegmentationModelPath,
    string EmbeddingModelPath,
    int? ForcedClusterCount);      // null = auto (threshold); N = force exactly N

public sealed record DiarisationProgress(double Progress);

public sealed record WireSegment(long StartMs, long EndMs, int Cluster);

public sealed record DiarisationResultPayload(
    IReadOnlyList<WireSegment> Segments,
    int ClusterCount,
    string Method);

public sealed record DiarisationErrorPayload(string Error, string? Detail);

public static class DiarisationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
