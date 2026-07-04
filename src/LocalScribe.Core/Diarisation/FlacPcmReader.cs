using CUETools.Codecs;
using CUETools.Codecs.FLAKE;
using LocalScribe.Core.Audio;
using NAudio.Wave;

namespace LocalScribe.Core.Diarisation;

// Decodes a retained 16 kHz / mono / 16-bit leg to float samples for diarisation.
// Counts samples from file start with NO leading trim, so sampleIndex = ms * 16000 / 1000
// stays valid against the AlignedAudioWriter mapping.
public static class FlacPcmReader
{
    public static float[] ReadMono16k(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ".wav" ? ReadWav(path) : ReadFlac(path);
    }

    private static float[] ReadFlac(string path)
    {
        using var reader = new FlakeReader(path, null);
        AudioPCMConfig pcm = reader.PCM;
        if (pcm.SampleRate != 16000 || pcm.ChannelCount != 1)
            throw new InvalidDataException(
                $"Diarisation input must be 16 kHz mono; got {pcm.SampleRate} Hz / {pcm.ChannelCount} ch: {path}");

        var samples = new List<float>((int)Math.Max(0, reader.Length));
        var buffer = new AudioBuffer(pcm, 16384);
        int n;
        while ((n = reader.Read(buffer, 16384)) > 0)
        {
            // AudioBuffer exposes interleaved int16 little-endian bytes for a 16-bit config.
            ReadOnlySpan<byte> bytes = buffer.Bytes.AsSpan(0, n * pcm.BlockAlign);
            samples.AddRange(PcmConverter.Int16BytesToFloat(bytes));
        }
        return samples.ToArray();
    }

    private static float[] ReadWav(string path)
    {
        using var reader = new AudioFileReader(path);
        if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.Channels != 1)
            throw new InvalidDataException(
                $"Diarisation input must be 16 kHz mono; got {reader.WaveFormat.SampleRate} Hz / {reader.WaveFormat.Channels} ch: {path}");
        var all = new List<float>();
        var buf = new float[16000];
        int n;
        while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            all.AddRange(buf.AsSpan(0, n).ToArray());
        return all.ToArray();
    }
}
