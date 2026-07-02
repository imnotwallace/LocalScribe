using LocalScribe.Core.Audio;
using LocalScribe.Core.Vad;

public class VadSegmenterTests
{
    private sealed class AlwaysSpeech : ISpeechProbabilityModel
    {
        public float SpeechProbability(ReadOnlySpan<float> window) => 0.9f;
        public void Reset() { }
    }

    private static async IAsyncEnumerable<AudioFrame> Frames(int windows)
    {
        yield return new AudioFrame(SourceKind.Remote, 0, new float[windows * 512]);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task End_of_stream_flushes_the_residual_utterance()
    {
        var seg = new SileroVadSegmenter(SourceKind.Remote, new VadOptions(), new AlwaysSpeech());
        var got = new List<LocalScribe.Core.Pipeline.AudioSegment>();
        await foreach (var s in seg.SegmentAsync(Frames(windows: 20), default))
            got.Add(s);

        var only = Assert.Single(got);                 // nothing finalized live; EOF flush emits
        Assert.Equal(SourceKind.Remote, only.Source);
        Assert.Equal(20 * 512, only.Pcm.Length);
    }
}
