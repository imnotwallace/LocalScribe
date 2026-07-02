// src/LocalScribe.Core/Audio/IAudioFileSink.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Audio;

/// <summary>Retained-audio writer seam: 16 kHz mono float in, session audio file out
/// (local.flac/remote.flac per settings.audioFormat - spec section 9).</summary>
public interface IAudioFileSink : IDisposable
{
    void Write(ReadOnlySpan<float> mono16k);
}

/// <summary>WAV variant - wraps the Stage-1 WavSink.</summary>
public sealed class WavAudioSink : IAudioFileSink
{
    private readonly WavSink _inner;
    public WavAudioSink(string path) => _inner = new WavSink(path);
    public void Write(ReadOnlySpan<float> mono16k) => _inner.Write(mono16k);
    public void Dispose() => _inner.Dispose();
}

public static class AudioSinkFactory
{
    public static IAudioFileSink Create(string path, AudioFormat format)
        => format == AudioFormat.Flac ? new FlacAudioSink(path) : new WavAudioSink(path);
}
