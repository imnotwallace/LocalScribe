// src/LocalScribe.Core/Audio/FlacAudioSink.cs
using CUETools.Codecs;
using CUETools.Codecs.FLAKE;
namespace LocalScribe.Core.Audio;

/// <summary>FLAC retained-audio writer (16-bit/mono/16 kHz) via the managed Flake encoder.
/// CUETools.Codecs.FLAKE is LGPL-3.0: consumed unmodified, dynamically linked, never trimmed.</summary>
public sealed class FlacAudioSink : IAudioFileSink
{
    private static readonly AudioPCMConfig Config = new(16, 1, 16000);
    private readonly FlakeWriter _writer;

    // FlakeWriter defaults to an 8192-byte PADDING metadata block. On this box, Windows
    // Media Foundation (System.Windows.Media.MediaPlayer) reports a constant forward clock
    // offset after any seek+Play on a FLAC file that carries that block - offset ~=
    // metadataBytes / avgAudioByteRate (probe-verified 2026-07-11; an identical file with the
    // PADDING block stripped shows only 2-21ms of noise). LocalScribe never rewrites FLAC
    // metadata after finalize, so writing zero padding is safe.
    public FlacAudioSink(string path)
    {
        _writer = new FlakeWriter(path, Config) { Padding = 0 };
    }

    public void Write(ReadOnlySpan<float> mono16k)
    {
        if (mono16k.Length == 0) return;
        byte[] bytes = PcmConverter.FloatToInt16Bytes(mono16k);
        var buffer = new AudioBuffer(Config, mono16k.Length);
        buffer.Prepare(bytes, mono16k.Length);
        _writer.Write(buffer);
    }

    public void Dispose() => _writer.Close();
}
