using LocalScribe.Core.Audio;
using LocalScribe.Core.Pipeline;

public class WavFileFrameReaderTests
{
    [Fact]
    public void Reads_16k_mono_wav_into_512_sample_frames_with_running_timestamps()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string wav = Path.Combine(dir, "in.wav");
            var pcm = new float[16000];                            // 1 s
            for (int i = 0; i < pcm.Length; i++) pcm[i] = 0.25f;
            using (var sink = new WavSink(wav)) sink.Write(pcm);

            var frames = WavFileFrameReader.ReadFrames(wav, SourceKind.Remote).ToList();

            Assert.Equal(16000 / 512, frames.Count);               // 31 whole windows; tail dropped
            Assert.All(frames, f => Assert.Equal(512, f.Samples.Length));
            Assert.Equal(SourceKind.Remote, frames[0].Source);
            Assert.Equal(0, frames[0].StartMs);
            Assert.Equal(32, frames[1].StartMs);
        }
        finally { Directory.Delete(dir, true); }
    }
}
