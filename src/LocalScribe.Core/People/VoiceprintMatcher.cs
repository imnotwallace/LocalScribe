using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;

namespace LocalScribe.Core.People;

/// <summary>One advisory match for a diarised cluster (voiceprint design 2026-07-25). A UI hint,
/// never an identification claim; the app never auto-assigns from it.</summary>
public sealed record VoiceprintSuggestion(string PersonId, string PersonName, double Score);

/// <summary>Pure matcher: cluster embeddings vs the candidate People pool. Thresholds are named
/// constants pending tuning against real audio (smoke runbook). At most one suggestion per
/// cluster; confusable candidates (top two within RunnerUpMargin) suppress the suggestion
/// entirely - silence beats a coin-flip.</summary>
public static class VoiceprintMatcher
{
    public const double SuggestThreshold = 0.55;
    public const double RunnerUpMargin = 0.05;

    public static IReadOnlyDictionary<string, VoiceprintSuggestion> Suggest(
        IReadOnlyDictionary<string, float[]> clusterEmbeddings,
        string method,
        IReadOnlyList<Person> candidates)
    {
        var result = new Dictionary<string, VoiceprintSuggestion>();
        foreach (var (clusterKey, probe) in clusterEmbeddings)
        {
            VoiceprintSuggestion? best = null;
            double runnerUp = double.MinValue;
            foreach (var person in candidates)
            {
                double score = double.MinValue;
                foreach (var e in person.Voiceprint)
                    if (string.Equals(e.Method, method, StringComparison.Ordinal))
                        score = Math.Max(score, VoiceprintMath.Cosine(probe, e.Embedding));
                if (score == double.MinValue) continue;      // no comparable enrollment - not a candidate

                if (best is null || score > best.Score)
                {
                    if (best is not null) runnerUp = Math.Max(runnerUp, best.Score);
                    best = new VoiceprintSuggestion(person.Id, person.Name, score);
                }
                else runnerUp = Math.Max(runnerUp, score);
            }
            if (best is null || best.Score < SuggestThreshold) continue;
            if (runnerUp != double.MinValue && best.Score - runnerUp < RunnerUpMargin) continue;
            result[clusterKey] = best;
        }
        return result;
    }
}
