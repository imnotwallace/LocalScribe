using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class AlignedAudioWriterTests
{
    private sealed class CollectingSink : IAudioFileSink
    {
        public readonly List<float> Samples = [];
        public bool Disposed;
        public void Write(ReadOnlySpan<float> mono16k) => Samples.AddRange(mono16k.ToArray());
        public void Dispose() => Disposed = true;
    }

    private static AudioFrame FrameAt(long startMs, float value, int samples = 512)
        => new(SourceKind.Local, startMs, Enumerable.Repeat(value, samples).ToArray());

    [Fact]
    public void Contiguous_frames_write_without_padding()
    {
        var sink = new CollectingSink();
        using var w = new AlignedAudioWriter(sink);
        w.Write(FrameAt(0, 0.1f));
        w.Write(FrameAt(32, 0.2f));           // 512 @ 16 kHz = 32 ms: exactly contiguous
        Assert.Equal(1024, w.SamplesWritten);
        Assert.Equal(1024, sink.Samples.Count);
        Assert.DoesNotContain(0f, sink.Samples);
    }

    [Fact]
    public void Gap_is_padded_with_silence_to_keep_clock_alignment()
    {
        var sink = new CollectingSink();
        using var w = new AlignedAudioWriter(sink);
        w.Write(FrameAt(0, 0.1f));
        w.Write(FrameAt(1000, 0.2f));         // pause gap: resumes at 1000 ms

        // Frame at 1000 ms must start at sample 16000 exactly.
        Assert.Equal(16000 + 512, w.SamplesWritten);
        Assert.Equal(0f, sink.Samples[512]);            // padding is silence
        Assert.Equal(0f, sink.Samples[15999]);
        Assert.Equal(0.2f, sink.Samples[16000]);        // resumed audio lands on-clock
    }

    [Fact]
    public void Small_negative_jitter_appends_without_padding_or_throwing()
    {
        var sink = new CollectingSink();
        using var w = new AlignedAudioWriter(sink);
        w.Write(FrameAt(0, 0.1f));
        w.Write(FrameAt(30, 0.2f));           // stamped 2 ms early vs 32 ms sample position
        Assert.Equal(1024, w.SamplesWritten); // appended as-is; ms-level drift accepted
    }

    [Fact]
    public void Dispose_disposes_sink()
    {
        var sink = new CollectingSink();
        new AlignedAudioWriter(sink).Dispose();
        Assert.True(sink.Disposed);
    }
}
