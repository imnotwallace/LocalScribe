namespace LocalScribe.Core.Diarisation;

public sealed record KMeansResult(int[] Assignments, float[][] Centroids);

/// <summary>Deterministic duration-weighted k-means on L2-normalized embeddings, cosine
/// distance (in-house clustering design 2026-08-02). No RNG: init picks the heaviest vector
/// then farthest-first; every tie-break is lowest-index/lowest-cluster. Callers normalize via
/// <see cref="NormalizeOrNull"/> and drop nulls before calling <see cref="Fit"/>.</summary>
public static class WeightedKMeans
{
    public const int MaxIterations = 200;

    public static float[]? NormalizeOrNull(float[] v)
    {
        double norm = 0;
        for (int i = 0; i < v.Length; i++) norm += (double)v[i] * v[i];
        if (v.Length == 0 || norm <= 0) return null;
        double inv = 1.0 / Math.Sqrt(norm);
        var outp = new float[v.Length];
        for (int i = 0; i < v.Length; i++) outp[i] = (float)(v[i] * inv);
        return outp;
    }

    public static KMeansResult Fit(IReadOnlyList<float[]> vectors, IReadOnlyList<double> weights, int k)
    {
        if (vectors.Count == 0) throw new ArgumentException("no vectors", nameof(vectors));
        if (weights.Count != vectors.Count) throw new ArgumentException("weights/vectors length mismatch", nameof(weights));
        if (k < 1 || k > vectors.Count) throw new ArgumentException($"k={k} outside 1..{vectors.Count}", nameof(k));
        for (int i = 0; i < weights.Count; i++)
            if (weights[i] <= 0) throw new ArgumentException("weights must be positive", nameof(weights));

        static double Dist(float[] a, float[] b) => 1.0 - VoiceprintMath.Cosine(a, b);

        // Init: heaviest vector first, then farthest-first. Ties -> lowest index.
        var centroids = new List<float[]>(k);
        int seed = 0;
        for (int i = 1; i < vectors.Count; i++) if (weights[i] > weights[seed]) seed = i;
        centroids.Add((float[])vectors[seed].Clone());
        while (centroids.Count < k)
        {
            int far = 0; double best = double.NegativeInfinity;
            for (int i = 0; i < vectors.Count; i++)
            {
                double d = double.PositiveInfinity;
                foreach (var c in centroids) d = Math.Min(d, Dist(vectors[i], c));
                if (d > best) { best = d; far = i; }
            }
            centroids.Add((float[])vectors[far].Clone());
        }

        var assign = new int[vectors.Count];
        Array.Fill(assign, -1);
        for (int iter = 0; iter < MaxIterations; iter++)
        {
            var next = new int[vectors.Count];
            for (int i = 0; i < vectors.Count; i++)
            {
                int bestC = 0; double bestD = Dist(vectors[i], centroids[0]);
                for (int c = 1; c < k; c++)
                {
                    double d = Dist(vectors[i], centroids[c]);
                    if (d < bestD) { bestD = d; bestC = c; }
                }
                next[i] = bestC;
            }
            if (next.AsSpan().SequenceEqual(assign)) break;
            assign = next;

            for (int c = 0; c < k; c++)
            {
                var acc = new double[vectors[0].Length];
                double tw = 0;
                for (int i = 0; i < vectors.Count; i++)
                {
                    if (assign[i] != c) continue;
                    for (int j = 0; j < acc.Length; j++) acc[j] += vectors[i][j] * weights[i];
                    tw += weights[i];
                }
                if (tw <= 0)
                {
                    // Emptied cluster: reseed to the vector farthest from its own centroid.
                    int far = 0; double best = double.NegativeInfinity;
                    for (int i = 0; i < vectors.Count; i++)
                    {
                        double d = Dist(vectors[i], centroids[assign[i]]);
                        if (d > best) { best = d; far = i; }
                    }
                    centroids[c] = (float[])vectors[far].Clone();
                    continue;
                }
                var mean = new float[acc.Length];
                for (int j = 0; j < acc.Length; j++) mean[j] = (float)(acc[j] / tw);
                centroids[c] = NormalizeOrNull(mean) ?? centroids[c];
            }
        }
        return new KMeansResult(assign, [.. centroids]);
    }
}
