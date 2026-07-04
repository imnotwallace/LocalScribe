using LocalScribe.Core.Audio;

namespace LocalScribe.Core.Diarisation;

public sealed record DiarisationCommit(
    IReadOnlyList<SourceKind> Sources,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Assignments, // "Local"/"Remote" -> seq -> clusterKey
    IReadOnlyDictionary<string, string> Names,                                    // clusterKey -> displayName
    string Method,
    DateTimeOffset DiarisedAtUtc);

public static class DefaultSpeakerLabels
{
    public static string For(SourceKind source, int clusterId) =>
        $"{source} Speaker {clusterId + 1}";   // 1-based, per-side ("Local"/"Remote")
}
