using System.Threading.Channels;
using LocalScribe.Core.Audio;
namespace LocalScribe.Core.Live;

/// <summary>Adapts push (ICaptureSource.FrameAvailable, capture thread) to pull
/// (IAsyncEnumerable for the VAD segmenter). Unbounded on purpose: the capture callback must
/// NEVER block (design: "capture never blocks on transcription") - when transcription lags,
/// the bounded worker queue's backpressure accumulates here in memory, not on the audio
/// thread. Complete() ends the stream, which is what triggers the segmenter's EOF flush.</summary>
public sealed class CaptureFrameBridge : IDisposable
{
    private readonly ICaptureSource _source;
    private readonly Channel<AudioFrame> _channel = Channel.CreateUnbounded<AudioFrame>(
        new UnboundedChannelOptions { SingleReader = true });
    private int _completed;

    public CaptureFrameBridge(ICaptureSource source)
    {
        _source = source;
        _source.FrameAvailable += OnFrame;
    }

    private void OnFrame(AudioFrame frame) => _channel.Writer.TryWrite(frame);

    public IAsyncEnumerable<AudioFrame> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 1) return;
        _source.FrameAvailable -= OnFrame;
        _channel.Writer.TryComplete();
    }

    public void Dispose() => Complete();
}
