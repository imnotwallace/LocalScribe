using LocalScribe.Core.Audio;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
namespace LocalScribe.Core.Live;

/// <summary>One source's live capture leg: fresh ICaptureSource -> CaptureFrameBridge -> the
/// audio loop (retained audio write + peak event, under captureCt) -> an unbounded hand-off
/// channel (growth after a dead feed is prevented by the feedCt guard below, not by capacity)
/// -> SileroVadSegmenter -> worker.EnqueueAsync (under feedCt). The two tokens are split so a
/// worker fault (feedCt cancelled) stops VAD feeding without stopping the audio write - retained
/// audio is evidentiary and must survive a transcription-worker failure (design section 3,
/// Fix #3). A leg ends by completing the bridge: the frame stream finishes, the audio loop
/// completes the hand-off channel, VadCore.Flush() force-emits the in-progress utterance (never
/// drop trailing audio - user decision 2026-07-02), and the flushed segment is enqueued before
/// StopLegAndFlushAsync returns. Start/Stop pairs may repeat (Pause/Resume legs); each leg gets a
/// fresh source and a fresh VAD model.</summary>
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
    private System.Threading.Channels.Channel<AudioFrame>? _segInput;
    private Task? _audioLoop;

    public event Action<SourceKind, float>? PeakObserved;

    public LiveSourcePipeline(SourceKind source, VadOptions vad,
        Func<ISpeechProbabilityModel> vadModelFactory, TranscriptionWorker worker,
        AlignedAudioWriter? audioWriter)
        => (_source, _vad, _vadModelFactory, _worker, _audioWriter)
         = (source, vad, vadModelFactory, worker, audioWriter);

    /// <summary>Starts a leg with the frame loop (audio write + peak) running under
    /// <paramref name="captureCt"/> and VAD->worker feeding gated by <paramref name="feedCt"/>.
    /// When feedCt is cancelled (e.g. a worker fault) the loop stops pushing frames into the
    /// segmenter but keeps writing audio - evidentiary audio must never depend on the worker
    /// staying alive (design section 3). Passing the same token for both reproduces the prior
    /// single-token behavior byte-for-byte.</summary>
    public void StartLeg(ICaptureSource source, CancellationToken captureCt, CancellationToken feedCt)
    {
        if (_legSource is not null)
            throw new InvalidOperationException($"{_source} leg already running.");

        _legSource = source;
        _bridge = new CaptureFrameBridge(source);
        _segInput = System.Threading.Channels.Channel.CreateUnbounded<AudioFrame>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        var segmenter = new SileroVadSegmenter(_source, _vad, _vadModelFactory());
        _feed = Task.Run(async () =>
        {
            await foreach (var segment in segmenter.SegmentAsync(
                _segInput.Reader.ReadAllAsync(feedCt), feedCt))
                await _worker.EnqueueAsync(segment, feedCt);
        }, CancellationToken.None);

        _audioLoop = Task.Run(async () =>
        {
            try
            {
                await foreach (var f in _bridge.ReadAllAsync(captureCt))
                {
                    _audioWriter?.Write(f);                 // ALWAYS - audio never depends on the feed
                    EmitPeak(f);
                    if (!feedCt.IsCancellationRequested)
                        _segInput.Writer.TryWrite(f);       // stop feeding VAD once the worker is gone
                }
            }
            finally
            {
                _segInput.Writer.TryComplete();             // ALWAYS unblock the feed (even on an audio-write fault) -
                                                             // clean EOF -> VAD Flush emits trailing utterance
            }
        }, CancellationToken.None);

        source.Start();                                 // start LAST: bridge is already listening
    }

    /// <summary>Fix (2026-07-08): stops this leg from accepting NEW capture frames, without waiting for
    /// the drain/flush. SessionController.StopAsync calls this on BOTH legs up front so they halt at the
    /// same instant (completing each frame bridge) before it settles either one - the remote leg no
    /// longer keeps recording while the local leg's flush runs. Frames already buffered in the bridge
    /// still drain into the audio writer via the clean EOF that StopLegAndFlushAsync awaits, so NO
    /// captured audio is lost (unlike cancelling the capture token, which would abandon the buffer).
    /// Idempotent: a no-op when no leg is running or the bridge is already completed.</summary>
    public void HaltCapture() => _bridge?.Complete();

    public async Task StopLegAndFlushAsync()
    {
        if (_legSource is null) return;
        _legSource.Stop();
        _bridge!.Complete();                            // ends the frame stream -> audio loop finishes
        try
        {
            if (_audioLoop is not null) await _audioLoop;   // drains capture, completes _segInput
            if (_feed is not null)
            {
                // A prior feedCt cancellation (worker fault - Fix #3) makes the feed loop end via
                // OperationCanceledException instead of a clean EOF; that is an expected outcome of
                // the token split, not a leg fault, so it must not stop Stop/Flush from completing
                // (the audio side already finished above; retained audio is unaffected either way).
                try { await _feed; }                        // VAD EOF flush enqueued before this returns
                catch (OperationCanceledException) { }
            }
        }
        finally
        {
            _bridge.Dispose();
            _legSource.Dispose();
            (_legSource, _bridge, _feed, _audioLoop, _segInput) = (null, null, null, null, null);
        }
    }

    private void EmitPeak(AudioFrame f)
    {
        if (PeakObserved is not { } handler) return;
        float peak = 0f;
        for (int i = 0; i < f.Samples.Length; i++)
        {
            float a = Math.Abs(f.Samples[i]);
            if (a > peak) peak = a;
        }
        handler(_source, peak);
    }
}
