namespace LocalScribe.Core.Search.Semantic;

/// <summary>Matryoshka truncation + unit normalization. Used by the helper on every embedded
/// vector; unit-length vectors make cosine similarity a plain dot product at query time.
/// The input array is never mutated; returns a new array.</summary>
public static class EmbeddingMath
{
    public static float[] TruncateAndNormalize(float[] v, int dim)
    {
        float[] r = dim > 0 && dim < v.Length ? v[..dim] : (float[])v.Clone();
        double sum = 0;
        foreach (float f in r) sum += (double)f * f;
        if (sum <= 0) return r;
        float inv = (float)(1.0 / Math.Sqrt(sum));
        for (int i = 0; i < r.Length; i++) r[i] *= inv;
        return r;
    }
}
