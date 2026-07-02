using System.Threading.Channels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
namespace LocalScribe.Core.Transcription;

public sealed record TranscriptionWorkerOptions
{
    public int QueueCapacity { get; init; } = 64;
    public double NoSpeechDropThreshold { get; init; } = 0.6;
    public double LaggingRtfThreshold { get; init; } = 1.0;
    public int LaggingWindow { get; init; } = 8;
    public string? InitialPrompt { get; init; }
}

/// <summary>Single-consumer transcription worker over a bounded channel (design:
/// "Backpressure, never drop"). Owns the engine lifecycle: hallucination gate, language
/// lock (recreate once), VRAM-OOM downgrade + same-segment retry, sustained-RTF downgrade
/// with a one-shot `transcription lagging` marker (spec section 3/section 8).</summary>
public sealed class TranscriptionWorker
{
    private readonly IEngineFactory _factory;
    private readonly LanguageResolver _language;
    private readonly IClock _clock;
    private readonly TranscriptionWorkerOptions _o;
    private readonly Channel<AudioSegment> _queue;
    private readonly Queue<double> _rtfWindow = new();
    private BackendPlan _plan;
    private bool _laggingRaised;

    public event Action<TranscribedSegment>? SegmentTranscribed;
    public event Action<string>? MarkerRaised;
    public event Action<string>? ErrorRaised;

    public TranscriptionWorker(IEngineFactory factory, BackendPlan initialPlan,
        LanguageResolver language, IClock clock, TranscriptionWorkerOptions options)
    {
        (_factory, _plan, _language, _clock, _o) = (factory, initialPlan, language, clock, options);
        _queue = Channel.CreateBounded<AudioSegment>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,     // absorb lag; never drop audio
            SingleReader = true,
        });
    }

    public ValueTask EnqueueAsync(AudioSegment segment, CancellationToken ct)
        => _queue.Writer.WriteAsync(segment, ct);

    public void Complete() => _queue.Writer.Complete();

    public async Task RunAsync(CancellationToken ct)
    {
        var engine = await CreateEngineAsync(ct);
        try
        {
            await foreach (var segment in _queue.Reader.ReadAllAsync(ct))
            {
                TranscriptionResult result;
                while (true)
                {
                    long t0 = _clock.ElapsedMs;
                    try
                    {
                        result = await engine.TranscribeAsync(segment, ct);
                    }
                    catch (VramOutOfMemoryException)
                    {
                        ErrorRaised?.Invoke("VRAM_OOM");
                        engine = await DowngradeAsync(engine, ct);
                        continue;                        // retry the SAME segment
                    }
                    TrackRtf(_clock.ElapsedMs - t0, segment.EndMs - segment.StartMs);
                    break;
                }

                if (!_laggingRaised
                    && _rtfWindow.Count >= _o.LaggingWindow
                    && _rtfWindow.All(r => r > _o.LaggingRtfThreshold))
                {
                    // one-shot in 2b: marker + a single downgrade step (spec 3/8.1)
                    _laggingRaised = true;
                    MarkerRaised?.Invoke(Markers.TranscriptionLagging);
                    ErrorRaised?.Invoke("RTF_LAGGING");
                    engine = await DowngradeAsync(engine, ct);
                    _rtfWindow.Clear();
                }

                if (string.IsNullOrWhiteSpace(result.Text)) continue;
                if (result.NoSpeechProb is { } p && p >= _o.NoSpeechDropThreshold) continue;

                bool wasLocked = _language.IsLocked;
                _language.Observe(result.DetectedLanguage);
                SegmentTranscribed?.Invoke(new TranscribedSegment(segment, result, engine.ModelName));

                if (!wasLocked && _language.IsLocked)
                    engine = await RecreateAsync(engine, ct);   // language lock: rebuild once
            }
        }
        finally
        {
            await engine.DisposeAsync();
        }
    }

    private void TrackRtf(long processingMs, long audioMs)
    {
        if (audioMs <= 0) return;
        _rtfWindow.Enqueue(processingMs / (double)audioMs);
        while (_rtfWindow.Count > _o.LaggingWindow) _rtfWindow.Dequeue();
    }

    private async Task<ITranscriptionEngine> DowngradeAsync(ITranscriptionEngine current, CancellationToken ct)
    {
        string? next = ModelLadder.Downgrade(_plan.ModelName);
        _plan = next is not null
            ? _plan with { ModelName = next }
            : _plan with { Backend = Backend.Cpu };     // at the floor: fall to CPU (design)
        return await RecreateAsync(current, ct);
    }

    private async Task<ITranscriptionEngine> RecreateAsync(ITranscriptionEngine current, CancellationToken ct)
    {
        await current.DisposeAsync();
        return await CreateEngineAsync(ct);
    }

    private Task<ITranscriptionEngine> CreateEngineAsync(CancellationToken ct)
        => _factory.CreateAsync(_plan, _language.IsLocked ? _language.Locked : null, _o.InitialPrompt, ct);
}
