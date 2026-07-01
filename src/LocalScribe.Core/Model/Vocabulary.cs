namespace LocalScribe.Core.Model;

/// <summary>A custom-vocabulary layer: bias terms + a deterministic heard->correct map (spec section 1.7/section 10).</summary>
public sealed record Vocabulary
{
    public IReadOnlyList<string> Terms { get; init; } = [];
    public IReadOnlyDictionary<string, string> Corrections { get; init; } = new Dictionary<string, string>();
}
