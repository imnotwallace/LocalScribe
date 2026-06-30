// tests/LocalScribe.Core.Tests/CapturePipelineTests.cs
using LocalScribe.Core.Audio;
using NAudio.Wave;
using Xunit;

public class CapturePipelineTests
{
    [Fact]
    public void Fake_source_frames_flow_through_sink_to_a_readable_wav()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}.wav");
        var source = new FakeCaptureSource(SourceKind.Remote, framesOf:
            new[] { new float[] { 0.1f, 0.2f }, new float[] { -0.1f, -0.2f } });
        try
        {
            using (var sink = new WavSink(path))
            {
                source.FrameAvailable += f => sink.Write(f.Samples);
                source.Start();          // FakeCaptureSource emits synchronously
                source.Stop();
            }

            using var reader = new AudioFileReader(path);
            var buf = new float[4];
            int read = reader.Read(buf, 0, buf.Length);
            Assert.Equal(4, read);       // 2 frames x 2 samples
            Assert.Equal(0.1f, buf[0], 3);
            Assert.Equal(-0.2f, buf[3], 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
