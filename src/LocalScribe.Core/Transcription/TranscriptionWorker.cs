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
    private string? _weightsFile;   // the file behind the CURRENT engine (evidentiary provenance)
    private string? _pendingWeightsMarker;   // deferred until it can sit on the right side of segments
    // Rolling mean of _rtfWindow, mirrored here so the UI can read it cross-thread without
    // touching the (single-consumer) Queue. NaN = "no data" sentinel; see RecentRtf.
    private double _recentRtf = double.NaN;
    // Mirrors _plan.Backend for the cross-thread engine chip (B1-1): the backend can flip to CPU
    // mid-session when a downgrade hits the ladder floor. Int-boxed enum so Volatile applies.
    private int _effectiveBackend;

    public event Action<TranscribedSegment>? SegmentTranscribed;
    public event Action<string>? MarkerRaised;
    public event Action<string>? ErrorRaised;

    /// <summary>Rolling realtime factor over the last LaggingWindow transcribed segments
    /// (processing-ms / audio-ms; above 1.0 = falling behind live audio), or null before the first
    /// tracked segment and again right after the one-shot lagging downgrade clears the window
    /// (design 2026-07-13 section 5 item 4: the console's keep-up chip). Read-only surface over the
    /// EXISTING _rtfWindow lag data - no new telemetry. Written only on the single-consumer worker
    /// loop; the Volatile pair keeps the cross-thread double read un-torn on every platform.</summary>
    public double? RecentRtf
    {
        get
        {
            double v = Volatile.Read(ref _recentRtf);
            return double.IsNaN(v) ? null : v;
        }
    }

    /// <summary>The backend the worker is CURRENTLY transcribing on. It starts at the Start-time
    /// plan's backend and flips to CPU when a mid-session downgrade hits the ladder floor
    /// (DowngradeAsync) - so the live engine chip can reflect reality instead of a stale Start-time
    /// backend (B1-1). Read-only cross-thread surface over the existing _plan (mirrored via Volatile,
    /// same shape as RecentRtf); written only on the single-consumer worker loop.</summary>
    public Backend EffectiveBackend => (Backend)Volatile.Read(ref _effectiveBackend);

    public TranscriptionWorker(IEngineFactory factory, BackendPlan initialPlan,
        LanguageResolver language, IClock clock, TranscriptionWorkerOptions options)
    {
        (_factory, _plan, _language, _clock, _o) = (factory, initialPlan, language, clock, options);
        _effectiveBackend = (int)initialPlan.Backend;
        _queue = Channel.CreateBounded<AudioSegment>(new BoundedChannelOptions(options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,     // absorb lag; never drop audio
            SingleReader = true,
        });
    }

    /// <summary>Enqueues a segment for transcription (design: "Backpressure, never drop").
    /// Throws ChannelClosedException if called after Complete(). Blocks (awaits) when the
    /// queue is full, until RunAsync drains it - callers must not assume this returns
    /// immediately.</summary>
    public ValueTask EnqueueAsync(AudioSegment segment, CancellationToken ct)
        => _queue.Writer.WriteAsync(segment, ct);

    public void Complete() => _queue.Writer.Complete();

    /// <summary>Runs the single-consumer loop. Must be running concurrently with producers
    /// calling EnqueueAsync, or a full queue (bounded channel, FullMode.Wait by design) will
    /// block them.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var engine = Adopt(await CreateEngineAsync(ct));
        try
        {
            await foreach (var segment in _queue.Reader.ReadAllAsync(ct))
            {
                TranscriptionResult result;
                string producedBy;
                string producedByWeights;
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
                        // At the CPU floor a persistent OOM retries indefinitely (user decision
                        // 2026-07-02: never drop audio; cancellation is the only escape). Real
                        // floor-OOM implies system RAM exhaustion.
                        engine = await DowngradeAsync(engine, ct);
                        continue;                        // retry the SAME segment
                    }
                    TrackRtf(_clock.ElapsedMs - t0, segment.EndMs - segment.StartMs);
                    producedBy = engine.ModelName;         // capture before any later downgrade
                    producedByWeights = engine.WeightsFile;
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
                    // Fresh window: the pre-downgrade engine's average must not keep the keep-up
                    // chip red after the ladder step already replaced that engine.
                    Volatile.Write(ref _recentRtf, double.NaN);
                }

                if (string.IsNullOrWhiteSpace(result.Text)) continue;
                if (result.NoSpeechProb is { } p && p >= _o.NoSpeechDropThreshold) continue;

                bool wasLocked = _language.IsLocked;
                // An English-only model has no multilingual head - its detected-language field
                // is junk (observed live: "az" on clean English speech). Only multilingual
                // models produce a trustworthy detection worth observing/locking.
                if (!producedBy.EndsWith(".en", StringComparison.Ordinal))
                    _language.Observe(result.DetectedLanguage);
                // Flush the pending weights marker on the correct SIDE of this segment
                // (re-verify probe 2026-07-13): before it when the NEW weights produced it
                // (OOM retry), after it when the OLD weights did (lagging downgrade fires
                // mid-iteration, before the trigger segment is emitted).
                bool producedByCurrentWeights = producedByWeights == _weightsFile;
                if (producedByCurrentWeights) FlushPendingWeightsMarker();
                SegmentTranscribed?.Invoke(new TranscribedSegment(segment, result, producedBy, producedByWeights));
                if (!producedByCurrentWeights) FlushPendingWeightsMarker();

                if (!wasLocked && _language.IsLocked)
                {
                    // Bidirectional weight fix-up on language lock (user decision 2026-07-02):
                    // an English-only model must switch to multilingual weights once a non-English
                    // language locks, and a multilingual model should switch to the ".en" weights
                    // once English locks, for a known ladder rung.
                    var previousPlan = _plan;
                    string model = _plan.ModelName;
                    bool isEnglishOnly = model.EndsWith(".en", StringComparison.Ordinal);
                    string locked = _language.Locked!;
                    if (locked == "en" && !isEnglishOnly && ModelLadder.HasEnglishVariant(model))
                        _plan = _plan with { ModelName = model + ".en" };
                    else if (locked != "en" && isEnglishOnly)
                        _plan = _plan with { ModelName = model[..^3] };

                    engine = await TrySwapEngineForLanguageLockAsync(engine, previousPlan, ct);
                }
            }
            // A change on the last segment (or one followed only by gated segments) still gets
            // its marker - the weights change is evidence even if the new engine produced nothing.
            FlushPendingWeightsMarker();
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
        Volatile.Write(ref _recentRtf, _rtfWindow.Average());   // keep-up chip source (section 5 item 4)
    }

    private async Task<ITranscriptionEngine> DowngradeAsync(ITranscriptionEngine current, CancellationToken ct)
    {
        string? next = ModelLadder.Downgrade(_plan.ModelName);
        _plan = next is not null
            ? _plan with { ModelName = next }
            : _plan with { Backend = Backend.Cpu };     // at the floor: fall to CPU (design)
        Volatile.Write(ref _effectiveBackend, (int)_plan.Backend);   // B1-1: publish the current backend
        return await RecreateAsync(current, ct);
    }

    private async Task<ITranscriptionEngine> RecreateAsync(ITranscriptionEngine current, CancellationToken ct)
    {
        await current.DisposeAsync();
        return Adopt(await CreateEngineAsync(ct));
    }

    /// <summary>Every engine the worker starts using passes through here. A recreation that
    /// loads a DIFFERENT weights file than the one prior segments came from (e.g. the VRAM-OOM
    /// floor fall re-resolving CUDA f16 to CPU q8_0) records a transcript marker - a mid-session
    /// weights change is evidence, never silent (review finding 2026-07-13). Compared by FILE,
    /// not by recreation event: a same-file reload stays silent, as on master. The marker is
    /// PENDED, not raised - the segment loop flushes it on the correct side of the surrounding
    /// segments (a chained transition, e.g. an OOM ladder walk, flushes the older one first).</summary>
    private ITranscriptionEngine Adopt(ITranscriptionEngine engine)
    {
        if (_weightsFile is not null && _weightsFile != engine.WeightsFile)
        {
            FlushPendingWeightsMarker();   // chained transitions: the older change precedes everything newer
            _pendingWeightsMarker = string.Format(Markers.TranscriptionWeightsChanged, _weightsFile, engine.WeightsFile);
        }
        _weightsFile = engine.WeightsFile;
        return engine;
    }

    private void FlushPendingWeightsMarker()
    {
        if (_pendingWeightsMarker is { } pending)
        {
            _pendingWeightsMarker = null;
            MarkerRaised?.Invoke(pending);
        }
    }

    /// <summary>Language-lock weight swap is an optimization, never worth a dead session:
    /// create the new engine BEFORE disposing the old one, and if creation fails for ANY
    /// reason - a missing weight file (e.g. only .en models fetched), a VRAM OOM made MORE
    /// likely by briefly holding two engines at once, or anything else - revert the plan,
    /// raise the matching error (spec 8.2), and keep transcribing on the current engine.</summary>
    private async Task<ITranscriptionEngine> TrySwapEngineForLanguageLockAsync(
        ITranscriptionEngine current, BackendPlan previousPlan, CancellationToken ct)
    {
        ITranscriptionEngine replacement;
        try
        {
            replacement = await CreateEngineAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The swap is an optimization - NO failure here is worth a dead live session
            // (create-before-dispose means even a VRAM OOM from holding two engines lands
            // here). Revert and keep transcribing on the current engine (spec 8.2).
            _plan = previousPlan;
            ErrorRaised?.Invoke(ex is FileNotFoundException ? "MODEL_DOWNLOAD_FAILED" : "BACKEND_INIT_FAILED");
            return current;
        }
        await current.DisposeAsync();
        return Adopt(replacement);
    }

    private Task<ITranscriptionEngine> CreateEngineAsync(CancellationToken ct)
        => _factory.CreateAsync(_plan, _language.IsLocked ? _language.Locked : null, _o.InitialPrompt, ct);
}
