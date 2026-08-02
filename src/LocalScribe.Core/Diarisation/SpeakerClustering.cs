namespace LocalScribe.Core.Diarisation;

public sealed record TimedEmbedding(long StartMs, long EndMs, float[] Embedding);

/// <summary>Tunables for the in-house clusterer (design 2026-08-02). Defaults come from the
/// offline tuning grid against the gold reference (tools/diar-eval, private data); tests pass
/// explicit values so re-tuning the defaults never breaks unit tests.</summary>
public sealed record ClusteringOptions(
    int ReliableMinMs = 1000,
    double SilhouetteFloor = 0.20,
    int MaxAutoClusters = 6);

public sealed record ClusterOutcome(int[] ClusterBySegment, int ClusterCount);

/// <summary>In-house speaker clustering over per-segment CAM++ embeddings (design 2026-08-02,
/// replacing sherpa FastClustering which collapsed separable embeddings). Deterministic: no RNG,
/// all tie-breaks defined. Two-pass: reliable segments (>= ReliableMinMs, non-degenerate
/// embedding) form centroids; short "bridge" segments attach to the nearest centroid afterwards
/// (their embeddings are duration-starved, not mixed-voice). Cluster ids are renumbered
/// contiguous 0-based by first temporal appearance.</summary>
public static class SpeakerClustering
{
    public static ClusterOutcome Cluster(
        IReadOnlyList<TimedEmbedding> segments,
        int? forcedClusterCount,
        ClusteringOptions? options = null)
    {
        var opt = options ?? new ClusteringOptions();
        if (segments.Count == 0) return new ClusterOutcome([], 0);

        var normalized = new float[segments.Count][];
        var usable = new List<int>();
        for (int i = 0; i < segments.Count; i++)
        {
            var n = WeightedKMeans.NormalizeOrNull(segments[i].Embedding);
            if (n is not null) { normalized[i] = n; usable.Add(i); }
        }
        if (usable.Count == 0)
            return new ClusterOutcome(new int[segments.Count], 1);

        int need = Math.Max(2, forcedClusterCount ?? 2);
        var reliable = usable.Where(i =>
            segments[i].EndMs - segments[i].StartMs >= opt.ReliableMinMs).ToList();
        if (reliable.Count < need) reliable = usable; // bar drop

        var vectors = reliable.Select(i => normalized[i]).ToList();
        var weights = reliable.Select(i =>
            (segments[i].EndMs - segments[i].StartMs) / 1000.0).ToList();

        int k;
        int[] fitAssign;
        float[][] centroids;
        if (forcedClusterCount is int forced)
        {
            k = Math.Clamp(forced, 1, reliable.Count);
            var fit = WeightedKMeans.Fit(vectors, weights, k);
            (fitAssign, centroids) = (fit.Assignments, fit.Centroids);
        }
        else
        {
            (k, fitAssign, centroids) = SelectAutoCount(vectors, weights, opt);
        }

        var raw = new int[segments.Count];
        Array.Fill(raw, -1);
        for (int r = 0; r < reliable.Count; r++) raw[reliable[r]] = fitAssign[r];

        var reliableSet = new HashSet<int>(reliable);
        for (int i = 0; i < segments.Count; i++)
        {
            if (reliableSet.Contains(i)) continue;
            if (normalized[i] is not null)
            {
                int bestC = 0;
                double bestD = 1.0 - VoiceprintMath.Cosine(centroids[0], normalized[i]);
                for (int c = 1; c < centroids.Length; c++)
                {
                    double d = 1.0 - VoiceprintMath.Cosine(centroids[c], normalized[i]);
                    if (d < bestD) { bestD = d; bestC = c; }
                }
                raw[i] = bestC;
            }
            else
            {
                long mid = (segments[i].StartMs + segments[i].EndMs) / 2;
                int nearest = reliable[0];
                long nearestDist = long.MaxValue;
                foreach (int r in reliable)
                {
                    long rMid = (segments[r].StartMs + segments[r].EndMs) / 2;
                    long d = Math.Abs(rMid - mid);
                    if (d < nearestDist || (d == nearestDist && r < nearest))
                        { nearestDist = d; nearest = r; }
                }
                raw[i] = raw[nearest];
            }
        }

        // Renumber contiguous 0-based by first temporal appearance.
        var order = Enumerable.Range(0, segments.Count)
            .OrderBy(i => segments[i].StartMs).ThenBy(i => segments[i].EndMs).ThenBy(i => i);
        var remap = new Dictionary<int, int>();
        foreach (int i in order)
            if (!remap.ContainsKey(raw[i])) remap[raw[i]] = remap.Count;
        var final = new int[segments.Count];
        for (int i = 0; i < segments.Count; i++) final[i] = remap[raw[i]];
        return new ClusterOutcome(final, remap.Count);
    }

    private static (int K, int[] Assignments, float[][] Centroids) SelectAutoCount(
        IReadOnlyList<float[]> vectors, IReadOnlyList<double> weights, ClusteringOptions opt)
    {
        int maxK = Math.Min(opt.MaxAutoClusters, vectors.Count);
        if (vectors.Count < 2 || maxK < 2)
            return (1, new int[vectors.Count], [SingleCentroid(vectors, weights)]);

        (double Score, int K, int[] Assignments, float[][] Centroids)? best = null;
        for (int k = 2; k <= maxK; k++)
        {
            var fit = WeightedKMeans.Fit(vectors, weights, k);
            double score = CosineSilhouette.Weighted(vectors, weights, fit.Assignments, k);
            if (best is null || score > best.Value.Score) // tie -> smaller k (first wins)
                best = (score, k, fit.Assignments, fit.Centroids);
        }

        if (best!.Value.Score < opt.SilhouetteFloor)
            return (1, new int[vectors.Count], [SingleCentroid(vectors, weights)]);
        return (best.Value.K, best.Value.Assignments, best.Value.Centroids);
    }

    private static float[] SingleCentroid(IReadOnlyList<float[]> vectors, IReadOnlyList<double> weights)
    {
        var acc = new double[vectors[0].Length];
        double tw = 0;
        for (int i = 0; i < vectors.Count; i++)
        {
            for (int j = 0; j < acc.Length; j++) acc[j] += vectors[i][j] * weights[i];
            tw += weights[i];
        }
        var mean = new float[acc.Length];
        for (int j = 0; j < acc.Length; j++) mean[j] = (float)(acc[j] / tw);
        return WeightedKMeans.NormalizeOrNull(mean) ?? vectors[0];
    }
}
