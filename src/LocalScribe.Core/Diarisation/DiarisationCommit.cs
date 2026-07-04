using LocalScribe.Core.Audio;

namespace LocalScribe.Core.Diarisation;

/// <summary>The immutable result of one diarisation run, ready to merge into <c>speakers.json</c>
/// via <see cref="SpeakersMerge"/>. <see cref="Sources"/> names the re-diarised sides; cluster ids
/// restart at 0 each run, so clusterKeys are only unique within a single commit.</summary>
public sealed record DiarisationCommit(
    IReadOnlyList<SourceKind> Sources,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Assignments, // "Local"/"Remote" -> seq -> clusterKey
    IReadOnlyDictionary<string, string> Names,                                    // clusterKey -> displayName
    string Method,
    DateTimeOffset DiarisedAtUtc);

/// <summary>Default per-side display labels for freshly diarised clusters (before any manual
/// rename). Labels are 1-based and scoped per source, e.g. "Remote Speaker 1".</summary>
public static class DefaultSpeakerLabels
{
    /// <summary>The default label for cluster <paramref name="clusterId"/> (0-based) on
    /// <paramref name="source"/>, rendered 1-based, e.g. <c>For(Remote, 0)</c> => "Remote Speaker 1".</summary>
    public static string For(SourceKind source, int clusterId) =>
        $"{source} Speaker {clusterId + 1}";   // 1-based, per-side ("Local"/"Remote")
}
