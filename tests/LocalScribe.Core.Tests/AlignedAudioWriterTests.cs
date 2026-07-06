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

    // ---- Stage 5.4 Phase 3: PadToMs (finalize-time pad to the session clock) ----

    [Fact]
    public void PadToMs_extends_with_silence_to_the_target()
    {
        var sink = new CollectingSink();
        using var w = new AlignedAudioWriter(sink);
        w.Write(FrameAt(0, 0.1f));                 // 512 recorded samples
        w.PadToMs(1000);                           // pad to 1 s = 16000 samples

        Assert.Equal(16000, w.SamplesWritten);
        Assert.Equal(16000, sink.Samples.Count);
        Assert.Equal(0.1f, sink.Samples[0]);       // recorded prefix intact
        Assert.Equal(0f, sink.Samples[512]);       // pad starts right after the frame
        Assert.Equal(0f, sink.Samples[15999]);     // pad is silence to the very end
    }

    [Fact]
    public void PadToMs_is_a_noop_at_or_behind_the_current_position()
    {
        var sink = new CollectingSink();
        using var w = new AlignedAudioWriter(sink);
        w.Write(FrameAt(0, 0.1f));                 // 512 samples = 32 ms
        w.PadToMs(10);                             // behind the write head: no-op
        w.PadToMs(32);                             // exactly at the write head: no-op

        Assert.Equal(512, w.SamplesWritten);       // never trims, never rewrites
        Assert.Equal(512, sink.Samples.Count);
    }

    [Fact]
    public void PadToMs_pads_across_multiple_silence_chunks()
    {
        var sink = new CollectingSink();
        using var w = new AlignedAudioWriter(sink);
        w.PadToMs(1000);                           // nothing written: 16000 zeros in
                                                   // 10 chunked writes (SilenceChunk = 1600)
        Assert.Equal(16000, w.SamplesWritten);
        Assert.Equal(16000, sink.Samples.Count);
        Assert.All(sink.Samples, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void PadToMs_never_rewrites_recorded_samples()
    {
        var sink = new CollectingSink();
        using var w = new AlignedAudioWriter(sink);
        w.Write(FrameAt(0, 0.25f, samples: 256));
        w.PadToMs(100);                            // 100 ms = 1600 samples total

        Assert.Equal(1600, w.SamplesWritten);
        Assert.All(sink.Samples.Take(256), s => Assert.Equal(0.25f, s));
        Assert.All(sink.Samples.Skip(256), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void PadToMs_then_Write_stays_clock_aligned()
    {
        var sink = new CollectingSink();
        using var w = new AlignedAudioWriter(sink);
        w.PadToMs(500);                            // 8000 samples of pad silence
        w.Write(FrameAt(1000, 0.3f));              // Write pads the remaining gap itself

        Assert.Equal(16512, w.SamplesWritten);     // padding composes with Write's gap logic
        Assert.Equal(0.3f, sink.Samples[16000]);   // frame lands at sample 16000 exactly
    }
}
