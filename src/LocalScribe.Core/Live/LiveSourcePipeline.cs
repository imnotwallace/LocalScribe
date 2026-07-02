using LocalScribe.Core.Audio;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
namespace LocalScribe.Core.Live;

/// <summary>One source's live capture leg: fresh ICaptureSource -> CaptureFrameBridge ->
/// tap (retained audio + peak event) -> SileroVadSegmenter -> worker.EnqueueAsync. A leg ends
/// by completing the bridge: the frame stream finishes, VadCore.Flush() force-emits the
/// in-progress utterance (never drop trailing audio - user decision 2026-07-02), and the
/// flushed segment is enqueued before StopLegAndFlushAsync returns. Start/Stop pairs may
/// repeat (Pause/Resume legs); each leg gets a fresh source and a fresh VAD model.</summary>
public sealed class LiveSourcePipeline
{
    private readonly SourceKind _source;
    private readonly VadOptions _vad;
    private readonly Func<ISpeechProbabilityModel> _vadModelFactory;
    private readonly TranscriptionWorker _worker;
    private readonly AlignedAudioWriter? _audioWriter;

    private ICaptureSource? _legSource;
    private CaptureFrameBridge? _bridge;
    private Task? _feed;

    public event Action<SourceKind, float>? PeakObserved;

    public LiveSourcePipeline(SourceKind source, VadOptions vad,
        Func<ISpeechProbabilityModel> vadModelFactory, TranscriptionWorker worker,
        AlignedAudioWriter? audioWriter)
        => (_source, _vad, _vadModelFactory, _worker, _audioWriter)
         = (source, vad, vadModelFactory, worker, audioWriter);

    public void StartLeg(ICaptureSource source, CancellationToken ct)
    {
        if (_legSource is not null)
            throw new InvalidOperationException($"{_source} leg already running.");

        _legSource = source;
        _bridge = new CaptureFrameBridge(source);
        var segmenter = new SileroVadSegmenter(_source, _vad, _vadModelFactory());
        var frames = Tap(_bridge.ReadAllAsync(ct));

        _feed = Task.Run(async () =>
        {
            await foreach (var segment in segmenter.SegmentAsync(frames, ct))
                await _worker.EnqueueAsync(segment, ct);
        }, CancellationToken.None);

        source.Start();                       // start LAST: bridge is already listening
    }

    public async Task StopLegAndFlushAsync()
    {
        if (_legSource is null) return;
        _legSource.Stop();
        _bridge!.Complete();                  // ends the stream -> segmenter EOF flush
        try
        {
            await _feed!;                     // flush segment is enqueued when this returns
        }
        finally
        {
            _bridge.Dispose();
            _legSource.Dispose();
            (_legSource, _bridge, _feed) = (null, null, null);
        }
    }

    private async IAsyncEnumerable<AudioFrame> Tap(IAsyncEnumerable<AudioFrame> frames)
    {
        await foreach (var f in frames)
        {
            _audioWriter?.Write(f);
            if (PeakObserved is { } handler)
            {
                float peak = 0f;
                for (int i = 0; i < f.Samples.Length; i++)
                {
                    float a = Math.Abs(f.Samples[i]);
                    if (a > peak) peak = a;
                }
                handler(_source, peak);
            }
            yield return f;
        }
    }
}
