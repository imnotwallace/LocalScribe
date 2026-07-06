using LocalScribe.Core.Audio;

namespace LocalScribe.Core.Model;

/// <summary>Whether a speaker slot carries a real name or is an explicit unnamed voice
/// (Stage 5.4 section 5.2). Unnamed slots have a stable Id/order, an empty Name, and render
/// "Speaker N"; they exist so a side's declared voice count == its slot count. Absent on the
/// wire in pre-5.4 metas; the init default keeps legacy participants Named.</summary>
public enum ParticipantKind { Named, Unnamed }

/// <summary>A session's participant snapshot (spec section 1.4/section 10). Stage 5.4:
/// ClusterKey is LIVE - when set, this slot durably owns that diarised cluster; NameResolver
/// labels the cluster's lines with the slot's Name (Named slots only) and SpeakersMerge
/// protects the owned key from fresh-run collisions like a pin.</summary>
public sealed record SessionParticipant
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public SourceKind Side { get; init; }
    public string? Role { get; init; }
    public bool IsSelf { get; init; }
    public string? ClusterKey { get; init; }
    public ParticipantKind Kind { get; init; } = ParticipantKind.Named;
}
