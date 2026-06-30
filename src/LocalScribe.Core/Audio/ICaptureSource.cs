namespace LocalScribe.Core.Audio;
public interface ICaptureSource : IDisposable
{
    SourceKind Source { get; }
    event Action<AudioFrame>? FrameAvailable;
    void Start();
    void Stop();
}
