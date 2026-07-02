namespace LocalScribe.Core.Pipeline;

/// <summary>PCM-level measurements on a segment.</summary>
public static class SegmentAudio
{
    public const double FloorDb = -90.0;

    /// <summary>RMS energy in dBFS (0 dB = full-scale), clamped to the -90 dB floor.</summary>
    public static double RmsDb(ReadOnlySpan<float> pcm)
    {
        if (pcm.Length == 0) return FloorDb;
        double sum = 0;
        foreach (float s in pcm) sum += (double)s * s;
        double rms = Math.Sqrt(sum / pcm.Length);
        if (rms <= 0) return FloorDb;
        return Math.Max(FloorDb, 20.0 * Math.Log10(rms));
    }
}
