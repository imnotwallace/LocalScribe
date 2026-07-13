using System.Threading.Channels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.Core.Pipeline;

public sealed record OfflineRunOptions
{
    public string? LocalWavPath { get; init; }
    public string? RemoteWavPath { get; init; }
    public VadOptions Vad { get; init; } = new();
    public TranscriptionWorkerOptions Worker { get; init; } = new();
}

/// <summary>Stage-2 walking skeleton: WAV pair -> VAD -> Whisper -> merge -> a complete,
/// finalized, spec-shaped session folder. Same components the live pipeline (Stage 3) wires
/// to real capture; only the frame source differs.</summary>
public sealed class OfflinePipelineRunner
{
    private readonly StoragePaths _paths;
    private readonly Settings _settings;
    private readonly IEngineFactory _engineFactory;
    private readonly Func<ISpeechProbabilityModel> _vadModelFactory;
    private readonly IHardwareProbe _hardware;
    private readonly IClock _clock;
    private readonly TimeProvider _time;
    private readonly string _appVersion;

    public OfflinePipelineRunner(StoragePaths paths, Settings settings, IEngineFactory engineFactory,
        Func<ISpeechProbabilityModel> vadModelFactory, IHardwareProbe hardware,
        IClock clock, TimeProvider time, string appVersion)
        => (_paths, _settings, _engineFactory, _vadModelFactory, _hardware, _clock, _time, _appVersion)
         = (paths, settings, engineFactory, vadModelFactory, hardware, clock, time, appVersion);

    public async Task<string> RunAsync(OfflineRunOptions options, CancellationToken ct)
    {
        if (options.LocalWavPath is null && options.RemoteWavPath is null)
            throw new ArgumentException("At least one of LocalWavPath/RemoteWavPath is required.");

        // 1) identity: wall-clock start, timezone capture (spec 1.2), collision-safe id (spec 9)
        var sources = new List<SourceKind>();
        if (options.LocalWavPath is not null) sources.Add(SourceKind.Local);
        if (options.RemoteWavPath is not null) sources.Add(SourceKind.Remote);

        var boot = await SessionBootstrap.StartAsync(_paths, _settings, AppKind.Manual,
            sources, new DeviceSnapshot(), _time, _appVersion, ct);
        string id = boot.Id;
        var live = boot.LiveRecord;
        var startedUtc = live.StartedAtUtc;
        var sessionStore = new SessionStore(_paths.SessionJson(id));

        // 2) pipeline
        var (plan, _) = BackendSelector.Select(_hardware.Probe(), _settings, ModelPaths.AvailableModels());
        var resolver = new LanguageResolver(_settings.Language);
        string prompt = new VocabularyProvider(_settings.Vocabulary, new Dictionary<string, Matter>())
            .BuildInitialPrompt(Array.Empty<string>());
        var worker = new TranscriptionWorker(_engineFactory, plan, resolver, _clock,
            options.Worker with { InitialPrompt = prompt.Length == 0 ? null : prompt });

        var merger = new TranscriptMerger(new TranscriptStore(_paths.TranscriptJsonl(id)));
        await merger.InitializeAsync(ct);

        // events -> single writer loop (event handlers must not await)
        var outbox = Channel.CreateUnbounded<object>();             // TranscribedSegment | string marker
        string? lastModel = null;
        string? lastWeightsFile = null;                             // exact ggml file (provenance)
        worker.SegmentTranscribed += ts => outbox.Writer.TryWrite(ts);
        worker.MarkerRaised += m => outbox.Writer.TryWrite(m);

        var writerLoop = Task.Run(async () =>
        {
            long lastEndMs = 0;
            await foreach (object item in outbox.Reader.ReadAllAsync(ct))
            {
                if (item is TranscribedSegment ts)
                {
                    var line = await merger.AppendSegmentAsync(ts, ct);
                    lastEndMs = Math.Max(lastEndMs, line.EndMs);
                    lastModel = ts.ModelName;
                    lastWeightsFile = ts.WeightsFile;
                }
                else if (item is string marker)
                {
                    await merger.AppendMarkerAsync(marker, lastEndMs, ct);
                }
            }
        }, ct);

        var workerLoop = worker.RunAsync(ct);

        // Finding C1: if the worker faults (e.g. missing ggml model -> FileNotFoundException),
        // the feeding loop below is the queue's ONLY producer and the bounded channel
        // (FullMode.Wait) has no reader left to drain it once >QueueCapacity segments buffer -
        // it would block forever with no escape (real recordings routinely exceed capacity).
        // Cancel a linked, feed-only token the instant workerLoop faults so EnqueueAsync/the
        // segmenter enumeration abort promptly; the ORIGINAL exception is recovered below via
        // `await workerLoop`, never masked by the resulting OperationCanceledException.
        using var feedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = workerLoop.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Cancel(),
            feedCts, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        bool faulted = false;
        try
        {
            try
            {
                foreach (var (path, kind) in EnumerateInputs(options))
                {
                    var segmenter = new SileroVadSegmenter(kind, options.Vad, _vadModelFactory());
                    await foreach (var segment in segmenter.SegmentAsync(ToAsync(WavFileFrameReader.ReadFrames(path, kind)), feedCts.Token))
                        await worker.EnqueueAsync(segment, feedCts.Token);
                }
            }
            catch (OperationCanceledException) when (feedCts.IsCancellationRequested)
            {
                // Feeding aborted early: either the worker faulted (real exception recovered
                // below by awaiting workerLoop) or the caller cancelled `ct` (feedCts is linked
                // to it) - either way, fall through so workerLoop surfaces the real outcome.
            }
            finally
            {
                worker.Complete();
            }

            await workerLoop;                                       // queue drained (spec 2.1 flush)
        }
        catch
        {
            faulted = true;
            throw;
        }
        finally
        {
            // writerLoop must never be orphaned on ANY path (success or fault).
            outbox.Writer.TryComplete();
            if (faulted)
            {
                // A primary fault is already propagating; observe writerLoop but never let a
                // secondary exception from it mask the real cause.
                try { await writerLoop; } catch { /* secondary - primary fault wins */ }
            }
            else
            {
                await writerLoop;                                   // real exception here IS the fault
            }
        }

        // 3) retained audio (keep by default; "never" skips - spec 7)
        var retained = new List<SourceKind>();
        if (_settings.AudioRetention != "never")
        {
            foreach (var (path, kind) in EnumerateInputs(options))
            {
                using var sink = AudioSinkFactory.Create(
                    _paths.AudioFile(id, kind, _settings.AudioFormat), _settings.AudioFormat);
                foreach (var frame in WavFileFrameReader.ReadFrames(path, kind))
                    sink.Write(frame.Samples);
                retained.Add(kind);
            }
        }

        // 4) finalize + project
        long duration = merger.View.Count == 0 ? 0 : merger.View.Max(l => l.EndMs);
        await sessionStore.SaveAsync(live with
        {
            EndedAtUtc = startedUtc.AddMilliseconds(duration),
            DurationMs = duration,
            SegmentCount = merger.View.Count(l => l.Kind == TranscriptKind.Segment),
            MarkerCount = merger.View.Count(l => l.Kind == TranscriptKind.Marker),
            Model = lastModel ?? plan.ModelName,
            WeightsFile = lastWeightsFile,   // exact file that ran (null: nothing transcribed)
            Backend = plan.Backend.ToString().ToUpperInvariant(),   // recorded actual, e.g. "CPU"
            Language = resolver.Locked ?? _settings.Language,
            RetainedAudioSources = retained,
        }, ct);

        await new SessionWriter(_paths, _settings, _time).RegenerateProjectionsAsync(id, ct);
        return id;
    }

    private static IEnumerable<(string Path, SourceKind Kind)> EnumerateInputs(OfflineRunOptions o)
    {
        if (o.LocalWavPath is not null) yield return (o.LocalWavPath, SourceKind.Local);
        if (o.RemoteWavPath is not null) yield return (o.RemoteWavPath, SourceKind.Remote);
    }

    private static async IAsyncEnumerable<AudioFrame> ToAsync(IEnumerable<AudioFrame> frames)
    {
        foreach (var f in frames) yield return f;
        await Task.CompletedTask;
    }
}
