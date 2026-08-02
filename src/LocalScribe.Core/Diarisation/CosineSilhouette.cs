namespace LocalScribe.Core.Diarisation;

/// <summary>Duration-weighted cosine silhouette for auto speaker-count selection (in-house
/// clustering design 2026-08-02). Vectors must be L2-normalized. A singleton point scores 0;
/// clusterCount &lt; 2 scores 0 overall.</summary>
public static class CosineSilhouette
{
    public static double Weighted(
        IReadOnlyList<float[]> vectors,
        IReadOnlyList<double> weights,
        IReadOnlyList<int> assignments,
        int clusterCount)
    {
        if (clusterCount < 2 || vectors.Count == 0) return 0.0;

        static double Dist(float[] a, float[] b) => 1.0 - VoiceprintMath.Cosine(a, b);

        double totalS = 0, totalW = 0;
        for (int i = 0; i < vectors.Count; i++)
        {
            double aSum = 0, aW = 0;
            var bSum = new double[clusterCount];
            var bW = new double[clusterCount];
            for (int j = 0; j < vectors.Count; j++)
            {
                if (j == i) continue;
                double d = Dist(vectors[i], vectors[j]);
                if (assignments[j] == assignments[i]) { aSum += weights[j] * d; aW += weights[j]; }
                else { bSum[assignments[j]] += weights[j] * d; bW[assignments[j]] += weights[j]; }
            }

            double s;
            if (aW <= 0) s = 0.0; // singleton cluster
            else
            {
                double a = aSum / aW;
                double b = double.PositiveInfinity;
                for (int c = 0; c < clusterCount; c++)
                    if (c != assignments[i] && bW[c] > 0) b = Math.Min(b, bSum[c] / bW[c]);
                s = double.IsPositiveInfinity(b) || Math.Max(a, b) == 0
                    ? 0.0
                    : (b - a) / Math.Max(a, b);
            }
            totalS += weights[i] * s;
            totalW += weights[i];
        }
        return totalW <= 0 ? 0.0 : totalS / totalW;
    }
}
