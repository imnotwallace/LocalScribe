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

    /// <summary>Total duration of a retained 16 kHz mono leg, read from the container header only
    /// (FLAC STREAMINFO total-samples / WAV header) with NO full PCM decode - used as the
    /// re-transcription progress denominator (2026-07-31). Returns 0 if the header can't be read;
    /// progress is a display concern, so a bad header must degrade gracefully, never abort the run.</summary>
    public static long DurationMs(string path)
    {
        try
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".wav")
            {
                using var reader = new AudioFileReader(path);
                return (long)reader.TotalTime.TotalMilliseconds;
            }
            using var r = new FlakeReader(path, null);
            return r.PCM.SampleRate > 0 ? r.Length * 1000L / r.PCM.SampleRate : 0;
        }
        catch { return 0; }
    }

    private static float[] ReadFlac(string path) => RunDecode(path, () =>
    {
        using var reader = new FlakeReader(path, null);
        AudioPCMConfig pcm = reader.PCM;
        if (pcm.SampleRate != 16000 || pcm.ChannelCount != 1)
            throw new InvalidDataException(
                $"Diarisation input must be 16 kHz mono; got {pcm.SampleRate} Hz / {pcm.ChannelCount} ch: {path}");
        // The decode loop below reads buffer.Bytes as interleaved int16 (2 bytes/sample). A wider
        // depth (24-/32-bit) has a different BlockAlign, so those bytes would be mis-parsed into
        // garbage samples with NO error - the silent-failure hazard flagged in the 2026-07-31
        // smoke. Reject it up front, exactly as FlacToWav does.
        if (pcm.BitsPerSample != 16)
            throw new InvalidDataException(
                $"Diarisation input must be 16-bit PCM; got {pcm.BitsPerSample}-bit: {path}");

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
    });

    private static float[] ReadWav(string path) => RunDecode(path, () =>
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
    });

    // Runs a decode delegate and normalizes ANY genuine decode/read failure (a corrupt or
    // truncated file reaching FlakeReader's/NAudio's internal decode can throw IOException,
    // EndOfStreamException, or other internal exception types, not just InvalidDataException)
    // to a single InvalidDataException so the diarisation helper's BAD_AUDIO filter always
    // catches it. The explicit format-guard InvalidDataException (wrong rate/channels) and a
    // missing-file FileNotFoundException pass through unchanged, as does OperationCanceledException.
    private static float[] RunDecode(string path, Func<float[]> decode)
    {
        try
        {
            return decode();
        }
        catch (Exception ex) when (ex is not InvalidDataException
                                       and not FileNotFoundException
                                       and not OperationCanceledException)
        {
            throw new InvalidDataException($"Failed to decode audio file: {path}", ex);
        }
    }
}
