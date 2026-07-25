namespace LocalScribe.Core.Diarisation;

/// <summary>Pure 16k-mono sample slicing shared by the Diarizer helper (per-cluster embedding
/// extraction) and tests. Ranges are clamped to the buffer; concatenation stops at
/// <see cref="MaxSecondsPerCluster"/> so a long cluster cannot balloon extraction time.</summary>
public static class EmbeddingSamples
{
    public const int SampleRate = 16000;
    public const int MaxSecondsPerCluster = 30;

    public static float[] Slice(float[] samples16kMono, IEnumerable<EmbedRange> ranges)
    {
        int cap = SampleRate * MaxSecondsPerCluster;
        var outp = new List<float>(Math.Min(cap, samples16kMono.Length));
        foreach (var r in ranges)
        {
            int start = (int)Math.Clamp(r.StartMs * SampleRate / 1000, 0, samples16kMono.Length);
            int end = (int)Math.Clamp(r.EndMs * SampleRate / 1000, 0, samples16kMono.Length);
            for (int i = start; i < end && outp.Count < cap; i++) outp.Add(samples16kMono[i]);
            if (outp.Count >= cap) break;
        }
        return [.. outp];
    }
}
