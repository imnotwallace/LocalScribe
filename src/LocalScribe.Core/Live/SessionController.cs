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

    /// <summary>Matters this recording is pre-tagged with (Stage 6.2). Seeds meta.MatterIds at
    /// Start and biases the Whisper initial prompt with those matters' vocabulary terms. Empty =
    /// record-first-classify-later (the default); the picker on the Record console is a convenience.</summary>
    public IReadOnlyList<string> MatterIds { get; init; } = [];

    /// <summary>Fix #2: how long a leg may keep producing peaks with NO transcript segment
    /// before SessionController raises a persistent SilentLegDetected (a wrong capture endpoint -
    /// e.g. the Communications-default device - records a noise floor above the Start-time peak
    /// probe's silence threshold but below actual speech, so VAD correctly emits zero segments and
    /// the probe never catches it). 15s default: long enough that normal conversational gaps
    /// never false-positive, short enough to warn the user well before the recording is lost.</summary>
    public int SilentLegGraceMs { get; init; } = 15000;
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
    private readonly Func<IReadOnlySet<string>> _availableModels;
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
        // Session-scoped, separate from FeedCts (Fix #3 / Task 6): the feed token aborts the
        // bounded queue's producer on a worker fault (C1 guard below), but capture/audio must
        // keep running - StartLeg takes both tokens so cancelling one never stops the other.
        public required CancellationTokenSource CaptureCts;
        public required LiveSourcePipeline Local;
        public required LiveSourcePipeline Remote;
        public required List<AlignedAudioWriter> AudioWriters;
        public required List<SourceKind> Retained;
        // Task 7 / Fix #2: per-leg silent-leg monitors, seeded to leg-start; reseeded on Resume
        // (see ResumeAsync) so the grace window restarts for a fresh leg.
        public required SilentLegMonitor LocalSilentMonitor;
        public required SilentLegMonitor RemoteSilentMonitor;
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
        // Fix #3 / Task 6: set once by the worker-fault continuation below (while Recording).
        // StopAsync's worker drain uses this to swallow the (already-surfaced) fault instead of
        // rethrowing it, so the session finalizes cleanly - audio was never stopped (Task 5).
        public bool TranscriptionFailed;
    }

    public SessionState State { get; private set; } = SessionState.Idle;
    public string? CurrentSessionId => _session?.Id;

    /// <summary>The live merger's full sorted view of the current session, or empty when Idle
    /// (design 5.4 4.2). Read synchronously from a LineInserted handler (the merger's consumer
    /// thread) for a consistent snapshot before marshalling to the UI thread.</summary>
    public IReadOnlyList<TranscriptLine> View => _session?.Merger.View ?? [];

    public event Action<SessionState>? StateChanged;
    public event Action<int, TranscriptLine>? LineInserted;
    public event Action<SourceKind, float>? PeakObserved;
    public event Action<string>? ErrorRaised;
    public event Action<string>? Notice;

    // Task 7 / Fix #2: sustained-no-speech "silent leg" monitor. SilentLegDetected is persistent
    // (raised once, not re-raised per peak) until a segment from that leg clears it. Driven
    // entirely off the existing PeakObserved/LineInserted plumbing below - no new timer thread.
    public event Action<SourceKind>? SilentLegDetected;
    public event Action<SourceKind>? SilentLegCleared;

    // Guards SilentLegMonitor access: PeakObserved fires on the capture thread, a segment insert
    // fires on the writer-loop thread (merger.LineInserted) - both touch the same Session's
    // monitors, so both go through this lock (brief: "guard with a lock or Interlocked").
    private readonly object _silentGate = new();

    public SessionController(StoragePaths paths, Func<Settings> settingsProvider,
        IEngineFactory engineFactory, Func<ISpeechProbabilityModel> vadModelFactory,
        IHardwareProbe hardware, ICaptureSourceProvider captureProvider, Func<IClock> clockFactory,
        TimeProvider time, string appVersion, Func<IReadOnlySet<string>>? availableModels = null)
        => (_paths, _settingsProvider, _engineFactory, _vadModelFactory, _hardware, _captureProvider,
            _clockFactory, _time, _appVersion, _availableModels)
         = (paths, settingsProvider, engineFactory, vadModelFactory, hardware, captureProvider,
            clockFactory, time, appVersion, availableModels ?? ModelPaths.AvailableModels);

    /// <summary>Convenience overload: a fixed Settings snapshot. Keeps every pre-Stage-4 call
    /// site and test compiling unchanged; production passes a live provider (design 6.2) so
    /// per-session inputs resolve at StartAsync, not at construction.</summary>
    public SessionController(StoragePaths paths, Settings settings, IEngineFactory engineFactory,
        Func<ISpeechProbabilityModel> vadModelFactory, IHardwareProbe hardware,
        ICaptureSourceProvider captureProvider, Func<IClock> clockFactory,
        TimeProvider time, string appVersion, Func<IReadOnlySet<string>>? availableModels = null)
        : this(paths, () => settings, engineFactory, vadModelFactory, hardware, captureProvider,
            clockFactory, time, appVersion, availableModels)
    {
    }

    private void SetState(SessionState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    /// <summary>Task 7 / Fix #2: routes a transcript segment insert into the matching leg's
    /// SilentLegMonitor and raises SilentLegCleared if it had been flagged. System-sourced
    /// lines (markers never reach here anyway - callers filter on Kind == Segment - but a
    /// defensive check costs nothing) are ignored: only Local/Remote legs are monitored.</summary>
    private void OnSegmentForSilentMonitor(TranscriptSource source,
        SilentLegMonitor localMonitor, SilentLegMonitor remoteMonitor, long nowMs)
    {
        if (source == TranscriptSource.System) return;
        var kind = source == TranscriptSource.Local ? SourceKind.Local : SourceKind.Remote;
        var monitor = source == TranscriptSource.Local ? localMonitor : remoteMonitor;
        bool cleared;
        lock (_silentGate) { cleared = monitor.OnSegment(nowMs); }
        if (cleared) SilentLegCleared?.Invoke(kind);
    }

    /// <summary>Task 7 / Fix #2: routes a per-frame peak into the matching leg's SilentLegMonitor
    /// and raises SilentLegDetected the moment the grace window first elapses with no segment.
    /// Only while Recording (a Paused leg has no capture running, so no peaks fire anyway, but
    /// Finalizing/Idle drain must never raise a stale detection off a leg that is being torn
    /// down).</summary>
    private void CheckSilentLeg(SourceKind kind,
        SilentLegMonitor localMonitor, SilentLegMonitor remoteMonitor, long nowMs)
    {
        if (State != SessionState.Recording) return;
        var monitor = kind == SourceKind.Local ? localMonitor : remoteMonitor;
        bool raise;
        lock (_silentGate) { raise = monitor.OnPeak(nowMs); }
        if (raise) SilentLegDetected?.Invoke(kind);
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

            // Fail-fast (Fix #1 / Task 3): resolve the model BEFORE anything else is created -
            // no capture sources opened, no preflight probe run, no session folder written. Root
            // cause this closes: Model=auto -> small.en not downloaded -> the worker faults
            // lazily -> Stop throws -> the session is lost as a dead-air "Recovered" husk. Auto
            // already downgrades to the best PRESENT model in the ladder (BackendSelector); an
            // explicit pick is validated verbatim here ("Start validates presence" per that
            // comment). This must sit before SessionBootstrap.StartAsync (which creates the
            // session folder) - NOT after it, even though BackendSelector.Select's own call site
            // further down in this method predates this check and still reads more naturally
            // there; moving it up here is required so a refusal never leaves a folder behind.
            // _availableModels is the single source of truth for both the downgrade computation
            // and the presence check below (injectable: defaults to ModelPaths.AvailableModels
            // in production; tests pass a deterministic fake so no ggml-*.bin or env var is
            // needed - LOCALSCRIBE_MODELS is process-global and would race across parallel
            // xUnit test classes).
            var available = _availableModels();
            var (plan, downgradedFrom) = BackendSelector.Select(_hardware.Probe(), settings, available);
            if (!available.Contains(plan.ModelName))
            {
                Notice?.Invoke($"Model '{plan.ModelName}' is not downloaded. Pick an available model in " +
                               "Settings > Transcription, or run tools/fetch-models.ps1.");
                return null;   // refuse: no session folder, no dead-air recording (State stays Idle)
            }
            if (downgradedFrom is not null)
                Notice?.Invoke($"Recording with {plan.ModelName}; {downgradedFrom} is not downloaded " +
                               "(download it for better accuracy).");

            // Every resource is held in a local as it is created so the catch below can
            // release exactly what exists if Start fails partway (brief contract: "dispose
            // whatever was created and rethrow with State back at Idle"). The Session object
            // is only constructed once nothing can fail anymore.
            ICaptureSource? micSource = null, remoteSource = null;
            TranscriptionWorker? worker = null;
            Channel<object>? outbox = null;
            Task? writerLoop = null, workerLoop = null;
            CancellationTokenSource? feedCts = null;
            CancellationTokenSource? captureCts = null;
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
                    [SourceKind.Local, SourceKind.Remote], devices, _time, _appVersion, ct,
                    options.MatterIds);

                var language = new LanguageResolver(settings.Language);
                // Per-matter prompt bias (Stage 6.2): load the picked matters (skip any missing/
                // corrupt file, exactly as SessionWriter's projection loader does) so their terms
                // join the global shortlist under the ~200-token cap. No picks => global-only.
                var mattersById = new Dictionary<string, Matter>();
                var matterStore = new MatterStore(_paths.MattersDir);
                foreach (string mid in options.MatterIds)
                {
                    var m = await matterStore.LoadAsync(mid, ct);
                    if (m is not null) mattersById[mid] = m;
                }
                string prompt = new VocabularyProvider(settings.Vocabulary, mattersById)
                    .BuildInitialPrompt(options.MatterIds);
                worker = new TranscriptionWorker(_engineFactory, plan, language, clock,
                    options.Worker with { InitialPrompt = prompt.Length == 0 ? null : prompt });

                var merger = new TranscriptMerger(new TranscriptStore(_paths.TranscriptJsonl(boot.Id)));
                await merger.InitializeAsync(ct);
                // Task 7 / Fix #2: seeded to leg-start (clock.ElapsedMs now, before either leg's
                // first frame) so a leg that never produces a single segment still starts its
                // grace window from a real timestamp, not 0.
                var localSilentMonitor = new SilentLegMonitor(options.SilentLegGraceMs, clock.ElapsedMs);
                var remoteSilentMonitor = new SilentLegMonitor(options.SilentLegGraceMs, clock.ElapsedMs);
                merger.LineInserted += (i, l) =>
                {
                    LineInserted?.Invoke(i, l);
                    if (l.Kind == TranscriptKind.Segment)
                        OnSegmentForSilentMonitor(l.Source, localSilentMonitor, remoteSilentMonitor, clock.ElapsedMs);
                };

                var ob = Channel.CreateUnbounded<object>();
                outbox = ob;
                var lastModel = new StrongBox<string?>();
                feedCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
                captureCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);

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
                local.PeakObserved += (s, p) =>
                { PeakObserved?.Invoke(s, p); CheckSilentLeg(s, localSilentMonitor, remoteSilentMonitor, clock.ElapsedMs); };
                remote.PeakObserved += (s, p) =>
                { PeakObserved?.Invoke(s, p); CheckSilentLeg(s, localSilentMonitor, remoteSilentMonitor, clock.ElapsedMs); };

                if (remoteSnap.FellBackToSystemMix)
                {
                    outbox.Writer.TryWrite(new MarkerAt(Markers.DegradedSystemAudioLoopback, clock.ElapsedMs));
                    Notice?.Invoke("Per-process capture unavailable - recording full system audio for the remote stream (possible bleed; use headphones).");
                }

                if (micSnap.FellBackToDefault)
                {
                    outbox.Writer.TryWrite(new MarkerAt(Markers.PinnedMicUnavailable, clock.ElapsedMs));
                    Notice?.Invoke("Pinned microphone unavailable - recording from the Windows Communications default instead.");
                }

                local.StartLeg(micSource, captureCts.Token, feedCts.Token);
                localLegStarted = true;
                remote.StartLeg(remoteSource, captureCts.Token, feedCts.Token);
                remoteLegStarted = true;

                _session = new Session
                {
                    Id = boot.Id, LiveRecord = boot.LiveRecord, Clock = clock, Plan = plan,
                    Language = language, Worker = worker, Merger = merger, Outbox = ob,
                    WriterLoop = writerLoop, WorkerLoop = workerLoop, FeedCts = feedCts,
                    CaptureCts = captureCts,
                    Local = local, Remote = remote, AudioWriters = audioWriters,
                    Retained = retained, LastModel = lastModel, Settings = settings,
                    RemoteDegraded = remoteSnap.FellBackToSystemMix,
                    LocalSilentMonitor = localSilentMonitor, RemoteSilentMonitor = remoteSilentMonitor,
                };
                SetState(SessionState.Recording);

                // C1 fault guard (see OfflinePipelineRunner): if the worker faults, the feed
                // legs are the bounded queue's only producers with no reader left - cancel the
                // feed token so they abort promptly instead of blocking forever. StopAsync
                // catches the resulting OperationCanceledException from its leg flushes and
                // falls through to await WorkerLoop, so the REAL exception surfaces there -
                // never the cancellation it caused.
                // Fix #3 / Task 6: a fault ALSO flags the session + emits the marker/notice, and
                // deliberately does NOT cancel captureCts - raw audio (Task 5's split) keeps
                // recording through a transcriber fault instead of being lost with the session.
                // Attached here (after _session/State are set), not right after `workerLoop =
                // worker.RunAsync(...)` above: a synchronously-faulting engine factory (e.g. a
                // missing-model FileNotFoundException thrown from CreateAsync before any real
                // await) completes workerLoop before this method ever yields, and
                // ExecuteSynchronously then runs this continuation INLINE at attach-time - too
                // early to see `_session` if attached earlier.
                var session = _session;
                _ = workerLoop.ContinueWith(t =>
                {
                    feedCts!.Cancel();                              // C1: unblock the feed (existing)
                    if (State == SessionState.Recording && ReferenceEquals(_session, session))
                    {
                        session.TranscriptionFailed = true;
                        session.Outbox.Writer.TryWrite(new MarkerAt(Markers.TranscriptionFailed, session.Clock.ElapsedMs));
                        ErrorRaised?.Invoke("TRANSCRIPTION_FAILED");
                        Notice?.Invoke("Live transcription stopped - audio is still recording. You can re-transcribe this session later.");
                    }
                }, CancellationToken.None,
                   TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                   TaskScheduler.Default);

                return boot.Id;
            }
            catch
            {
                // Partial-start cleanup: best-effort release of everything created so far,
                // then rethrow. State never left Idle (SetState(Recording) is the last
                // statement on the success path above).
                feedCts?.Cancel();
                captureCts?.Cancel();
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
                captureCts?.Dispose();
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
            // Task 7 / Fix #2: a fresh leg restarts the grace window - reseed both monitors to
            // now and drop any flag (a leg that was already flagged before Pause gets a clean
            // slate on Resume, exactly like a brand-new Start). Reset() reports whether a leg
            // was flagged at reset time so we can raise a matching SilentLegCleared for it -
            // notification symmetry: every SilentLegDetected must have a matching
            // SilentLegCleared, or a UI banner driven off those events would stay stuck showing
            // "silent" after a Resume even though the monitor cleared internally. Raised outside
            // the lock, matching CheckSilentLeg/OnSegmentForSilentMonitor above.
            bool localWasFlagged, remoteWasFlagged;
            lock (_silentGate)
            {
                localWasFlagged = s.LocalSilentMonitor.Reset(s.Clock.ElapsedMs);
                remoteWasFlagged = s.RemoteSilentMonitor.Reset(s.Clock.ElapsedMs);
            }
            if (localWasFlagged) SilentLegCleared?.Invoke(SourceKind.Local);
            if (remoteWasFlagged) SilentLegCleared?.Invoke(SourceKind.Remote);
            s.Local.StartLeg(micSource, s.CaptureCts.Token, s.FeedCts.Token);
            s.Remote.StartLeg(remoteSource, s.CaptureCts.Token, s.FeedCts.Token);
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
            long durationMs = 0;
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
                // Fix #3 / Task 6: a fault already flagged mid-session (the ContinueWith above)
                // has already surfaced via ErrorRaised/Notice - swallow it here instead of
                // rethrowing so the session finalizes on the SAME clean path as a healthy stop
                // (faulted stays false: full padded audio, recovered:false, marker present).
                try { await s.WorkerLoop; }
                catch when (s.TranscriptionFailed) { /* already surfaced mid-session; audio-only finalize */ }
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

                // Stage 5.4 Phase 3: capture the session end ONCE, then pad retained audio to it
                // so the audio files and the recorded DurationMs agree exactly. Legs settled above
                // (StopLegAndFlushAsync awaits the feed task that performs every Tap ->
                // audioWriter.Write) is what makes SamplesWritten final - the writer loop drained
                // above is a separate concern, the transcript outbox - padding is strictly
                // additive silence after the last recorded sample. Pad only on the clean path: a
                // pad fault must never mask an in-flight finalize fault, and a faulted finalize
                // keeps today's recovery semantics (no fabricated tail). The nested finally keeps
                // Dispose unconditional - a pad fault (e.g. disk full) cannot leak sink handles.
                durationMs = s.Clock.ElapsedMs;
                try
                {
                    if (!faulted)
                        foreach (var w in s.AudioWriters) w.PadToMs(durationMs);
                }
                finally
                {
                    foreach (var w in s.AudioWriters) w.Dispose();
                    s.FeedCts.Dispose();
                    s.CaptureCts.Dispose();
                    _session = null;
                }
            }

            long duration = durationMs;                            // captured at teardown - audio was padded to exactly this (spec 2.1)
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
