namespace LocalScribe.Core.Model;

/// <summary>matters/matters.json - lightweight index for the Matter picker (spec section 1.5).</summary>
public sealed record MattersIndex
{
    public int SchemaVersion { get; init; } = 2;
    public IReadOnlyList<MattersIndexEntry> Matters { get; init; } = [];
}

public sealed record MattersIndexEntry
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Reference { get; init; }
    public int SessionCount { get; init; }

    /// <summary>v2 (Stage 4): mirrors matter.json Archived for list rendering (design section 8).</summary>
    public bool Archived { get; init; }
}
