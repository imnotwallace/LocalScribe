using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalScribe.Core.Diarisation;

public sealed record DiarisationJob(
    string FlacPath,
    string Source,                 // "Local" / "Remote" (TranscriptSource string)
    string SegmentationModelPath,
    string EmbeddingModelPath,
    int? ForcedClusterCount,       // null = auto (threshold); N = force exactly N
    bool EmitEmbeddings = false);  // voiceprint design 2026-07-25: also emit per-cluster mean embeddings

public sealed record DiarisationProgress(double Progress);

public sealed record WireSegment(long StartMs, long EndMs, int Cluster);

public sealed record DiarisationResultPayload(
    IReadOnlyList<WireSegment> Segments,
    int ClusterCount,
    string Method,
    IReadOnlyDictionary<string, float[]>? ClusterEmbeddings = null,  // cluster id ("0") -> mean vector
    string? EmbeddingMethod = null);

public sealed record DiarisationErrorPayload(string Error, string? Detail);

/// <summary>The embed op (voiceprint design 2026-07-25): mean speaker embedding over the given
/// ranges of a FLAC leg. Routed by the helper on the presence of op=="embed"; a DiarisationJob
/// has no op property, so legacy jobs keep working unchanged.</summary>
public sealed record EmbedRange(long StartMs, long EndMs);

public sealed record EmbedJob(
    string Op,                      // always "embed"
    string FlacPath,
    IReadOnlyList<EmbedRange> Ranges,
    string EmbeddingModelPath);

public sealed record EmbedResultPayload(float[] Embedding, string Method);

public static class EmbeddingMethods
{
    /// <summary>The 3D-Speaker CAM++ zh-en model both diarisation clustering and voiceprint
    /// enrollment run on. Only same-method embeddings are comparable.</summary>
    public const string CampPlus = "campplus-zh-en";
}

public static class DiarisationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
