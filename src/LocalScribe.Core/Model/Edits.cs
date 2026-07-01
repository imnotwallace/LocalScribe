// src/LocalScribe.Core/Model/Edits.cs
namespace LocalScribe.Core.Model;

/// <summary>A single in-place text correction, keyed by the immutable seq (spec section 1.6).</summary>
public sealed record Correction
{
    public string Text { get; init; } = "";
    public DateTimeOffset EditedAtUtc { get; init; }
}

/// <summary>edits.json - non-destructive text-correction overlay (spec section 1.6). No tombstones/
/// hide/delete. Speaker corrections live in speakers.json, not here.</summary>
public sealed record Edits
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyDictionary<string, Correction> Corrections { get; init; } = new Dictionary<string, Correction>();
}
