using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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

    /// <summary>How long the pre-flight probe listens to each side (spec 12.3). Real capture
    /// sources deliver frames as audio arrives, so this is real wall time per side (2x total) -
    /// tests shrink it; production leaves the 1 s default.</summary>
    public TimeSpan ProbeWindow { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>The live session lifecycle (spec 2.1): Idle -> Recording <-> Paused -> Finalizing
/// -> Idle. Composes two LiveSourcePipelines over the shared TranscriptionWorker/TranscriptMerger,
/// mirroring OfflinePipelineRunner's outbox/writer-loop and C1 fault-guard patterns. Pause STOPS
/// capture (privilege protection - nothing is transcribed during a paused sidebar); Resume starts
/// fresh legs. The session clock keeps ticking through Pause: durationMs = wall time at Stop.
/// All public methods serialize on one semaphore; events fire from worker threads while that
/// internal gate may still be held, so an event handler must never synchronously call back into
/// StartAsync/PauseAsync/ResumeAsync/StopAsync (deadlock) - marshal to another context first,
/// as Stage 3b's dispatcher does, before re-entering the controller.</summary>
public sealed class SessionController
{
    private readonly StoragePaths _paths;
    private readonly Func<Settings> _settingsProvider;
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
        // Written by the writer loop, which starts before the Session object exists
        // (Session is only constructed once Start can no longer fail) - hence a shared box.
        public required StrongBox<string?> LastModel;
        // Start-time Settings snapshot (design 6.2): finalize (StopAsync) renders and records
        // with the settings the session STARTED under, even if a save lands mid-session.
        public required Settings Settings;
        // Tracks whether the remote leg is already known-degraded (spec 12.1: fallback is
        // marked once, not on every resume). Set from the Start-time snapshot below; a later
        // Resume that falls back sets it too. A recovery back to per-process on a later resume
        // is never un-marked - only the transition INTO degradation is a marked event.
        public bool RemoteDegraded;
    }

    public SessionState State { get; private set; } = SessionState.Idle;
    public string? CurrentSessionId => _session?.Id;

    public event Action<SessionState>? StateChanged;
    public event Action<int, TranscriptLine>? LineInserted;
    public event Action<SourceKind, float>? PeakObserved;
    public event Action<string>? ErrorRaised;
    public event Action<string>? Notice;

    public SessionController(StoragePaths paths, Func<Settings> settingsProvider,
        IEngineFactory engineFactory, Func<ISpeechProbabilityModel> vadModelFactory,
        IHardwareProbe hardware, ICaptureSourceProvider captureProvider, Func<IClock> clockFactory,
        TimeProvider time, string appVersion)
        => (_paths, _settingsProvider, _engineFactory, _vadModelFactory, _hardware, _captureProvider,
            _clockFactory, _time, _appVersion)
         = (paths, settingsProvider, engineFactory, vadModelFactory, hardware, captureProvider,
            clockFactory, time, appVersion);

    /// <summary>Convenience overload: a fixed Settings snapshot. Keeps every pre-Stage-4 call
    /// site and test compiling unchanged; production passes a live provider (design 6.2) so
    /// per-session inputs resolve at StartAsync, not at construction.</summary>
    public SessionController(StoragePaths paths, Settings settings, IEngineFactory engineFactory,
        Func<ISpeechProbabilityModel> vadModelFactory, IHardwareProbe hardware,
        ICaptureSourceProvider captureProvider, Func<IClock> clockFactory,
        TimeProvider time, string appVersion)
        : this(paths, () => settings, engineFactory, vadModelFactory, hardware, captureProvider,
            clockFactory, time, appVersion)
    {
    }

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
            // Design 6.2 settings seam: per-session inputs resolve NOW, not at construction.
            var settings = _settingsProvider();

            // Every resource is held in a local as it is created so the catch below can
            // release exactly what exists if Start fails partway (brief contract: "dispose
            // whatever was created and rethrow with State back at Idle"). The Session object
            // is only constructed once nothing can fail anymore.
            ICaptureSource? micSource = null, remoteSource = null;
            TranscriptionWorker? worker = null;
            Channel<object>? outbox = null;
            Task? writerLoop = null, workerLoop = null;
            CancellationTokenSource? feedCts = null;
            var audioWriters = new List<AlignedAudioWriter>();
            LiveSourcePipeline? local = null, remote = null;
            bool localLegStarted = false, remoteLegStarted = false;

            try
            {
                // Pre-flight peak probe (spec 12.3): throwaway sources, own try/finally disposal
                // below so a probe-time exception falls straight into the partial-failure catch
                // with nothing else created yet (micSource/remoteSource/etc. all still null).
                if (options.RunPreflightProbe)
                {
                    var (probeMic, _) = _captureProvider.CreateMic(clock);
                    var (probeRemote, _) = _captureProvider.CreateRemote(clock);
                    try
                    {
                        float localPeak = await PreflightProbe.MeasurePeakAsync(
                            probeMic, options.ProbeWindow, ct);
                        float remotePeak = await PreflightProbe.MeasurePeakAsync(
                            probeRemote, options.ProbeWindow, ct);
                        if (localPeak < PreflightProbe.SilencePeakThreshold)
                        {
                            ErrorRaised?.Invoke("SILENT_SOURCE");
                            Notice?.Invoke("Microphone level is near zero - check mute/input device before relying on this recording.");
                        }
                        if (remotePeak < PreflightProbe.SilencePeakThreshold)
                        {
                            ErrorRaised?.Invoke("SILENT_SOURCE");
                            Notice?.Invoke("Remote audio level is near zero - is meeting audio actually playing?");
                        }
                    }
                    finally
                    {
                        probeMic.Dispose();
                        probeRemote.Dispose();
                    }
                }

                (micSource, var micSnap) = _captureProvider.CreateMic(clock);
                (remoteSource, var remoteSnap) = _captureProvider.CreateRemote(clock);
                var devices = new DeviceSnapshot { Mic = micSnap, Remote = remoteSnap };

                // Stage 4 (design 7.4): a manual Start derives AppKind from the planner-resolved
                // remote image BEFORE bootstrap, so session.json App, the folder id, and the
                // default meta Title/Medium (SessionMeta.CreateDefault) all agree. Derive only
                // when RemoteSnapshot.App is a planner-MATCHED image: a per-process plan, or a
                // full-mix fallback (which exposes the matched image via RemotePlan.App). An
                // explicitly pinned systemMix has FellBackToSystemMix=false and its App is the
                // raw user setting - never derived. Unknown/null images resolve to Manual, so
                // unresolved plans stay Manual. Non-manual options are always honored verbatim.
                AppKind app = options.App;
                if (options.App == AppKind.Manual
                    && (remoteSnap.Mode == RemoteMode.PerProcess || remoteSnap.FellBackToSystemMix))
                    app = AppKindResolver.FromProcessImage(remoteSnap.App);

                var boot = await SessionBootstrap.StartAsync(_paths, settings, app,
                    [SourceKind.Local, SourceKind.Remote], devices, _time, _appVersion, ct);

                var plan = BackendSelector.Select(_hardware.Probe(), settings);
                var language = new LanguageResolver(settings.Language);
                string prompt = new VocabularyProvider(settings.Vocabulary, new Dictionary<string, Matter>())
                    .BuildInitialPrompt([]);
                worker = new TranscriptionWorker(_engineFactory, plan, language, clock,
                    options.Worker with { InitialPrompt = prompt.Length == 0 ? null : prompt });

                var merger = new TranscriptMerger(new TranscriptStore(_paths.TranscriptJsonl(boot.Id)));
                await merger.InitializeAsync(ct);
                merger.LineInserted += (i, l) => LineInserted?.Invoke(i, l);

                var ob = Channel.CreateUnbounded<object>();
                outbox = ob;
                var lastModel = new StrongBox<string?>();
                feedCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);

                worker.SegmentTranscribed += ts => ob.Writer.TryWrite(ts);
                worker.MarkerRaised += m => ob.Writer.TryWrite(m);
                worker.ErrorRaised += e => ErrorRaised?.Invoke(e);

                writerLoop = Task.Run(async () =>
                {
                    long lastEndMs = 0;
                    await foreach (object item in ob.Reader.ReadAllAsync(CancellationToken.None))
                    {
                        if (item is TranscribedSegment ts)
                        {
                            var line = await merger.AppendSegmentAsync(ts, CancellationToken.None);
                            lastEndMs = Math.Max(lastEndMs, line.EndMs);
                            lastModel.Value = ts.ModelName;
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

                workerLoop = worker.RunAsync(feedCts.Token);
                // C1 fault guard (see OfflinePipelineRunner): if the worker faults, the feed
                // legs are the bounded queue's only producers with no reader left - cancel the
                // feed token so they abort promptly instead of blocking forever. StopAsync
                // catches the resulting OperationCanceledException from its leg flushes and
                // falls through to await WorkerLoop, so the REAL exception surfaces there -
                // never the cancellation it caused.
                _ = workerLoop.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Cancel(),
                    feedCts, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                AlignedAudioWriter? localWriter = null, remoteWriter = null;
                var retained = new List<SourceKind>();
                if (settings.AudioRetention != "never")
                {
                    localWriter = new AlignedAudioWriter(AudioSinkFactory.Create(
                        _paths.AudioFile(boot.Id, SourceKind.Local, settings.AudioFormat), settings.AudioFormat));
                    audioWriters.Add(localWriter);
                    remoteWriter = new AlignedAudioWriter(AudioSinkFactory.Create(
                        _paths.AudioFile(boot.Id, SourceKind.Remote, settings.AudioFormat), settings.AudioFormat));
                    audioWriters.Add(remoteWriter);
                    retained.AddRange([SourceKind.Local, SourceKind.Remote]);
                }

                local = new LiveSourcePipeline(SourceKind.Local, options.Vad,
                    _vadModelFactory, worker, localWriter);
                remote = new LiveSourcePipeline(SourceKind.Remote, options.Vad,
                    _vadModelFactory, worker, remoteWriter);
                local.PeakObserved += (s, p) => PeakObserved?.Invoke(s, p);
                remote.PeakObserved += (s, p) => PeakObserved?.Invoke(s, p);

                if (remoteSnap.FellBackToSystemMix)
                {
                    outbox.Writer.TryWrite(new MarkerAt(Markers.DegradedSystemAudioLoopback, clock.ElapsedMs));
                    Notice?.Invoke("Per-process capture unavailable - recording full system audio for the remote stream (possible bleed; use headphones).");
                }

                local.StartLeg(micSource, feedCts.Token);
                localLegStarted = true;
                remote.StartLeg(remoteSource, feedCts.Token);
                remoteLegStarted = true;

                _session = new Session
                {
                    Id = boot.Id, LiveRecord = boot.LiveRecord, Clock = clock, Plan = plan,
                    Language = language, Worker = worker, Merger = merger, Outbox = ob,
                    WriterLoop = writerLoop, WorkerLoop = workerLoop, FeedCts = feedCts,
                    Local = local, Remote = remote, AudioWriters = audioWriters,
                    Retained = retained, LastModel = lastModel, Settings = settings,
                    RemoteDegraded = remoteSnap.FellBackToSystemMix,
                };
                SetState(SessionState.Recording);
                return boot.Id;
            }
            catch
            {
                // Partial-start cleanup: best-effort release of everything created so far,
                // then rethrow. State never left Idle (SetState(Recording) is the last
                // statement on the success path above).
                feedCts?.Cancel();
                if (localLegStarted) { try { await local!.StopLegAndFlushAsync(); } catch { } }
                else micSource?.Dispose();
                if (remoteLegStarted) { try { await remote!.StopLegAndFlushAsync(); } catch { } }
                else remoteSource?.Dispose();
                if (worker is not null)
                {
                    try { worker.Complete(); } catch { }
                    if (workerLoop is not null) { try { await workerLoop; } catch { } }
                }
                outbox?.Writer.TryComplete();
                if (writerLoop is not null) { try { await writerLoop; } catch { } }
                foreach (var w in audioWriters) { try { w.Dispose(); } catch { } }
                feedCts?.Dispose();
                throw;
            }
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
            var (remoteSource, remoteSnap) = _captureProvider.CreateRemote(s.Clock);
            if (remoteSnap.FellBackToSystemMix && !s.RemoteDegraded)
            {
                // Spec 12.1: the fallback must never be silent - a resumed leg can degrade
                // even when the session started per-process (the app's render session may
                // have gone inactive during the pause). Marked once per degradation.
                s.RemoteDegraded = true;
                s.Outbox.Writer.TryWrite(new MarkerAt(Markers.DegradedSystemAudioLoopback, s.Clock.ElapsedMs));
                Notice?.Invoke("Per-process capture unavailable after resume - recording full system audio for the remote stream (possible bleed; use headphones).");
            }
            s.Local.StartLeg(micSource, s.FeedCts.Token);
            s.Remote.StartLeg(remoteSource, s.FeedCts.Token);
            SetState(SessionState.Recording);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Settles one leg during Stop: swallows the C1-guard cancellation (the worker
    /// fault surfaces from awaiting WorkerLoop instead) and captures any genuine leg fault so
    /// the sibling leg and the worker drain always run. Never lets a leg fault skip cleanup.</summary>
    private static async Task<Exception?> SettleLegAsync(LiveSourcePipeline leg, CancellationTokenSource feedCts)
    {
        try { await leg.StopLegAndFlushAsync(); return null; }
        catch (OperationCanceledException) when (feedCts.IsCancellationRequested) { return null; }
        catch (Exception ex) { return ex; }
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
                Exception? legFault = null;
                if (!wasPaused)                     // paused legs are already stopped+flushed
                {
                    // Settle each leg independently: neither a C1-guard cancellation nor a
                    // genuine leg fault (e.g. disk-full from the feed task) may skip the
                    // sibling flush or the worker drain below. A worker fault surfaces from
                    // awaiting WorkerLoop and wins; a pure leg fault is rethrown (original
                    // stack) only after both legs settled and the queue drained.
                    legFault = await SettleLegAsync(s.Local, s.FeedCts);
                    Exception? remoteFault = await SettleLegAsync(s.Remote, s.FeedCts);
                    legFault ??= remoteFault;
                }
                s.Worker.Complete();
                await s.WorkerLoop;                     // a worker fault surfaces here and wins
                if (legFault is not null)               // both legs settled, queue drained:
                    ExceptionDispatchInfo.Capture(legFault).Throw();   // leg fault is primary
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
                Model = s.LastModel.Value ?? s.Plan.ModelName,
                Backend = s.Plan.Backend.ToString().ToUpperInvariant(),
                Language = s.Language.Locked ?? s.Settings.Language,
                RetainedAudioSources = s.Retained,
            }, ct);
            await new SessionWriter(_paths, s.Settings, _time).RegenerateProjectionsAsync(s.Id, ct);

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
