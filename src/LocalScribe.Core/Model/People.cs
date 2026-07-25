namespace LocalScribe.Core.Model;

/// <summary>One captured voiceprint sample (voiceprint design 2026-07-25). The vector is COPIED
/// from the source session's embeddings.json at enrollment, so per-session purges and re-diarises
/// never invalidate it. Full provenance kept for the People UI and for targeted deletion.</summary>
public sealed record VoiceprintEnrollment
{
    public string Id { get; init; } = "";
    public float[] Embedding { get; init; } = [];
    public string Method { get; init; } = "";
    public string SourceSessionId { get; init; } = "";
    public string SourceClusterKey { get; init; } = "";
    public DateTimeOffset EnrolledAtUtc { get; init; }
}

/// <summary>A globally-known person (voiceprint design 2026-07-25): the identity anchor
/// voiceprints attach to. Matter RosterMembers link here via RosterMember.PersonId.</summary>
public sealed record Person
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Role { get; init; }
    public string? Org { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
    public IReadOnlyList<VoiceprintEnrollment> Voiceprint { get; init; } = [];
}

/// <summary>people\people.json - the People registry. USER data (never derived/rebuildable):
/// enrollments are deletable individually, per-person, or via the global purge.</summary>
public sealed record PeopleRegistry
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<Person> People { get; init; } = [];
}
