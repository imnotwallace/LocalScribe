using LocalScribe.Core.Audio;
using LocalScribe.Core.Pipeline;

public class SegmentAudioTests
{
    [Fact]
    public void Full_scale_square_wave_is_zero_db()
    {
        var pcm = new float[1600];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = i % 2 == 0 ? 1f : -1f;
        Assert.Equal(0.0, SegmentAudio.RmsDb(pcm), 1);
    }

    [Fact]
    public void Half_scale_is_about_minus_six_db()
    {
        var pcm = new float[1600];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = i % 2 == 0 ? 0.5f : -0.5f;
        Assert.Equal(-6.02, SegmentAudio.RmsDb(pcm), 1);
    }

    [Fact]
    public void Silence_and_empty_clamp_to_floor()
    {
        Assert.Equal(-90.0, SegmentAudio.RmsDb(new float[1600]));
        Assert.Equal(-90.0, SegmentAudio.RmsDb(ReadOnlySpan<float>.Empty));
    }

    [Fact]
    public void AudioSegment_carries_the_design_contract()
    {
        var seg = new AudioSegment(SourceKind.Remote, 1000, 2000, new float[16000]);
        Assert.Equal(SourceKind.Remote, seg.Source);
        Assert.Equal(1000, seg.StartMs);
        Assert.Equal(2000, seg.EndMs);
        Assert.Equal(16000, seg.Pcm.Length);
    }
}
