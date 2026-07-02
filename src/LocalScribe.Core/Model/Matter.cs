namespace LocalScribe.Core.Model;

/// <summary>A Matter roster member - the durable, reusable source of truth for a name (spec section 1.5/section 10).</summary>
public sealed record RosterMember
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Role { get; init; }
}

/// <summary>matter.json - the legal-case grouping, with a reusable participant roster and
/// per-Matter vocabulary (spec section 1.5). Session<->Matter is many-to-many via meta.matterIds.</summary>
public sealed record Matter
{
    public int SchemaVersion { get; init; } = 2;
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Reference { get; init; }
    public string? Description { get; init; }
    public DateTimeOffset DateCreatedUtc { get; init; }
    public IReadOnlyList<RosterMember> Roster { get; init; } = [];
    public Vocabulary Vocabulary { get; init; } = new();

    /// <summary>v2 (Stage 4): hidden from default lists only; does NOT cascade to sessions (design 4.1).</summary>
    public bool Archived { get; init; }
}
