using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;

namespace LocalScribe.Core.Diarisation;

public sealed record ClusterAssignment(
    IReadOnlyDictionary<string, string> SeqToClusterKey,
    IReadOnlyList<string> ClusterKeys);

public static class ClusterAssigner
{
    public static ClusterAssignment Assign(
        IReadOnlyList<TranscriptLine> lines,
        IReadOnlyList<DiarisedSegment> segments,
        SourceKind source)
    {
        // speakers.json outer key uses the TranscriptSource string; Local/Remote match.
        var wanted = source == SourceKind.Local ? TranscriptSource.Local : TranscriptSource.Remote;
        string prefix = wanted.ToString();

        var seqToCluster = new Dictionary<string, string>();
        var clusterIds = new SortedSet<int>();

        foreach (var line in lines)
        {
            if (line.Kind != TranscriptKind.Segment || line.Source != wanted) continue;

            long bestOverlap = 0;
            int? bestCluster = null;
            foreach (var s in segments)
            {
                long overlap = Math.Min(line.EndMs, s.EndMs) - Math.Max(line.StartMs, s.StartMs);
                if (overlap <= 0) continue;
                // max overlap; tie -> lower cluster id
                if (overlap > bestOverlap || (overlap == bestOverlap && bestCluster is int bc && s.Cluster < bc))
                {
                    bestOverlap = overlap;
                    bestCluster = s.Cluster;
                }
            }
            if (bestCluster is null) continue;   // uncovered: leave unassigned

            seqToCluster[line.Seq.ToString()] = $"{prefix}:{bestCluster}";
            clusterIds.Add(bestCluster.Value);
        }

        var clusterKeys = clusterIds.Select(id => $"{prefix}:{id}").ToList();
        return new ClusterAssignment(seqToCluster, clusterKeys);
    }
}
