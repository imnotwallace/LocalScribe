namespace LocalScribe.Core.Diarisation;

/// <summary>Pure embedding-vector math for voiceprint matching (voiceprint design 2026-07-25).
/// Degenerate inputs (length mismatch, zero norm, empty) score 0.0 - "no similarity", never throw.</summary>
public static class VoiceprintMath
{
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0.0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0.0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
