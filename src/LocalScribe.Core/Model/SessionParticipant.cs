using LocalScribe.Core.Audio;

namespace LocalScribe.Core.Model;

/// <summary>A session's participant snapshot (spec section 1.4/section 10). clusterKey is reserved (null in v1).</summary>
public sealed record SessionParticipant
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public SourceKind Side { get; init; }
    public string? Role { get; init; }
    public bool IsSelf { get; init; }
    public string? ClusterKey { get; init; }
}
