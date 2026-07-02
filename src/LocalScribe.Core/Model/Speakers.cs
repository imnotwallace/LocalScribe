using LocalScribe.Core.Audio;
namespace LocalScribe.Core.Model;

/// <summary>speakers.json - diarisation clusters + name overrides + manual pinned assignments
/// (spec section 1.3). Non-destructive; absent until used. The sole speaker-name authority.</summary>
public sealed record Speakers
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyDictionary<string, string> Names { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, Dictionary<string, string>> Assignments { get; init; }
        = new Dictionary<string, Dictionary<string, string>>();
    public IReadOnlyDictionary<string, List<string>> Pinned { get; init; }
        = new Dictionary<string, List<string>>();
    public IReadOnlyList<SourceKind> DiarisedSources { get; init; } = [];
    public string? Method { get; init; }
    public DateTimeOffset? DiarisedAtUtc { get; init; }
    public IReadOnlyDictionary<string, double> Confidence { get; init; } = new Dictionary<string, double>();
}
