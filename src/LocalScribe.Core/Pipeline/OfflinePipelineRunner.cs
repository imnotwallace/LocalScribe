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
        var startedUtc = _time.GetUtcNow();
        var tz = _time.LocalTimeZone;
        var offset = tz.GetUtcOffset(startedUtc);
        var startedLocal = startedUtc.ToOffset(offset);

        SessionParticipant? self = string.IsNullOrEmpty(_settings.Self.Name) ? null
            : new SessionParticipant
            { Id = "p-self", Name = _settings.Self.Name, Role = _settings.Self.Role, Side = SourceKind.Local, IsSelf = true };
        var meta = SessionMeta.CreateDefault(AppKind.Manual, startedLocal, self);

        string id = SessionId.EnsureUnique(
            SessionId.New(startedLocal, AppKind.Manual, meta.Title),
            x => Directory.Exists(_paths.SessionDir(x)));
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(meta, ct);

        var sources = new List<SourceKind>();
        if (options.LocalWavPath is not null) sources.Add(SourceKind.Local);
        if (options.RemoteWavPath is not null) sources.Add(SourceKind.Remote);

        var sessionStore = new SessionStore(_paths.SessionJson(id));
        var live = new SessionRecord
        {
            Id = id, App = AppKind.Manual, StartedAtUtc = startedUtc,
            TimeZoneId = tz.Id, UtcOffsetMinutes = (int)offset.TotalMinutes,
            Sources = sources, AppVersion = _appVersion, Language = _settings.Language,
        };
        await sessionStore.SaveAsync(live, ct);                     // live record: recovery-compatible

        // 2) pipeline
        var plan = BackendSelector.Select(_hardware.Probe(), _settings);
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
                }
                else if (item is string marker)
                {
                    await merger.AppendMarkerAsync(marker, lastEndMs, ct);
                }
            }
        }, ct);

        var workerLoop = worker.RunAsync(ct);
        foreach (var (path, kind) in EnumerateInputs(options))
        {
            var segmenter = new SileroVadSegmenter(kind, options.Vad, _vadModelFactory());
            await foreach (var segment in segmenter.SegmentAsync(ToAsync(WavFileFrameReader.ReadFrames(path, kind)), ct))
                await worker.EnqueueAsync(segment, ct);
        }
        worker.Complete();
        await workerLoop;                                           // queue drained (spec 2.1 flush)
        outbox.Writer.Complete();
        await writerLoop;

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
