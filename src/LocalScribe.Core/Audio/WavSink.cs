// src/LocalScribe.Core/Audio/WavSink.cs
using NAudio.Wave;
namespace LocalScribe.Core.Audio;

/// <summary>Append-only 16 kHz mono 16-bit PCM WAV writer.</summary>
public sealed class WavSink : IDisposable
{
    public const int SampleRate = 16000;
    private readonly WaveFileWriter _writer;

    public WavSink(string path)
        => _writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 1));

    public void Write(ReadOnlySpan<float> mono16k)
    {
        byte[] bytes = PcmConverter.FloatToInt16Bytes(mono16k);
        _writer.Write(bytes, 0, bytes.Length);
    }

    public void Dispose() => _writer.Dispose();
}
