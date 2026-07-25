using LocalScribe.Core.Diarisation;

public class VoiceprintMathTests
{
    [Fact]
    public void Cosine_identical_vectors_is_1()
        => Assert.Equal(1.0, VoiceprintMath.Cosine([1f, 2f, 3f], [1f, 2f, 3f]), 6);

    [Fact]
    public void Cosine_orthogonal_vectors_is_0()
        => Assert.Equal(0.0, VoiceprintMath.Cosine([1f, 0f], [0f, 1f]), 6);

    [Fact]
    public void Cosine_length_mismatch_or_zero_norm_is_0()
    {
        Assert.Equal(0.0, VoiceprintMath.Cosine([1f, 2f], [1f, 2f, 3f]));
        Assert.Equal(0.0, VoiceprintMath.Cosine([0f, 0f], [1f, 2f]));
        Assert.Equal(0.0, VoiceprintMath.Cosine([], []));
    }

    [Fact]
    public void Slice_clamps_concatenates_in_order()
    {
        float[] samples = new float[EmbeddingSamples.SampleRate * 2];       // 2s
        for (int i = 0; i < samples.Length; i++) samples[i] = i;
        var outp = EmbeddingSamples.Slice(samples,
            [new EmbedRange(1000, 1500), new EmbedRange(0, 250)]);
        Assert.Equal(EmbeddingSamples.SampleRate / 2 + EmbeddingSamples.SampleRate / 4, outp.Length);
        Assert.Equal(EmbeddingSamples.SampleRate, outp[0]);                 // 1000ms -> sample 16000
        Assert.Equal(0, outp[EmbeddingSamples.SampleRate / 2]);             // second range starts at 0
    }

    [Fact]
    public void Slice_out_of_bounds_range_is_clamped_not_thrown()
    {
        float[] samples = new float[EmbeddingSamples.SampleRate];           // 1s
        var outp = EmbeddingSamples.Slice(samples, [new EmbedRange(500, 99_000)]);
        Assert.Equal(EmbeddingSamples.SampleRate / 2, outp.Length);
    }

    [Fact]
    public void Slice_truncates_at_cap()
    {
        float[] samples = new float[EmbeddingSamples.SampleRate * 40];      // 40s
        var outp = EmbeddingSamples.Slice(samples, [new EmbedRange(0, 40_000)]);
        Assert.Equal(EmbeddingSamples.SampleRate * EmbeddingSamples.MaxSecondsPerCluster, outp.Length);
    }
}
