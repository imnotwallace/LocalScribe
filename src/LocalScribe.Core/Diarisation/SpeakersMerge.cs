using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;

namespace LocalScribe.Core.Diarisation;

public static class SpeakersMerge
{
    public static Speakers Merge(Speakers? existing, DiarisationCommit commit)
    {
        existing ??= new Speakers();
        var reSources = commit.Sources.Select(s => s.ToString()).ToHashSet(); // "Local"/"Remote"

        var assignments = existing.Assignments.ToDictionary(
            kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        var pinned = existing.Pinned.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value));
        var names = new Dictionary<string, string>(existing.Names);

        // clusterKeys still referenced by any pinned seq (across all sources) must keep their names.
        var pinnedClusterKeys = new HashSet<string>();
        foreach (var (src, seqs) in pinned)
            if (assignments.TryGetValue(src, out var bySeq))
                foreach (var seq in seqs)
                    if (bySeq.TryGetValue(seq, out var ck)) pinnedClusterKeys.Add(ck);

        foreach (var sourceKey in reSources)
        {
            var pinnedSeqs = pinned.TryGetValue(sourceKey, out var ps) ? new HashSet<string>(ps) : [];

            // Reset non-pinned assignments for this source: keep only pinned seqs...
            var kept = new Dictionary<string, string>();
            if (assignments.TryGetValue(sourceKey, out var old))
                foreach (var (seq, ck) in old)
                    if (pinnedSeqs.Contains(seq)) kept[seq] = ck;
            // ...then apply the new run (skip any seq that is pinned).
            if (commit.Assignments.TryGetValue(sourceKey, out var fresh))
                foreach (var (seq, ck) in fresh)
                    if (!pinnedSeqs.Contains(seq)) kept[seq] = ck;
            assignments[sourceKey] = kept;

            // Drop non-pinned Names whose clusterKey belongs to this source.
            foreach (var ck in names.Keys.ToList())
                if (ck.StartsWith(sourceKey + ":", StringComparison.Ordinal) && !pinnedClusterKeys.Contains(ck))
                    names.Remove(ck);
        }

        // Apply the run's names (defaults or user-typed).
        foreach (var (ck, name) in commit.Names) names[ck] = name;

        var diarisedSources = existing.DiarisedSources
            .Concat(commit.Sources).Distinct().ToList();

        return existing with
        {
            Assignments = assignments,
            Pinned = pinned,
            Names = names,
            DiarisedSources = diarisedSources,
            Method = commit.Method,
            DiarisedAtUtc = commit.DiarisedAtUtc,
        };
    }
}
