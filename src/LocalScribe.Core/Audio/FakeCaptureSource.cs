// src/LocalScribe.Core/Audio/FakeCaptureSource.cs
namespace LocalScribe.Core.Audio;

/// <summary>Test double: synchronously replays preset frames on Start().</summary>
public sealed class FakeCaptureSource : ICaptureSource
{
    private readonly float[][] _frames;
    private long _t;
    public SourceKind Source { get; }
    public event Action<AudioFrame>? FrameAvailable;

    public FakeCaptureSource(SourceKind source, float[][] framesOf)
        => (Source, _frames) = (source, framesOf);

    public void Start()
    {
        foreach (var f in _frames)
        {
            FrameAvailable?.Invoke(new AudioFrame(Source, _t, f));
            _t += (long)(1000.0 * f.Length / WavSink.SampleRate);
        }
    }

    public void Stop() { }
    public void Dispose() { }
}
