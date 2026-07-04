using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;

namespace LocalScribe.Core.Diarisation;

/// <summary>Merges a fresh diarisation run (<see cref="DiarisationCommit"/>) into the existing
/// <see cref="Speakers"/> record without ever mutating either input. Manual pins (assignment and
/// name) survive re-diarisation verbatim; because diarisation cluster ids restart at 0 each run, a
/// fresh clusterKey can collide with a surviving pinned clusterKey - such fresh keys are remapped
/// to unused ids so a different speaker can never inherit a pinned speaker's key or name.</summary>
public static class SpeakersMerge
{
    /// <summary>Returns a new <see cref="Speakers"/> combining <paramref name="existing"/> with the
    /// re-diarised <paramref name="commit"/>. Pinned seqs keep their old assignment and name; every
    /// non-pinned assignment/name for a re-diarised source is replaced by the fresh run. Neither
    /// <paramref name="existing"/> nor <paramref name="commit"/> is mutated.</summary>
    public static Speakers Merge(Speakers? existing, DiarisationCommit commit)
    {
        existing ??= new Speakers();
        var reSources = commit.Sources.Select(s => s.ToString()).ToHashSet(); // "Local"/"Remote"

        // Collision-avoidance remap: fresh clusterKeys restart at 0 each run, so a fresh key can
        // equal a surviving pinned key (a DIFFERENT speaker). Remap those fresh keys to unused ids
        // per source, deterministically, before applying the commit. Keyed by full "Source:id"
        // clusterKey (source-prefixed, so unambiguous across sources); non-colliding keys untouched.
        var remap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in commit.Sources)
        {
            var sourceKey = source.ToString();

            // clusterKeys this source's pinned seqs currently map to (preserved verbatim by merge).
            var pinnedKeysForSource = new HashSet<string>(StringComparer.Ordinal);
            if (existing.Pinned.TryGetValue(sourceKey, out var pinnedSeqList) &&
                existing.Assignments.TryGetValue(sourceKey, out var existingBySeq))
                foreach (var seq in pinnedSeqList)
                    if (existingBySeq.TryGetValue(seq, out var ck)) pinnedKeysForSource.Add(ck);
            if (pinnedKeysForSource.Count == 0) continue; // nothing for a fresh key to collide with

            // Fresh clusterKeys this source's commit produces: assignment values + own Names keys.
            var freshKeys = new HashSet<string>(StringComparer.Ordinal);
            if (commit.Assignments.TryGetValue(sourceKey, out var freshBySeq))
                foreach (var ck in freshBySeq.Values) freshKeys.Add(ck);
            foreach (var ck in commit.Names.Keys)
                if (ck.StartsWith(sourceKey + ":", StringComparison.Ordinal)) freshKeys.Add(ck);

            var colliding = freshKeys.Where(pinnedKeysForSource.Contains).ToList();
            if (colliding.Count == 0) continue;

            // New ids start above the max id seen among all pinned+fresh keys for this source, so a
            // remapped key cannot collide with any pinned id, any fresh id, or another new id.
            var maxId = -1;
            foreach (var ck in pinnedKeysForSource) maxId = Math.Max(maxId, ParseId(ck));
            foreach (var ck in freshKeys) maxId = Math.Max(maxId, ParseId(ck));

            var nextId = maxId + 1;
            foreach (var oldKey in colliding.OrderBy(ParseId))
                remap[oldKey] = $"{sourceKey}:{nextId++}";
        }

        // Apply the remap to a COPY of the commit's Assignments (values) and Names (keys). The
        // passed-in commit is never mutated; non-colliding keys pass through unchanged.
        var commitAssignments = commit.Assignments;
        var commitNames = commit.Names;
        if (remap.Count > 0)
        {
            var remappedAssignments = new Dictionary<string, IReadOnlyDictionary<string, string>>();
            foreach (var (src, bySeq) in commit.Assignments)
            {
                var copy = new Dictionary<string, string>();
                foreach (var (seq, ck) in bySeq)
                    copy[seq] = remap.TryGetValue(ck, out var nk) ? nk : ck;
                remappedAssignments[src] = copy;
            }
            commitAssignments = remappedAssignments;

            var remappedNames = new Dictionary<string, string>();
            foreach (var (ck, name) in commit.Names)
                remappedNames[remap.TryGetValue(ck, out var nk) ? nk : ck] = name;
            commitNames = remappedNames;
        }

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
            if (commitAssignments.TryGetValue(sourceKey, out var fresh))
                foreach (var (seq, ck) in fresh)
                    if (!pinnedSeqs.Contains(seq)) kept[seq] = ck;
            assignments[sourceKey] = kept;

            // Drop non-pinned Names whose clusterKey belongs to this source.
            foreach (var ck in names.Keys.ToList())
                if (ck.StartsWith(sourceKey + ":", StringComparison.Ordinal) && !pinnedClusterKeys.Contains(ck))
                    names.Remove(ck);
        }

        // Apply the run's names (defaults or user-typed), using the remapped commit. Defense in
        // depth: never overwrite a pinned clusterKey's name, and only overlay names for a
        // re-diarised source. After the remap a pinned collision cannot occur, but this guard keeps
        // a pinned name unassailable and stops a stray non-committed-source name from leaking in.
        foreach (var (ck, name) in commitNames)
            if (!pinnedClusterKeys.Contains(ck) &&
                reSources.Any(src => ck.StartsWith(src + ":", StringComparison.Ordinal)))
                names[ck] = name;

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

    // Parses the integer id from a "Source:id" clusterKey (suffix after the first ':').
    // Returns -1 for a malformed key so it never inflates the allocated-id ceiling.
    private static int ParseId(string clusterKey)
    {
        var idx = clusterKey.IndexOf(':');
        return idx >= 0 && idx + 1 < clusterKey.Length &&
               int.TryParse(clusterKey.AsSpan(idx + 1), out var id)
            ? id : -1;
    }
}
