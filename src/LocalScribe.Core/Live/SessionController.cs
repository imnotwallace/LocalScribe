using System.Threading.Channels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using LocalScribe.Core.Vocabulary;
namespace LocalScribe.Core.Live;

public enum SessionState { Idle, Recording, Paused, Finalizing }

public sealed record LiveSessionOptions
{
    public AppKind App { get; init; } = AppKind.Manual;
    public VadOptions Vad { get; init; } = new();
    public TranscriptionWorkerOptions Worker { get; init; } = new();
    public bool RunPreflightProbe { get; init; } = true;
}

/// <summary>The live session lifecycle (spec 2.1): Idle -> Recording <-> Paused -> Finalizing
/// -> Idle. Composes two LiveSourcePipelines over the shared TranscriptionWorker/TranscriptMerger,
/// mirroring OfflinePipelineRunner's outbox/writer-loop and C1 fault-guard patterns. Pause STOPS
/// capture (privilege protection - nothing is transcribed during a paused sidebar); Resume starts
/// fresh legs. The session clock keeps ticking through Pause: durationMs = wall time at Stop.
/// All public methods serialize on one semaphore; events fire from worker threads - UI adapters
/// (Stage 3b) must marshal to their dispatcher.</summary>
public sealed class SessionController
{
    private readonly StoragePaths _paths;
    private readonly Settings _settings;
    private readonly IEngineFactory _engineFactory;
    private readonly Func<ISpeechProbabilityModel> _vadModelFactory;
    private readonly IHardwareProbe _hardware;
    private readonly ICaptureSourceProvider _captureProvider;
    private readonly Func<IClock> _clockFactory;
    private readonly TimeProvider _time;
    private readonly string _appVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private sealed record MarkerAt(string Message, long AtMs);

    // Per-session state (null when Idle).
    private Session? _session;

    private sealed class Session
    {
        public required string Id;
        public required SessionRecord LiveRecord;
        public required IClock Clock;
        public required BackendPlan Plan;
        public required LanguageResolver Language;
        public required TranscriptionWorker Worker;
        public required TranscriptMerger Merger;
        public required Channel<object> Outbox;
        public required Task WriterLoop;
        public required Task WorkerLoop;
        public required CancellationTokenSource FeedCts;
        public required LiveSourcePipeline Local;
        public required LiveSourcePipeline Remote;
        public required List<AlignedAudioWriter> AudioWriters;
        public required List<SourceKind> Retained;
        public string? LastModel;
    }

    public SessionState State { get; private set; } = SessionState.Idle;
    public string? CurrentSessionId => _session?.Id;

    public event Action<SessionState>? StateChanged;
    public event Action<int, TranscriptLine>? LineInserted;
    public event Action<SourceKind, float>? PeakObserved;
    public event Action<string>? ErrorRaised;
    public event Action<string>? Notice;

    public SessionController(StoragePaths paths, Settings settings, IEngineFactory engineFactory,
        Func<ISpeechProbabilityModel> vadModelFactory, IHardwareProbe hardware,
        ICaptureSourceProvider captureProvider, Func<IClock> clockFactory,
        TimeProvider time, string appVersion)
        => (_paths, _settings, _engineFactory, _vadModelFactory, _hardware, _captureProvider,
            _clockFactory, _time, _appVersion)
         = (paths, settings, engineFactory, vadModelFactory, hardware, captureProvider,
            clockFactory, time, appVersion);

    private void SetState(SessionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    public async Task<string?> StartAsync(LiveSessionOptions options, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State != SessionState.Idle)
            {
                Notice?.Invoke("Already recording - stop the current session first.");
                return null;
            }

            var clock = _clockFactory();

            // Task 9 inserts the pre-flight peak probe here (options.RunPreflightProbe).

            var (micSource, micSnap) = _captureProvider.CreateMic(clock);
            var (remoteSource, remoteSnap) = _captureProvider.CreateRemote(clock);
            var devices = new DeviceSnapshot { Mic = micSnap, Remote = remoteSnap };

            var boot = await SessionBootstrap.StartAsync(_paths, _settings, options.App,
                [SourceKind.Local, SourceKind.Remote], devices, _time, _appVersion, ct);

            var plan = BackendSelector.Select(_hardware.Probe(), _settings);
            var language = new LanguageResolver(_settings.Language);
            string prompt = new VocabularyProvider(_settings.Vocabulary, new Dictionary<string, Matter>())
                .BuildInitialPrompt([]);
            var worker = new TranscriptionWorker(_engineFactory, plan, language, clock,
                options.Worker with { InitialPrompt = prompt.Length == 0 ? null : prompt });

            var merger = new TranscriptMerger(new TranscriptStore(_paths.TranscriptJsonl(boot.Id)));
            await merger.InitializeAsync(ct);
            merger.LineInserted += (i, l) => LineInserted?.Invoke(i, l);

            var outbox = Channel.CreateUnbounded<object>();
            var session = new Session
            {
                Id = boot.Id, LiveRecord = boot.LiveRecord, Clock = clock, Plan = plan,
                Language = language, Worker = worker, Merger = merger, Outbox = outbox,
                WriterLoop = Task.CompletedTask, WorkerLoop = Task.CompletedTask,
                FeedCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None),
                Local = null!, Remote = null!, AudioWriters = [], Retained = [],
            };

            worker.SegmentTranscribed += ts => outbox.Writer.TryWrite(ts);
            worker.MarkerRaised += m => outbox.Writer.TryWrite(m);
            worker.ErrorRaised += e => ErrorRaised?.Invoke(e);

            session.WriterLoop = Task.Run(async () =>
            {
                long lastEndMs = 0;
                await foreach (object item in outbox.Reader.ReadAllAsync(CancellationToken.None))
                {
                    if (item is TranscribedSegment ts)
                    {
                        var line = await merger.AppendSegmentAsync(ts, CancellationToken.None);
                        lastEndMs = Math.Max(lastEndMs, line.EndMs);
                        session.LastModel = ts.ModelName;
                    }
                    else if (item is MarkerAt at)
                    {
                        await merger.AppendMarkerAsync(at.Message, at.AtMs, CancellationToken.None);
                    }
                    else if (item is string marker)
                    {
                        await merger.AppendMarkerAsync(marker, lastEndMs, CancellationToken.None);
                    }
                }
            }, CancellationToken.None);

            session.WorkerLoop = worker.RunAsync(session.FeedCts.Token);
            // C1 fault guard (see OfflinePipelineRunner): if the worker faults, the feed loops
            // are the bounded queue's only producers with no reader left - cancel feeding so
            // they abort promptly; the real exception is recovered by awaiting WorkerLoop.
            _ = session.WorkerLoop.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Cancel(),
                session.FeedCts, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            AlignedAudioWriter? localWriter = null, remoteWriter = null;
            if (_settings.AudioRetention != "never")
            {
                localWriter = new AlignedAudioWriter(AudioSinkFactory.Create(
                    _paths.AudioFile(boot.Id, SourceKind.Local, _settings.AudioFormat), _settings.AudioFormat));
                remoteWriter = new AlignedAudioWriter(AudioSinkFactory.Create(
                    _paths.AudioFile(boot.Id, SourceKind.Remote, _settings.AudioFormat), _settings.AudioFormat));
                session.AudioWriters.AddRange([localWriter, remoteWriter]);
                session.Retained.AddRange([SourceKind.Local, SourceKind.Remote]);
            }

            session.Local = new LiveSourcePipeline(SourceKind.Local, options.Vad,
                _vadModelFactory, worker, localWriter);
            session.Remote = new LiveSourcePipeline(SourceKind.Remote, options.Vad,
                _vadModelFactory, worker, remoteWriter);
            session.Local.PeakObserved += (s, p) => PeakObserved?.Invoke(s, p);
            session.Remote.PeakObserved += (s, p) => PeakObserved?.Invoke(s, p);

            // Task 9 emits the degraded marker here when remoteSnap.FellBackToSystemMix.

            session.Local.StartLeg(micSource, session.FeedCts.Token);
            session.Remote.StartLeg(remoteSource, session.FeedCts.Token);

            _session = session;
            SetState(SessionState.Recording);
            return session.Id;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PauseAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State != SessionState.Recording || _session is null)
            {
                Notice?.Invoke("Nothing to pause.");
                return;
            }
            var s = _session;
            await s.Local.StopLegAndFlushAsync();               // VAD flush: trailing words kept
            await s.Remote.StopLegAndFlushAsync();
            s.Outbox.Writer.TryWrite(new MarkerAt(Markers.PausedByUser, s.Clock.ElapsedMs));
            SetState(SessionState.Paused);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State != SessionState.Paused || _session is null)
            {
                Notice?.Invoke("Nothing to resume.");
                return;
            }
            var s = _session;
            s.Outbox.Writer.TryWrite(new MarkerAt(Markers.Resumed, s.Clock.ElapsedMs));
            var (micSource, _) = _captureProvider.CreateMic(s.Clock);      // fresh leg: re-resolves device
            var (remoteSource, _) = _captureProvider.CreateRemote(s.Clock);
            s.Local.StartLeg(micSource, s.FeedCts.Token);
            s.Remote.StartLeg(remoteSource, s.FeedCts.Token);
            SetState(SessionState.Recording);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> StopAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State is not (SessionState.Recording or SessionState.Paused) || _session is null)
            {
                Notice?.Invoke("Nothing to stop.");
                return null;
            }
            var s = _session;
            bool wasPaused = State == SessionState.Paused;   // capture BEFORE SetState below
            SetState(SessionState.Finalizing);

            bool faulted = false;
            try
            {
                if (!wasPaused)                     // paused legs are already stopped+flushed
                {
                    await s.Local.StopLegAndFlushAsync();
                    await s.Remote.StopLegAndFlushAsync();
                }
                s.Worker.Complete();
                await s.WorkerLoop;                               // drained (spec 2.1 flush)
            }
            catch
            {
                faulted = true;
                throw;
            }
            finally
            {
                s.Outbox.Writer.TryComplete();
                if (faulted) { try { await s.WriterLoop; } catch { } }
                else await s.WriterLoop;

                foreach (var w in s.AudioWriters) w.Dispose();
                s.FeedCts.Dispose();
                _session = null;
            }

            long duration = s.Clock.ElapsedMs;                    // wall time incl. pauses (spec 2.1)
            await new SessionStore(_paths.SessionJson(s.Id)).SaveAsync(s.LiveRecord with
            {
                EndedAtUtc = _time.GetUtcNow(),
                DurationMs = duration,
                SegmentCount = s.Merger.View.Count(l => l.Kind == TranscriptKind.Segment),
                MarkerCount = s.Merger.View.Count(l => l.Kind == TranscriptKind.Marker),
                Model = s.LastModel ?? s.Plan.ModelName,
                Backend = s.Plan.Backend.ToString().ToUpperInvariant(),
                Language = s.Language.Locked ?? _settings.Language,
                RetainedAudioSources = s.Retained,
            }, ct);
            await new SessionWriter(_paths, _settings, _time).RegenerateProjectionsAsync(s.Id, ct);

            SetState(SessionState.Idle);
            return s.Id;
        }
        catch
        {
            // A finalize fault must not strand the controller in Finalizing forever.
            _session = null;
            SetState(SessionState.Idle);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
}
