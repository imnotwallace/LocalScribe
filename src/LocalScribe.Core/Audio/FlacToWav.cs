using CUETools.Codecs;
using CUETools.Codecs.FLAKE;
using NAudio.Wave;
namespace LocalScribe.Core.Audio;

/// <summary>Decodes a FLAC leg to a linear-PCM WAV (same rate/channels, 16-bit), streaming so a
/// long file never fully materializes in memory. Purpose (measured 2026-08-02): our FLACs carry
/// no SEEKTABLE (FlakeWriter writes none), so Windows Media Foundation - the app's FLAC player -
/// ESTIMATES the time&lt;-&gt;sample mapping and drifts, growing with position (~19 s off by the
/// 8-minute mark). Linear PCM has a trivial byte-offset&lt;-&gt;time relationship that MF positions
/// and seeks EXACTLY, so playing the decoded WAV fixes both the scrubber clock and any seek. The
/// evidentiary FLAC is NEVER modified - this output is a throwaway playback cache. Decode is via the
/// SAME CUETools FLAKE library that wrote the FLAC (guaranteed sample-exact). 16-bit only (every
/// app FLAC is 16-bit): a wider depth throws rather than being silently mis-read.</summary>
public static class FlacToWav
{
    public static void Convert(string flacPath, string wavPath)
    {
        using var reader = new FlakeReader(flacPath, null);
        AudioPCMConfig pcm = reader.PCM;
        if (pcm.BitsPerSample != 16)
            throw new InvalidDataException(
                $"FlacToWav supports 16-bit PCM only; got {pcm.BitsPerSample}-bit: {flacPath}");

        using var writer = new WaveFileWriter(wavPath, new WaveFormat(pcm.SampleRate, 16, pcm.ChannelCount));
        var buffer = new AudioBuffer(pcm, 16384);
        int frames;
        while ((frames = reader.Read(buffer, 16384)) > 0)
            writer.Write(buffer.Bytes, 0, frames * pcm.BlockAlign);   // interleaved int16 LE, verbatim
    }
}
