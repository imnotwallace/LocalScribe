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

    public FlacAudioSink(string path) => _writer = new FlakeWriter(path, Config);

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
