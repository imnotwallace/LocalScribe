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

    /// <summary>The per-leg grace window for the Start-time silent-source check (spec 12.3).
    /// Fix (2026-07-08): no longer a pre-capture throwaway probe - each leg's first ProbeWindow of
    /// REAL captured audio is peak-sampled off the session clock (both legs concurrently, never
    /// delaying capture). Tests shrink it; production leaves the 1 s default.</summary>
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
    // Fix #4 (win-arm64): volatile enforces the "set _finalizing = s BEFORE _session = null" ordering
    // the `View => (_session ?? _finalizing)` read below relies on, so a cross-thread LineInserted racing
    // the Stop transition can never observe both fields null and blank the live view.
    private volatile Session? _session;

    // Fix (2026-07-08): a completed Stop kicks the remaining transcription drain + re-finalize onto
    // this task and returns Idle immediately. Task.CompletedTask when no finalize is in flight. A
    // new StartAsync awaits this first so the old engine is disposed before a new one is created.
    private Task _pendingFinalize = Task.CompletedTask;
    public Task PendingFinalize => _pendingFinalize;

    // Fix (2026-07-08): the session whose transcription tail is still draining on the background
    // finalizer (FinalizeInBackgroundAsync), after StopAsync has already nulled _session and returned
    // Idle. Its merger keeps receiving the tail's segments, and LineInserted keeps firing, so View must
    // still resolve to THIS merger during the drain - otherwise the live transcript view would read an
    // empty View and wipe itself the moment a segment finishes transcribing after Stop (worse with a
    // slow engine, which is exactly when the drain runs). Null except during a background finalize.
    // Fix #4 (win-arm64): volatile, paired with volatile _session, to guarantee the ordering above.
    private volatile Session? _finalizing;

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
        public required StrongBox<string?> LastWeightsFile;   // exact ggml file (provenance)
        // Start-time Settings snapshot (design 6.2): finalize (StopAsync) renders and records
        // with the settings the session STARTED under, even if a save lands mid-session.
        public required Settings Settings;
        // Tracks whether the remote leg is already known-degraded (spec 12.1: fallback is
        // marked once, not on every resume). Set from the Start-time snapshot below; a later
        // Resume that falls back sets it too. A recovery back to per-process on a later resume
        // is never un-marked - only the transition INTO degradation is a marked event.
        public bool RemoteDegraded;
        // Capture Scope Control (design 2026-07-12 section 2 point 6): the session's CURRENT
        // remote target, seeded at Start from the composed settings and updated by every live
        // SetRemoteCaptureAsync / Resume rebuild. RemoteSetting is a value record, so the
        // idempotency guard is a plain record-equality check. Mutated only under _gate.
        public required RemoteSetting CurrentRemoteTarget;
        // Mute controls (design 2026-07-10 section 1): mutated only under _gate by
        // SetLocalMuteAsync; read by the VM via the SessionController.LocalMuted property.
        public bool LocalMuted;
        // Fix #3 / Task 6: set once by the worker-fault continuation below (while Recording).
        // StopAsync's worker drain uses this to swallow the (already-surfaced) fault instead of
        // rethrowing it, so the session finalizes cleanly - audio was never stopped (Task 5).
        // Fix #3 (harden): an ATOMIC flag - the marker write is check-and-set in TWO places (the
        // mid-session ContinueWith and FinalizeInBackgroundAsync's catch), which could otherwise both
        // fire (a duplicate marker) or race. TryMarkTranscriptionFailed's CompareExchange makes exactly
        // one site win, so the "transcription failed" marker is written exactly once. The bool getter
        // keeps every existing read site (StopAsync/CheckSilentLeg) working unchanged.
        private int _transcriptionFailed;
        public bool TranscriptionFailed => Volatile.Read(ref _transcriptionFailed) == 1;
        public bool TryMarkTranscriptionFailed()
            => Interlocked.CompareExchange(ref _transcriptionFailed, 1, 0) == 0;
    }

    public SessionState State { get; private set; } = SessionState.Idle;
    public string? CurrentSessionId => _session?.Id;

    /// <summary>The id of the session whose transcription tail is still draining on the background
    /// finalizer (Fix 2026-07-08 / design 2026-07-12 section 1), or null when no finalize is in
    /// flight. Set at Stop before the Idle transition; cleared in FinalizeInBackgroundAsync's finally
    /// just before SessionFinalizeCompleted. A benign cross-thread read (cosmetic label only).</summary>
    public string? FinalizingSessionId => _finalizing?.Id;

    /// <summary>One-engine-at-a-time, reverse direction (design 2026-07-13 section 3.2): set
    /// once by the composition root after the offline RetranscriptionRunner exists (the runner
    /// is constructed AFTER this controller and probes it for the forward direction, so a ctor
    /// parameter cannot express the cycle - same set-by-composition seam as
    /// MaintenanceService.StartupScanTask). Returns a user-facing refusal reason while another
    /// engine owner is busy, else null. Read under _gate at the top of StartAsync. Best-effort
    /// against user-level concurrency: like the existing await-PendingFinalize guard, it closes
    /// the seconds-apart double-engine cases, not a same-instant theoretical race.</summary>
    public Func<string?>? ExternalEngineBusy { get; set; }

    /// <summary>The engine plan Start WOULD bind right now (design 2026-07-13 section 5 item 4:
    /// the ready card's engine chip): the same BackendSelector.Select over the same injected
    /// hardware/settings/available-models seams StartAsync itself resolves, so the chip can never
    /// drift from what Start actually does. ModelName comes back CANONICAL (Select routes explicit
    /// picks through ModelFileResolver.CanonicalName, so a persisted "small.en-q8_0" previews as
    /// "small.en" - the quant choice is a file detail the chip does not show), and CpuThreads rides
    /// along per Select's contract. Read-only and side-effect-free on the controller; the
    /// FIRST call may probe hardware (nvidia-smi, cached thereafter by LiveHardwareProbe), so UI
    /// callers read it off the UI thread (the console reads it inside its Task.Run refresh).
    /// Informational only - it never gates or delays Start (locked anti-pattern, design section 7).</summary>
    public BackendPlan PreviewEnginePlan
        => BackendSelector.Select(_hardware.Probe(), _settingsProvider(), _availableModels()).Plan;

    /// <summary>The running session's Start-time engine plan, or null when Idle (design 2026-07-13
    /// section 5 item 4: the while-recording engine chip). Benign cross-thread read (cosmetic
    /// label only, same contract as FinalizingSessionId above).</summary>
    public BackendPlan? ActiveEnginePlan => _session?.Plan;

    /// <summary>The model that most recently produced a transcribed segment for the running session
    /// (tracks mid-session ladder downgrades via TranscribedSegment.ModelName), falling back to the
    /// Start plan's model before the first segment; null when Idle. The SAME LastModel-then-plan
    /// resolution PersistFinalAsync writes to session.json's Model field, so the live chip and the
    /// read-view footer agree by construction. (Deliberately NOT LastWeightsFile - that is
    /// file-level provenance for the evidentiary record, not a display name.)</summary>
    public string? ActiveModelName
        => _session is { } s ? (s.LastModel.Value ?? s.Plan.ModelName) : null;

    /// <summary>The backend the running session's worker is CURRENTLY transcribing on - tracks a
    /// mid-session downgrade that hits the ladder floor and falls to CPU
    /// (<see cref="TranscriptionWorker.EffectiveBackend"/>); null when Idle. The live engine chip
    /// binds to this so a floor-fall to CPU is not shown as the stale Start-time backend (B1-1).
    /// session.json's Backend field remains the Start plan's (the weights file + weights-changed
    /// marker are its evidentiary record of a mid-session engine change).</summary>
    public Backend? ActiveEngineBackend => _session?.Worker.EffectiveBackend;

    /// <summary>The running session's rolling transcription realtime factor (see
    /// <see cref="TranscriptionWorker.RecentRtf"/>), or null when Idle or before the first tracked
    /// segment. Drives the console's keep-up chip (design 2026-07-13 section 5 item 4).</summary>
    public double? RecentTranscriptionRtf => _session?.Worker.RecentRtf;

    /// <summary>The live merger's full sorted view of the current session, or empty when Idle
    /// (design 5.4 4.2). Read synchronously from a LineInserted handler (the merger's consumer
    /// thread) for a consistent snapshot before marshalling to the UI thread. Falls back to the
    /// background-finalizing session (Fix 2026-07-08) so the tail transcribed after Stop still
    /// resolves to a live merger and backfills the view instead of clearing it.</summary>
    public IReadOnlyList<TranscriptLine> View => (_session ?? _finalizing)?.Merger.View ?? [];

    public event Action<SessionState>? StateChanged;
    public event Action<int, TranscriptLine>? LineInserted;
    public event Action<SourceKind, float>? PeakObserved;
    public event Action<string>? ErrorRaised;
    public event Action<string>? Notice;

    /// <summary>Fires exactly once when a session's background finalize settles on disk (design
    /// 2026-07-12 section 1) - on BOTH the success path (PersistFinalAsync wrote EndedAtUtc) and the
    /// failure path (FINALIZE_FAILED, EndedAtUtc never written). Passes the session id; raised AFTER
    /// _finalizing is cleared, so a handler re-reading FinalizingSessionId sees null. The Sessions
    /// list re-reads disk truth on it, so one event covers both outcomes.</summary>
    public event Action<string>? SessionFinalizeCompleted;

    /// <summary>True while the user has muted their own side via LocalScribe (design 2026-07-10
    /// section 1): the LOCAL leg is not captured at all while muted - privileged asides never enter
    /// the evidentiary record. Independent of Pause; false when Idle.</summary>
    public bool LocalMuted => _session?.LocalMuted ?? false;
    public event Action<bool>? LocalMuteChanged;

    /// <summary>Device-level (endpoint) mic mute observed on the local leg's capture device
    /// (design 2026-07-10 section 2) - the mute that silences LocalScribe's own stream too, so it
    /// must surface instantly, not after the silent-leg grace. Distinct from LocalMuted (the
    /// user's deliberate in-LocalScribe mute).</summary>
    public event Action<bool>? MicDeviceMuteChanged;

    // Task 7 / Fix #2: sustained-no-speech "silent leg" monitor. SilentLegDetected is persistent
    // (raised once, not re-raised per peak) until a segment from that leg clears it. Driven
    // entirely off the existing PeakObserved/LineInserted plumbing below - no new timer thread.
    public event Action<SourceKind>? SilentLegDetected;
    public event Action<SourceKind>? SilentLegCleared;

    // Task 8: field-like events are invocable only from within the declaring class, and this
    // codebase has no InternalsVisibleTo wiring between LocalScribe.Core and the test assemblies
    // (checked: none exists), so SessionViewModelTests (LocalScribe.App.Tests) needs a public
    // hook to drive these directly instead of waiting out the real 15s grace window end-to-end.
    // Production code never calls these two methods.
    public void RaiseSilentLegDetectedForTest(SourceKind kind) => SilentLegDetected?.Invoke(kind);
    public void RaiseSilentLegClearedForTest(SourceKind kind) => SilentLegCleared?.Invoke(kind);

    // Guards SilentLegMonitor access: PeakObserved fires on the capture thread, a segment insert
    // fires on the writer-loop thread (merger.LineInserted) - both touch the same Session's
    // monitors, so both go through this lock (brief: "guard with a lock or Interlocked").
    private readonly object _silentGate = new();

    // Fix (2026-07-08): the Start-time silent-source check now reads the REAL capture stream's
    // first ProbeWindow instead of a pre-capture throwaway source, so the probe never delays
    // capture. Null when RunPreflightProbe is false. Fix #5 (accuracy): the FIELD lifecycle
    // (created in StartAsync, nulled in StopAsync/ResumeAsync) runs under _gate, NOT _silentGate;
    // FeedStartPeak serializes only the window.Feed() CALL under _silentGate (and captures the
    // window reference into a local BEFORE locking, so a concurrent null of the field is benign).
    private PreflightProbe.StartPeakWindow? _localStartPeak;
    private PreflightProbe.StartPeakWindow? _remoteStartPeak;

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
        // Final-review Finding 2: a dead transcriber (worker fault, Fix #3) leaves audio+peaks
        // flowing with no segments ever arriving, so after the grace window BOTH legs would trip
        // "No speech detected" on top of the accurate TRANSCRIPTION_FAILED notice - misleading
        // (the device is fine; transcription died). Read unsynchronized: TranscriptionFailed is
        // monotonic false->true set once on the fault continuation, so a one-tick-stale read here
        // is harmless.
        if (_session?.TranscriptionFailed == true) return;
        var monitor = kind == SourceKind.Local ? localMonitor : remoteMonitor;
        bool raise;
        lock (_silentGate) { raise = monitor.OnPeak(nowMs); }
        if (raise) SilentLegDetected?.Invoke(kind);
    }

    /// <summary>Fix (2026-07-08): raises SILENT_SOURCE once if a leg's first ProbeWindow of REAL
    /// audio stayed below the silence floor (dead/all-zeros endpoint), replacing the pre-capture
    /// throwaway probe. Serialized on _silentGate; each window decides at most once.</summary>
    private void FeedStartPeak(SourceKind kind, float peak, long nowMs)
    {
        var window = kind == SourceKind.Local ? _localStartPeak : _remoteStartPeak;
        if (window is null) return;
        bool silent;
        lock (_silentGate) { silent = window.Feed(peak, nowMs); }
        if (!silent) return;
        ErrorRaised?.Invoke("SILENT_SOURCE");
        Notice?.Invoke(kind == SourceKind.Local
            ? "Microphone level is near zero - check mute/input device before relying on this recording."
            : "Remote audio level is near zero - is meeting audio actually playing?");
    }

    /// <summary>Hooks a local leg's endpoint-mute capability (no-op for sources without it).
    /// Surfaces an already-muted device at leg start immediately. The handler is guarded by
    /// session identity + LocalMuted (a deliberately muted user needs no device warning) and only
    /// reports while Recording. Marker writes go through the outbox (thread-safe channel); the
    /// event fires on the COM callback thread - consumers marshal (same contract as PeakObserved).</summary>
    private void HookDeviceMute(ICaptureSource micSource, Session session)
    {
        if (micSource is not IEndpointMuteObservable m) return;
        if (m.DeviceMuted) OnDeviceMuteChanged(session, true);
        m.DeviceMuteChanged += muted => OnDeviceMuteChanged(session, muted);
    }

    private void OnDeviceMuteChanged(Session session, bool muted)
    {
        if (!ReferenceEquals(_session, session)) return;         // stale leg after Stop/new session
        if (session.LocalMuted) return;                          // deliberate mute: no warning
        if (State != SessionState.Recording) return;
        session.Outbox.Writer.TryWrite(new MarkerAt(
            muted ? Markers.MicDeviceMuted : Markers.MicDeviceUnmuted, session.Clock.ElapsedMs));
        MicDeviceMuteChanged?.Invoke(muted);
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

            // One-engine-at-a-time (design 2026-07-13 section 3.2): an offline re-transcription
            // holds a whisper engine; starting a live session on top would double-load the model
            // (VRAM) and break the locked one-engine rule. Refuse exactly like the not-Idle
            // branch above - Notice + null, nothing created, State stays Idle.
            if (ExternalEngineBusy?.Invoke() is string engineBusy)
            {
                Notice?.Invoke(engineBusy);
                return null;
            }

            // Fix (2026-07-08): a previous session's transcription tail may still be draining on a
            // background task (holds the old whisper engine + keeps firing LineInserted). Wait for it so we
            // never hold two engines at once (VRAM double-load) and the old drain's lines can't bleed into
            // the new session's live view. Fast when already complete. Swallow a prior finalize fault - it
            // was already surfaced by FinalizeInBackgroundAsync (FINALIZE_FAILED).
            if (!_pendingFinalize.IsCompleted)
            {
                Notice?.Invoke("Finishing the previous recording's transcript...");
                try { await _pendingFinalize; } catch { }
            }
            _pendingFinalize = Task.CompletedTask;

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
                var lastWeightsFile = new StrongBox<string?>();
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
                            lastWeightsFile.Value = ts.WeightsFile;
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

                // Fix (2026-07-08): run the worker on a pool thread. RunAsync's first statement is
                // `await CreateEngineAsync`, which for the real WhisperEngineFactory resolves a
                // Task.FromResult whose value (the WhisperNetEngine ctor: WhisperFactory.FromPath +
                // builder.Build(), a multi-second synchronous model/CUDA load) is built inline - so a
                // direct call would block StartAsync here, BEFORE StartLeg starts capture (lost
                // opening + blank lead-in). Task.Run moves that synchronous prologue off the Start
                // thread so capture starts immediately; the model loads concurrently and the bounded
                // worker queue absorbs the backlog until it is ready.
                workerLoop = Task.Run(() => worker.RunAsync(feedCts.Token), CancellationToken.None);

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

                _localStartPeak = options.RunPreflightProbe
                    ? new PreflightProbe.StartPeakWindow((int)options.ProbeWindow.TotalMilliseconds) : null;
                _remoteStartPeak = options.RunPreflightProbe
                    ? new PreflightProbe.StartPeakWindow((int)options.ProbeWindow.TotalMilliseconds) : null;

                local.PeakObserved += (s, p) =>
                {
                    PeakObserved?.Invoke(s, p);
                    CheckSilentLeg(s, localSilentMonitor, remoteSilentMonitor, clock.ElapsedMs);
                    FeedStartPeak(s, p, clock.ElapsedMs);
                };
                remote.PeakObserved += (s, p) =>
                {
                    PeakObserved?.Invoke(s, p);
                    CheckSilentLeg(s, localSilentMonitor, remoteSilentMonitor, clock.ElapsedMs);
                    FeedStartPeak(s, p, clock.ElapsedMs);
                };

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
                    Retained = retained, LastModel = lastModel, LastWeightsFile = lastWeightsFile,
                    Settings = settings,
                    RemoteDegraded = remoteSnap.FellBackToSystemMix,
                    CurrentRemoteTarget = settings.Remote,
                    LocalSilentMonitor = localSilentMonitor, RemoteSilentMonitor = remoteSilentMonitor,
                };
                SetState(SessionState.Recording);
                HookDeviceMute(micSource, _session);

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
                // Task.Run(() => worker.RunAsync(...))` above: the worker now runs on a POOL THREAD
                // concurrently with the rest of this synchronous prologue, so a fast fault (e.g. a
                // missing-model FileNotFoundException thrown from CreateAsync before any real await)
                // can complete workerLoop on the pool thread BEFORE the main thread reaches this
                // ContinueWith call. ExecuteSynchronously then runs this continuation INLINE at
                // attach-time - a genuine race, not an eliminated path (Task.Run made it a pool-thread
                // race rather than the old synchronous inline run, but it is still possible). It stays
                // harmless because the attach point sits AFTER the `_session`/`State` assignments in
                // program order: whichever thread runs the continuation and whenever, it observes
                // `_session`/`State` already set, so the `ReferenceEquals(_session, session)` guard is
                // valid. Attaching earlier (before those assignments) would let an inline run see them
                // unset.
                var session = _session;
                _ = workerLoop.ContinueWith(t =>
                {
                    feedCts!.Cancel();                              // C1: unblock the feed (existing)
                    // Fix #3 (harden): the atomic TryMark is the LAST term so the marker + notices fire
                    // only when THIS site wins the CAS (exactly-once). If the fault surfaces so late that
                    // Stop already left Recording, the guard short-circuits before the CAS and the
                    // FinalizeInBackgroundAsync catch owns the marker instead.
                    if (State == SessionState.Recording && ReferenceEquals(_session, session)
                        && session.TryMarkTranscriptionFailed())
                    {
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

            // Build BOTH fallible sources before committing anything (2026-07-11 review fix, hardened
            // 2026-07-12 for M1): CreateRemote/CreateMic are INERT builds that can genuinely throw
            // (COMException on a vanished device, NotSupportedException on a bad mix format). A build throw
            // here disposes whatever was created, leaves State untouched (still Paused), writes NO marker,
            // and rethrows - the caller sees it and a retry Resume can succeed once the cause is fixed
            // (no leg started yet, so no wedge). The remote WASAPI ACTIVATION, however, runs LATER in
            // StartLeg/Start() - a per-app target adopted from a change made WHILE PAUSED activates for the
            // first time only there. A StartLeg activation throw must NOT wedge the session: it degrades to
            // full system mix (recording the involuntary DegradedSystemAudioLoopback) so Resume still
            // completes with remote recording; if system mix ALSO fails to start it records
            // RemoteCaptureLost and completes local-only. Crucially it NEVER rethrows (unlike
            // SetRemoteCaptureAsync) - rethrowing would leave State Paused with the local leg already
            // running, so a retry Resume would then throw "leg already running" (the wedge M1 removes). The
            // "resumed" marker is written AFTER both legs start, so a StartLeg throw can never leave an
            // orphan "resumed" line in the transcript.
            ICaptureSource? remoteSource = null;
            ICaptureSource? micSource = null;
            RemoteSnapshot remoteSnap;
            try
            {
                (remoteSource, remoteSnap) = _captureProvider.CreateRemote(s.Clock);
                if (!s.LocalMuted)
                    (micSource, _) = _captureProvider.CreateMic(s.Clock);
            }
            catch
            {
                remoteSource?.Dispose();
                throw;
            }

            // Task 7 / Fix #2: a fresh leg restarts the grace window - reseed both monitors to
            // now and drop any flag (a leg that was already flagged before Pause gets a clean
            // slate on Resume, exactly like a brand-new Start). Reset() reports whether a leg
            // was flagged at reset time so we can raise a matching SilentLegCleared for it -
            // notification symmetry: every SilentLegDetected must have a matching
            // SilentLegCleared, or a UI banner driven off those events would stay stuck showing
            // "silent" after a Resume even though the monitor cleared internally. Raised outside
            // the lock, matching CheckSilentLeg/OnSegmentForSilentMonitor above. Pure state resets
            // (no marker, no leg work), so they stay BEFORE the legs start.
            bool localWasFlagged, remoteWasFlagged;
            lock (_silentGate)
            {
                localWasFlagged = s.LocalSilentMonitor.Reset(s.Clock.ElapsedMs);
                remoteWasFlagged = s.RemoteSilentMonitor.Reset(s.Clock.ElapsedMs);
            }
            if (localWasFlagged) SilentLegCleared?.Invoke(SourceKind.Local);
            if (remoteWasFlagged) SilentLegCleared?.Invoke(SourceKind.Remote);
            // The Start-time silent-source probe is a START-only check (the old throwaway probe ran once,
            // synchronously, inside StartAsync and could never straddle a Pause). If a Pause interrupted it
            // before the grace window closed, abandon it here so a post-Resume peak can't produce a bogus
            // one-shot SILENT_SOURCE off a truncated pre-pause sample. The sustained SilentLegMonitor
            // (re-armed just above) covers a genuinely silent resumed leg.
            _localStartPeak = _remoteStartPeak = null;
            // Resume honors mute (design 2026-07-10 section 1): a user who muted for a privileged
            // aside and then paused must never be silently unmuted by Resume - the local leg
            // restarts only on an explicit SetLocalMuteAsync(false). micSource was already built
            // (or left null if muted) in the try block above.
            if (micSource is not null)
                s.Local.StartLeg(micSource, s.CaptureCts.Token, s.FeedCts.Token);

            // Remote leg start with the SetRemoteCaptureAsync fail-safe (M1): the adopted target (a
            // paused-then-changed per-app override is adopted here) activates its WASAPI client for the
            // first time in StartLeg/Start(). If that throws, the half-started leg is reset
            // (StopLegAndFlushAsync awaits its _feed/_audioLoop and nulls _legSource so the retry StartLeg
            // is legal) and remote falls back to full system mix so the counterparty is never silently
            // dropped (LOCKED evidentiary invariant), marked as an involuntary degrade below.
            RemoteSetting resolvedTarget = _settingsProvider().Remote;   // a paused override change is adopted here
            try
            {
                s.Remote.StartLeg(remoteSource!, s.CaptureCts.Token, s.FeedCts.Token);  // non-null: the build try above returned without throwing
            }
            catch
            {
                await s.Remote.StopLegAndFlushAsync();       // reset the half-started leg so the retry StartLeg is legal
                var mix = new RemoteSetting { Mode = RemoteMode.SystemMix };
                try
                {
                    var (mixSource, mixSnap) = _captureProvider.CreateRemote(s.Clock, mix);
                    s.Remote.StartLeg(mixSource, s.CaptureCts.Token, s.FeedCts.Token);
                    resolvedTarget = mix;                                 // CurrentRemoteTarget must reflect reality
                    remoteSnap = mixSnap with { FellBackToSystemMix = true };   // force the involuntary-degrade marker below
                }
                catch
                {
                    // System-mix loopback ALSO failed to start (essentially never - whole-machine loopback).
                    // Reset the second failed leg, RECORD the loss so it is never silent, notice the user -
                    // and UNLIKE SetRemoteCaptureAsync, DO NOT rethrow. Rethrowing would leave State Paused
                    // with the local leg already running, so a retry Resume would throw "leg already
                    // running" (the wedge M1 removes). Fall through to SetState(Recording): the resume
                    // completes local-only with the loss honestly recorded; the user retries via the picker.
                    await s.Remote.StopLegAndFlushAsync();
                    s.Outbox.Writer.TryWrite(new MarkerAt(Markers.RemoteCaptureLost, s.Clock.ElapsedMs));
                    Notice?.Invoke("Remote capture unavailable after resume - the adopted target and the system-mix fallback both failed to start. Only your microphone is still being recorded.");
                    remoteSnap = remoteSnap with { FellBackToSystemMix = false };   // leg stopped, not degraded -> suppress the degrade marker
                }
            }

            // Resumed marker + involuntary-degrade marker AFTER both legs (moved for M1): a StartLeg throw
            // above can no longer leave an orphan "resumed" line. remoteSnap now reflects the ACTUALLY
            // resolved remote leg - a planner-level per-app->system-mix fallback during the pause OR the
            // activation-failure fallback above - so the one-per-degradation guard covers both.
            s.Outbox.Writer.TryWrite(new MarkerAt(Markers.Resumed, s.Clock.ElapsedMs));
            if (remoteSnap.FellBackToSystemMix && !s.RemoteDegraded)
            {
                // Spec 12.1: the fallback must never be silent - a resumed leg can degrade even when the
                // session started per-process (the app's render session may have gone inactive during the
                // pause, or the adopted per-app target failed to activate at Resume). Marked once per
                // degradation.
                s.RemoteDegraded = true;
                s.Outbox.Writer.TryWrite(new MarkerAt(Markers.DegradedSystemAudioLoopback, s.Clock.ElapsedMs));
                Notice?.Invoke("Per-process capture unavailable after resume - recording full system audio for the remote stream (possible bleed; use headphones).");
            }

            s.CurrentRemoteTarget = resolvedTarget;   // reflects reality: the adopted target, or system-mix on fallback
            SetState(SessionState.Recording);
            // Hook AFTER SetState(Recording): HookDeviceMute's initial DeviceMuted read goes through
            // OnDeviceMuteChanged's State==Recording guard, so hooking any earlier would silently
            // swallow a device already muted while paused - it must surface AT Resume ("surfaces an
            // already-muted device at leg start immediately"), not on the next transition. Null when
            // muted: a muted Resume starts no local leg, so there is nothing to hook (the unmute
            // branch of SetLocalMuteAsync hooks its own fresh leg).
            if (micSource is not null) HookDeviceMute(micSource, s);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Mutes/unmutes the user's own side (design 2026-07-10 section 1). Muted = the local
    /// leg is stopped and captures NOTHING (one-sided Pause; the muted span is silence in the
    /// retained file, bracketed by markers). Unmute starts a fresh local leg exactly like Resume's
    /// local half. Valid while Recording or Paused; Resume honors the muted state (never silently
    /// unmutes - see ResumeAsync). Idempotent: setting the current state again is a no-op.</summary>
    public async Task SetLocalMuteAsync(bool muted, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State is not (SessionState.Recording or SessionState.Paused) || _session is null)
            {
                Notice?.Invoke("Nothing to mute - not recording.");
                return;
            }
            var s = _session;
            if (s.LocalMuted == muted) return;                     // idempotent: no duplicate markers

            if (muted)
            {
                if (State == SessionState.Recording)
                    await s.Local.StopLegAndFlushAsync();           // Pause's clean flush: trailing words kept
                s.LocalMuted = true;
                s.Outbox.Writer.TryWrite(new MarkerAt(Markers.LocalMuted, s.Clock.ElapsedMs));
                _localStartPeak = null;                             // Start-only probe: abandoned like Resume does
                // A stale "no speech" banner must not outlive the leg it described - the muted
                // state line supersedes it (same reset-and-clear symmetry as ResumeAsync).
                bool wasFlagged;
                lock (_silentGate) { wasFlagged = s.LocalSilentMonitor.Reset(s.Clock.ElapsedMs); }
                if (wasFlagged) SilentLegCleared?.Invoke(SourceKind.Local);
            }
            else
            {
                if (State == SessionState.Recording)
                {
                    // Build the leg BEFORE committing state/marker (2026-07-11 review fix):
                    // CreateMic and StartLeg genuinely throw (COMException on an unplugged
                    // device, NotSupportedException on a bad mix format). The old order
                    // committed LocalMuted=false + wrote the "microphone unmuted" marker FIRST,
                    // so a throw here left the session reporting unmuted with an orphan marker
                    // while the local leg was actually dead - and the idempotency guard above
                    // (`s.LocalMuted == muted`) then blocked every retry-mute attempt forever
                    // (evidentiary violation: markers must reflect reality). On a throw here the
                    // exception propagates to the caller, state stays truthfully muted, and no
                    // marker is written.
                    var (micSource, _) = _captureProvider.CreateMic(s.Clock);   // fresh leg: re-resolves device (like Resume)
                    s.Local.StartLeg(micSource, s.CaptureCts.Token, s.FeedCts.Token);

                    s.LocalMuted = false;
                    s.Outbox.Writer.TryWrite(new MarkerAt(Markers.LocalUnmuted, s.Clock.ElapsedMs));
                    bool wasFlagged;
                    lock (_silentGate) { wasFlagged = s.LocalSilentMonitor.Reset(s.Clock.ElapsedMs); }
                    if (wasFlagged) SilentLegCleared?.Invoke(SourceKind.Local);
                    // Hook AFTER the LocalMuted=false commit above (not folded into the
                    // build-first cluster): HookDeviceMute's initial DeviceMuted read goes
                    // through OnDeviceMuteChanged's `session.LocalMuted` guard ("deliberate
                    // mute: no warning"), so hooking while LocalMuted is still true would
                    // silently swallow the "device already muted" surfacing this exact unmute
                    // path is required to produce (spec 2.1: surfaces at every local-leg start,
                    // including unmute). HookDeviceMute itself is fail-open/non-throwing (see
                    // its doc comment), so it carries none of the evidentiary risk above.
                    HookDeviceMute(micSource, s);
                }
                else
                {
                    // While Paused: no leg work happens here (Resume starts it, and Resume's own
                    // fallible-build-first fix covers that path) - state+marker only, same order
                    // as before this fix.
                    s.LocalMuted = false;
                    s.Outbox.Writer.TryWrite(new MarkerAt(Markers.LocalUnmuted, s.Clock.ElapsedMs));
                }
            }
            LocalMuteChanged?.Invoke(muted);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Mid-recording remote-target hot-swap (design 2026-07-12 section 2). CreateRemote is
    /// INERT (it only builds the source object; ProcessLoopbackCapture's ctor just assigns fields), so
    /// it is called BEFORE the old leg is torn down: a throw from CreateRemote itself (rare) aborts
    /// with the OLD leg untouched and NO marker (the caller reverts the picker), exactly like
    /// ResumeAsync / unmute. The WASAPI activation ACTUALLY runs later, in StartLeg/Start() (design
    /// section 2), AFTER the old leg has been flushed - so a StartLeg activation throw would leave the
    /// old leg gone. That must NOT silently drop counterparty audio (LOCKED evidentiary invariant): it
    /// falls back to full system-mix capture (reusing the involuntary DegradedSystemAudioLoopback
    /// marker, NOT a "by user" line) so remote keeps recording; if system mix ALSO fails to start
    /// (essentially never - whole-machine loopback) the remote leg is stopped, RemoteCaptureLost + a
    /// notice are written, and the call rethrows so the caller reverts. On success the remote leg is
    /// VAD-flushed then restarted on the SAME pipeline (retained FLAC + transcript stay continuous;
    /// AlignedAudioWriter silence-pads the sub-second gap), the remote silent monitor is reset, and a
    /// marker is emitted per the ACTUALLY-RESOLVED plan. Requires Recording; idempotent when the
    /// requested target already matches the running leg. A Paused session takes no leg action here -
    /// the caller updates the override only, and Resume adopts it.</summary>
    public async Task SetRemoteCaptureAsync(RemoteSetting target, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (State != SessionState.Recording || _session is null)
            {
                Notice?.Invoke("Not recording - cannot change the remote capture target.");
                return;
            }
            var s = _session;
            if (target == s.CurrentRemoteTarget) return;   // idempotent: value-equal request, nothing built

            // Build (inert): CreateRemote only constructs the source - the WASAPI activation is deferred
            // to StartLeg/Start() below. A throw HERE leaves the running leg untouched + no marker.
            var (newSource, snap) = _captureProvider.CreateRemote(s.Clock, target);

            // Commit: flush the old leg (trailing words kept), start the new one on the same pipeline.
            await s.Remote.StopLegAndFlushAsync();
            try
            {
                s.Remote.StartLeg(newSource, s.CaptureCts.Token, s.FeedCts.Token);
            }
            catch
            {
                // The requested target's WASAPI activation failed in StartLeg/Start() - AFTER the old
                // leg was torn down (that is where ProcessLoopbackCapture activates, not in the inert
                // CreateRemote). The old leg is gone, so leaving remote dead would SILENTLY drop
                // counterparty audio. Fall back to full system-mix capture so remote keeps recording,
                // recorded honestly as an INVOLUNTARY degrade (reused DegradedSystemAudioLoopback).
                //
                // Reset the half-started leg first: StartLeg set _legSource BEFORE Start() threw, so the
                // pipeline believes a leg is running and its _feed/_audioLoop tasks are draining an empty
                // bridge. StopLegAndFlushAsync completes that bridge, AWAITS both tasks (so the retry
                // StartLeg below cannot race a stale task against the new bridge/channel), disposes the
                // failed newSource, and resets _legSource to null so the retry StartLeg is legal.
                await s.Remote.StopLegAndFlushAsync();

                var mix = new RemoteSetting { Mode = RemoteMode.SystemMix };
                try
                {
                    var (mixSource, mixSnap) = _captureProvider.CreateRemote(s.Clock, mix);
                    s.Remote.StartLeg(mixSource, s.CaptureCts.Token, s.FeedCts.Token);
                    target = mix;                                 // CurrentRemoteTarget must reflect reality
                    // Force the fell-back flag so WriteRemoteChangeMarker takes the involuntary-degrade
                    // branch (a raw SystemMix plan has FellBackToSystemMix=false = a deliberate change).
                    snap = mixSnap with { FellBackToSystemMix = true };
                }
                catch
                {
                    // System-mix loopback also failed to start. Reset the second failed leg (drains its
                    // tasks + disposes mixSource), RECORD the loss so it is never silent, notice the user,
                    // and rethrow so the caller reverts the picker. State stays Recording with the remote
                    // leg stopped; the local leg keeps recording.
                    await s.Remote.StopLegAndFlushAsync();
                    s.Outbox.Writer.TryWrite(new MarkerAt(Markers.RemoteCaptureLost, s.Clock.ElapsedMs));
                    Notice?.Invoke("Remote capture stopped - the new target and the system-mix fallback both failed to start. Only your microphone is still being recorded.");
                    throw;
                }
            }

            // Fresh leg (either the requested target or the system-mix fallback): reseed the silent
            // monitor (drop any stale "no speech" flag) and abandon the start-only peak probe, exactly
            // like ResumeAsync's remote half.
            bool wasFlagged;
            lock (_silentGate) { wasFlagged = s.RemoteSilentMonitor.Reset(s.Clock.ElapsedMs); }
            if (wasFlagged) SilentLegCleared?.Invoke(SourceKind.Remote);
            _remoteStartPeak = null;

            s.CurrentRemoteTarget = target;   // target = mix on the fallback path
            WriteRemoteChangeMarker(s, snap);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Emits the design-2026-07-12 section 3 marker for a live remote switch, from the
    /// actually-resolved snapshot so the record never lies. An app that FELL BACK to system mix is
    /// an involuntary degrade - reuse the existing DegradedSystemAudioLoopback (no "by user"),
    /// marked once per degradation like ResumeAsync. Explicit system mix is a deliberate scope
    /// change; a clean per-app capture is a marked recovery. Both clear RemoteDegraded so a later
    /// involuntary fallback can mark again.</summary>
    private void WriteRemoteChangeMarker(Session s, RemoteSnapshot snap)
    {
        if (snap.FellBackToSystemMix)
        {
            if (!s.RemoteDegraded)
            {
                s.RemoteDegraded = true;
                s.Outbox.Writer.TryWrite(new MarkerAt(Markers.DegradedSystemAudioLoopback, s.Clock.ElapsedMs));
                Notice?.Invoke("Per-process capture unavailable - recording full system audio for the remote stream (possible bleed; use headphones).");
            }
            return;
        }
        if (snap.Mode == RemoteMode.SystemMix)
        {
            s.RemoteDegraded = false;
            s.Outbox.Writer.TryWrite(new MarkerAt(Markers.RemoteCaptureChangedSystemMix, s.Clock.ElapsedMs));
            return;
        }
        s.RemoteDegraded = false;
        s.Outbox.Writer.TryWrite(new MarkerAt(
            string.Format(Markers.RemoteCaptureChangedPerApp, snap.App), s.Clock.ElapsedMs));
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
        Session? s = null;
        try
        {
            if (State is not (SessionState.Recording or SessionState.Paused) || _session is null)
            {
                Notice?.Invoke("Nothing to stop.");
                return null;
            }
            s = _session;
            bool wasPaused = State == SessionState.Paused;   // capture BEFORE SetState below
            SetState(SessionState.Finalizing);

            // Fix (2026-07-08): halt-then-finalize. Snapshot the TRUE stop instant BEFORE any drain, and
            // halt BOTH legs' capture at the same instant so the remote leg stops recording the moment
            // Stop is pressed instead of over-recording while the local leg settles (the old
            // drain-then-finalize settled legs SEQUENTIALLY and read the clock AFTER draining the whole
            // backlog, so the drain's wall-time became trailing silence + inflated duration). HaltCapture
            // completes each leg's frame bridge up front - both legs stop taking NEW frames together,
            // while already-buffered frames still drain into the audio writer via the clean EOF that
            // SettleLegAsync awaits, so NO captured audio is lost (cancelling CaptureCts would abandon the
            // buffer). Audio is padded to this snapshot and finalized synchronously below; only the
            // transcription tail is deferred to a background task.
            bool faulted = false;
            long durationMs = s.Clock.ElapsedMs;      // the true stop instant, BEFORE any drain
            // Fix #1 (evidentiary): snapshot the wall-clock stop instant HERE, at the SAME point as
            // durationMs. PersistFinalAsync runs on the background finalizer AFTER the transcription
            // tail drains; reading _time.GetUtcNow() there would drift EndedAtUtc forward by the drain's
            // wall time (disagreeing with DurationMs, which correctly stays at the stop instant).
            var endedAtUtc = _time.GetUtcNow();
            if (!wasPaused)                           // paused legs already halted+flushed at Pause
            {
                s.Local.HaltCapture();                // both legs stop taking new frames at the same
                s.Remote.HaltCapture();               // instant (remote no longer records during local settle)
            }
            try
            {
                Exception? legFault = null;
                if (!wasPaused)                     // paused legs are already stopped+flushed
                {
                    // Settle each leg independently (drain the buffered frames + VAD EOF flush): a genuine
                    // leg fault (e.g. disk-full from the audio write, or the source's Stop() throwing) may
                    // not skip the sibling flush. A real leg fault is captured and rethrown (original
                    // stack) only after both legs have settled. The C1-guard cancellation (a worker fault
                    // cancelled the feed token) is swallowed inside SettleLegAsync.
                    // Fix #2 (KNOWN LIMITATION - document only): the VAD EOF flush here enqueues the
                    // trailing segment onto the worker's BOUNDED queue (capacity 64, FullMode.Wait). If
                    // that queue is already FULL and the model is still loading (nothing draining it),
                    // the trailing-flush EnqueueAsync can block Stop until a slot frees - feedCt cancels
                    // only on a worker FAULT, not on a slow load. This is bounded and far better than the
                    // old always-drain Stop; a proper fix (defer the feed-settle to the background task and
                    // keep only the audio-settle synchronous) is a known follow-up.
                    legFault = await SettleLegAsync(s.Local, s.FeedCts);
                    Exception? remoteFault = await SettleLegAsync(s.Remote, s.FeedCts);
                    legFault ??= remoteFault;
                }
                if (legFault is not null)
                    ExceptionDispatchInfo.Capture(legFault).Throw();   // leg fault is primary
            }
            catch
            {
                faulted = true;
                throw;
            }
            finally
            {
                // Audio is finalized synchronously, ALWAYS before StopAsync returns and NEVER on the
                // background task: pad retained audio to the stop-instant snapshot on the clean path (so
                // the files and DurationMs agree exactly - spec 2.1), then close every sink. Pad only on
                // the clean path: a faulted finalize keeps today's recovery semantics (no fabricated
                // tail). The nested finally keeps Dispose unconditional - a pad fault (e.g. disk full)
                // cannot leak a sink handle.
                try
                {
                    if (!faulted)
                        foreach (var w in s.AudioWriters) w.PadToMs(durationMs);
                }
                finally
                {
                    foreach (var w in s.AudioWriters) w.Dispose();
                    s.CaptureCts.Dispose();
                    _localStartPeak = _remoteStartPeak = null;
                }
            }

            // CLEAN PATH ONLY (a genuine leg fault rethrew above and is torn down in the catch below):
            // hand the remaining transcription drain + session.json/projection write to a background task
            // and return Idle immediately. Audio is already complete and closed (above), so a slow or
            // failed drain can never affect the raw recording. _finalizing keeps View resolving to this
            // session's merger while the tail drains (so the live view backfills, not clears); it is set
            // BEFORE _session is nulled so a LineInserted racing the transition never sees both null.
            _finalizing = s;
            _pendingFinalize = FinalizeInBackgroundAsync(s, durationMs, endedAtUtc);
            _session = null;
            SetState(SessionState.Idle);
            return s.Id;
        }
        catch
        {
            // A genuine leg fault (disk-full audio write) - or any other finalize-path fault - reaches
            // here with the worker, writer loop, and FeedCts still LIVE, because the background finalizer
            // only runs on the clean path. Tear them down synchronously and best-effort so those tasks
            // never leak forever (the worker would block on a queue that is never Completed), then
            // rethrow. Audio was NOT padded on this path (the fault skipped the pad above), preserving
            // today's recovery semantics for the raw recording.
            if (s is not null)
            {
                try { s.Worker.Complete(); } catch { }
                try { await s.WorkerLoop; } catch { }
                s.Outbox.Writer.TryComplete();
                try { await s.WriterLoop; } catch { }
                try { s.FeedCts.Dispose(); } catch { }
            }
            _session = null;
            SetState(SessionState.Idle);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fix (2026-07-08): drains the remaining transcription backlog and persists the final
    /// session.json + projections AFTER Stop has already finalized audio and returned Idle. Audio is
    /// complete and closed before this runs (StopAsync, Task 3), so a slow/failed drain never affects
    /// the raw recording. Swallows a worker fault the same way the synchronous path did (already
    /// surfaced mid-session via TRANSCRIPTION_FAILED - audio-only finalize with the marker, not a
    /// recovery husk). Never throws to an unobserved task.</summary>
    private async Task FinalizeInBackgroundAsync(Session s, long durationMs, DateTimeOffset endedAtUtc)
    {
        try
        {
            s.Worker.Complete();
            try { await s.WorkerLoop; }
            catch
            {
                // The worker faulted (e.g. a lazy model-load failure). Normally the mid-session
                // ContinueWith already set TranscriptionFailed and wrote the marker while Recording;
                // but under load the fault can surface so late that Stop already moved past Recording
                // and that guard was skipped. Ensure the flag + marker HERE (before the outbox is
                // completed below, so it still drains into transcript.jsonl) so a worker fault ALWAYS
                // finalizes audio-only WITH the marker (Fix #3 evidentiary guarantee) and never rethrows
                // to leave the session an un-finalized FINALIZE_FAILED husk.
                // Fix #3 (harden): the atomic TryMark writes the marker only if the mid-session
                // ContinueWith did not already claim it (exactly-once, whichever site wins the CAS).
                if (s.TryMarkTranscriptionFailed())
                    s.Outbox.Writer.TryWrite(new MarkerAt(Markers.TranscriptionFailed, s.Clock.ElapsedMs));
            }
            s.Outbox.Writer.TryComplete();
            await s.WriterLoop;
            s.FeedCts.Dispose();
            await PersistFinalAsync(s, durationMs, endedAtUtc, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The session folder + a live session.json already exist; the launch-time recovery scan
            // (SessionWriter.RecoverIfNeededAsync) will finalize it as Recovered if this never
            // completed. Surface for diagnostics, never crash the app.
            // Fix #6 (harden): a subscriber that throws from ErrorRaised/Notice must never fault the
            // background _pendingFinalize task (which StartAsync awaits and only try/catches on its own
            // path). Swallow any handler exception here - the recovery scan is the real safety net.
            try
            {
                ErrorRaised?.Invoke("FINALIZE_FAILED");
                Notice?.Invoke("Finalizing the transcript failed - the recording is safe; re-open the session to retry.");
            }
            catch { }
            _ = ex;
        }
        finally
        {
            // The tail is drained (WriterLoop completed above, so every LineInserted has fired). Stop
            // resolving View to this session - a later idle read must return empty. Only clear our own
            // reference: a new session may already have started and set _finalizing for itself.
            if (ReferenceEquals(_finalizing, s)) _finalizing = null;
            // Sessions-list live auto-update (design 2026-07-12): the row is settled on disk now
            // (success => EndedAtUtc written; failure => still un-ended). Raise AFTER the clear above
            // so FinalizingSessionId reads null in the handler. Wrap like Fix #6: a throwing
            // subscriber must never fault the unobserved _pendingFinalize task.
            try { SessionFinalizeCompleted?.Invoke(s.Id); } catch { }
        }
    }

    /// <summary>Writes the final session.json (end time, duration, segment/marker counts, resolved
    /// model/backend/language, retained audio) then regenerates the read-view projections. Extracted
    /// so the background finalizer and any future synchronous caller share one persistence path
    /// (same fields the pre-2026-07-08 inline StopAsync save wrote).</summary>
    private async Task PersistFinalAsync(Session s, long durationMs, DateTimeOffset endedAtUtc, CancellationToken ct)
    {
        await new SessionStore(_paths.SessionJson(s.Id)).SaveAsync(s.LiveRecord with
        {
            EndedAtUtc = endedAtUtc,   // Fix #1: the stop instant snapshotted in StopAsync, not the drain-completion instant
            DurationMs = durationMs,
            SegmentCount = s.Merger.View.Count(l => l.Kind == TranscriptKind.Segment),
            MarkerCount = s.Merger.View.Count(l => l.Kind == TranscriptKind.Marker),
            Model = s.LastModel.Value ?? s.Plan.ModelName,
            WeightsFile = s.LastWeightsFile.Value,   // exact file that ran (null: nothing transcribed)
            Backend = s.Plan.Backend.ToString().ToUpperInvariant(),
            Language = s.Language.Locked ?? s.Settings.Language,
            RetainedAudioSources = s.Retained,
        }, ct);
        await new SessionWriter(_paths, s.Settings, _time).RegenerateProjectionsAsync(s.Id, ct);
    }
}
