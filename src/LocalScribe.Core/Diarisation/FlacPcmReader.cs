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
            if (!HasReadableMetadata(path)) return 0;   // see the guard's own comment
            using var r = new FlakeReader(path, null);
            return r.PCM.SampleRate > 0 ? r.Length * 1000L / r.PCM.SampleRate : 0;
        }
        catch { return 0; }
    }

    /// <summary>False for exactly the two truncation shapes that make FlakeReader SPIN FOREVER, and
    /// true for everything else - including files it can still read despite being truncated.
    ///
    /// THIS GUARD EXISTS BECAUSE FlakeReader HANGS, NOT BECAUSE IT THROWS (MEASURED 2026-08-06
    /// against FLAKE 1.0.5, on a real 833-byte leg cut one byte at a time). Its metadata parse loop
    /// treats a zero-byte read at EOF as "try again" rather than "stop", so it burns a core
    /// indefinitely - 304 CPU-seconds before the first run was killed. A catch-all cannot help: an
    /// infinite loop throws nothing. The hazard is a TORN WRITE that got the magic down, which is
    /// precisely what an interrupted recording leaves on disk - the very files launch-time recovery
    /// now probes (Tier 1B, T1-2).
    ///
    /// The two hanging shapes, both measured, both rejected below:
    ///   1. STREAMINFO itself is incomplete (file shorter than 4 + 4 + 34 = 42 bytes). Measured
    ///      HUNG at 4, 7, 8 and 41 bytes.
    ///   2. A block body ends EXACTLY at EOF with the last-block flag not yet seen, so the next
    ///      header read starts at end-of-stream. Measured HUNG at exactly 42 bytes.
    ///
    /// Everything else is left alone ON PURPOSE, and this is why the guard is not the simpler
    /// "reject unless the whole chain fits". MEASURED: 43 through 62 bytes - a chain truncated
    /// mid-block, one byte past the boundary - all return the correct 5000 ms, because FLAC keeps
    /// total samples in STREAMINFO rather than deriving it from the frames present. A
    /// completeness rule would have thrown those readable durations away, and a plain file-size
    /// floor cannot express case 2 at all (42 bytes is a legal STREAMINFO and still hangs).
    ///
    /// FileShare.ReadWrite, never FileShare.Read: the recording that produced this leg may still be
    /// open on it, and excluding writers here would turn a diagnostic probe into a capture failure.</summary>
    private static bool HasReadableMetadata(string path)
    {
        const int StreamInfoEnd = 4 + 4 + 34;               // magic + block header + STREAMINFO body

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long length = fs.Length;
        if (length < StreamInfoEnd) return false;           // shape 1

        Span<byte> buf = stackalloc byte[4];
        if (fs.Read(buf) != 4) return false;
        if (buf[0] != 0x66 || buf[1] != 0x4C || buf[2] != 0x61 || buf[3] != 0x43) return false;  // "fLaC"

        // Walk the block chain looking ONLY for shape 2. Landing past EOF (a truncated body) or
        // on a partial header is safe and stays accepted.
        long pos = 4;
        while (pos + 4 <= length)
        {
            fs.Position = pos;
            if (fs.Read(buf) != 4) return true;             // partial header: safe, measured
            bool isLast = (buf[0] & 0x80) != 0;             // METADATA_BLOCK_HEADER bit 7
            int blockLength = (buf[1] << 16) | (buf[2] << 8) | buf[3];   // 24-bit big-endian
            pos += 4 + blockLength;
            if (isLast) return true;                        // chain complete
            if (pos == length) return false;                // shape 2: next header starts at EOF
            if (pos > length) return true;                  // truncated body: safe, measured
        }
        return true;
    }

    private static float[] ReadFlac(string path) => RunDecode(path, () =>
    {
        // Same hang, same constructor, same root cause as in DurationMs above - guarded here too
        // rather than only where Tier 1B happened to hit it, because Split Speakers and Import both
        // decode user-supplied and crash-interrupted files through this path. An InvalidDataException
        // is the RIGHT failure here (not a 0 like DurationMs): RunDecode passes it through unchanged
        // and the diarisation helper's BAD_AUDIO filter reports it honestly.
        if (!HasReadableMetadata(path))
            throw new InvalidDataException(
                $"Truncated or malformed FLAC - its metadata blocks run past the end of the file: {path}");
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
