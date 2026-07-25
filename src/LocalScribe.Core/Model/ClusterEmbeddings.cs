namespace LocalScribe.Core.Model;

/// <summary>embeddings.json - per-cluster mean speaker embeddings captured at diarise time
/// (voiceprint design 2026-07-25). DERIVED biometric data: rebuildable (re-diarise / embed op),
/// deletable by the voiceprint purge, never evidence. Keys are FULL post-remap clusterKeys
/// ("Remote:0") - written only after SpeakersMerge's collision remap is applied, so an entry can
/// never point at a different voice than speakers.json does.</summary>
public sealed record ClusterEmbeddings
{
    public int SchemaVersion { get; init; } = 1;
    public string Method { get; init; } = "";
    public DateTimeOffset ExtractedAtUtc { get; init; }
    public IReadOnlyDictionary<string, float[]> Entries { get; init; } = new Dictionary<string, float[]>();
}
