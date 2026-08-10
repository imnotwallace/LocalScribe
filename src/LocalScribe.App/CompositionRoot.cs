using System.IO;
using System.Reflection;
using LocalScribe.App.Services;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Retranscription;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
namespace LocalScribe.App;

/// <summary>Everything App.OnStartup and MainWindow need, built once. StoragePaths is
/// constructed exactly once from the settings loaded at startup - a storageRoot change is
/// restart-required by design (design 6.2); everything else resolves settings live via
/// ISettingsService.Current.</summary>
/// <param name="Embedding">The SAME SherpaHelperDiariser instance as <see cref="Diarisation"/>,
/// seen through its other interface (voiceprint design 2026-07-25): one helper process seam,
/// never a second engine object. Used by the Settings backfill scan's embed op.</param>
/// <param name="BuildInfo">The SECOND version string (Tier 1 plan A, 2026-08-05): the assembly's
/// InformationalVersion, e.g. "0.9.0+g1628935". Goes to the diagnostic log header, the Settings
/// About line and support copy-paste. Deliberately NOT <see cref="AppVersion"/>, which is the
/// numeric assembly version and is written into every session.json - append-only evidentiary data
/// that must stay short and stable.</param>
public sealed record AppComposition(
    SessionController Controller,
    ISettingsService Settings,
    StoragePaths Paths,
    MaintenanceService Maintenance,
    WindowRegistry Windows,
    IRecycleBin RecycleBin,
    string AppVersion,
    IDiarisationEngine Diarisation,
    IEmbeddingEngine Embedding,
    RemoteTargetOverride RemoteOverride,
    MatterSelectionOverride MatterSelection,
    MicOverride MicOverride,
    ICaptureDeviceEnumerator DeviceEnumerator,
    IAudioSessionScanner Scanner,
    RetranscriptionRunner Retranscription,
    SummaryStore Summaries,
    SummarizationService Summarizer,
    AssistantManifestCache AssistantModels,
    IAssistantJobRunner AssistantChat,
    AssistantGate AssistantGate,
    string BuildInfo,
    DiagnosticLog Log);

/// <summary>Builds the app's object graph over the real adapters. Construction only - no
/// capture, no models touched until StartAsync. Settings load synchronously at startup
/// (small local file).</summary>
public static class CompositionRoot
{
    /// <summary>Maps one capture diagnostic line onto a diagnostic LEVEL (F3, final whole-branch
    /// review, 2026-08-05). ProcessLoopbackCapture emits three genuinely different severities
    /// through ONE <c>Action&lt;string&gt;</c> event and encodes the severity in the message text;
    /// the app sink used to flatten all three to <c>info</c>, with two measured consequences:
    /// <list type="bullet">
    /// <item>DiagnosticLog.Write latches LastError only for rank 0 (<c>error</c>), so a capture
    /// FAULT could never reach Settings' "Copy last error" - at the SHIPPED DEFAULT
    /// (Logging.Level = "info"), not merely in some edge configuration. A 90-minute deposition
    /// whose per-process loopback was invalidated at minute 40 handed support a clipboard reading
    /// "No errors have been recorded since LocalScribe started."</item>
    /// <item>Write returns early when Rank(level) &gt; Rank(cfg.Level), so setting Level="warn" to
    /// reduce noise silently deleted "capture error" and "device invalidated" - the highest-value
    /// lines in the file.</item>
    /// </list>
    /// REJECTED: widening IDiagnosticSource / CaptureDiagnostics.Attach / WasapiCaptureSourceProvider
    /// to carry a level. That is a Core PUBLIC-API change, and three follow-on Tier 1 plans consume
    /// this contract; the severity vocabulary already exists as a fixed message prefix, so the sink
    /// can branch on it without touching Core at all. SherpaHelperDiariser - same round - already
    /// picks its level this way (<c>exit == 0 ? Debug : Warn</c>).
    ///
    /// This is a deliberate CROSS-FILE COUPLING on message prefixes, and it is pinned on BOTH sides:
    /// ProcessLoopbackCaptureSourceTests.Diagnostic_message_prefixes_are_the_severity_vocabulary_the_app_sink_maps
    /// pins the literals in ProcessLoopbackCapture.cs, and CompositionRootTests pins this mapping.
    /// A silent rename of either half fails a test. Both fault sites are behind the same 30-second
    /// wall-clock throttle (DiagnosticThrottleIntervalMs), so <c>error</c> here can neither flood
    /// the file nor thrash LastError.</summary>
    public static string CaptureDiagnosticLevel(string message)
    {
        // Ordinal: these are program-defined ASCII tokens, never user text or culture data.
        if (message.StartsWith("capture error", StringComparison.Ordinal)
            || message.StartsWith("device invalidated", StringComparison.Ordinal))
            return DiagnosticLevels.Error;       // MUST latch LastError - that is the whole point
        // Evidentiary (silence was inserted into a recording) so it must survive a warn-level
        // filter, but it is a QUALITY event rather than a failure and must not clobber a real
        // error - which rank 1 gets exactly right.
        if (message.StartsWith("data discontinuity", StringComparison.Ordinal))
            return DiagnosticLevels.Warn;
        return DiagnosticLevels.Info;            // "activated: ..." and anything added later
    }

    public static AppComposition Build()
    {
        string settingsPath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "LocalScribe", "settings.json");
        // Build() runs inline from App.OnStartup, i.e. on the WPF UI thread under a
        // DispatcherSynchronizationContext. Core's storage helpers await with no
        // ConfigureAwait(false), so a plain "LoadOrDefaultAsync(...).GetAwaiter().GetResult()"
        // here would deadlock whenever settings.json exists and the read completes async: the
        // continuation would try to post back to this same UI thread, which is already blocked
        // in GetResult(). Task.Run moves the whole async call onto a pool thread where
        // SynchronizationContext.Current is null, so its continuations never try to post back
        // here - GetResult() then only blocks until the pool work finishes.
        var loaded = Task.Run(() => new SettingsStore(settingsPath).LoadOrDefaultAsync(default))
            .GetAwaiter().GetResult();

        // SettingsService FIRST (Task 10's locked ctor: the settings PATH plus the loaded
        // snapshot) - everything downstream resolves settings through it.
        var settingsService = new SettingsService(settingsPath, loaded);

        // Native backend order, once per process, from the persisted setting (2026-08-11). This
        // used to be an unconditional [Cuda, Vulkan, Cpu] literal in App.OnStartup, which meant the
        // Backend picker constrained nothing: choosing "cpu" on a CUDA box recorded "cpu" while
        // whisper.cpp ran CUDA. It has to happen HERE - after settings load, before any engine -
        // because Whisper.net only honours RuntimeOptions before the first WhisperFactory and then
        // reuses the loaded library for the rest of the process. That also makes the setting
        // RESTART-REQUIRED, which the Settings page states.
        Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder =
            WhisperRuntimeOrder.For(settingsService.Current.Backend);
        var paths = new StoragePaths(settingsService.Current.StorageRoot);   // once; restart-required
        string appVersion = typeof(CompositionRoot).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        // Tier 1 plan A (2026-08-05): a SECOND version string, deliberately not folded into
        // appVersion above. Assembly.GetName().Version is the ASSEMBLY version and ignores
        // AssemblyInformationalVersionAttribute entirely - MSBuild strips any "+sha" suffix before
        // deriving it - so the two are genuinely different values. REJECTED: changing the line
        // above to read the informational version, because that string flows to
        // SessionBootstrap.cs:42 -> SessionRecord.AppVersion -> every session.json, which is
        // append-only evidentiary data that cannot be edited afterwards.
        string buildInfo = typeof(CompositionRoot).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? appVersion;
        // Diagnostic log (Tier 1 plan A): built HERE rather than in App.OnStartup so the seams
        // constructed below - the capture provider and the diarisation helper - can be handed the
        // same sink. ZERO IO in the ctor (Directory.CreateDirectory lives in the drain), which is
        // what keeps CompositionRootTests from creating folders in the developer's real
        // %USERPROFILE%\LocalScribe on every test run. The settings func is re-invoked per write:
        // SettingsService swaps the reference on save, so a captured value would pin the level.
        // This local is THE process-wide instance - it is returned as AppComposition.Log at the
        // bottom of this method, and everything outside Build() reaches it as comp.Log
        // (SHARED-CONTRACT section 3a). REJECTED: a second sink for any consumer - two logs would
        // interleave two chained drains over one file.
        var log = new DiagnosticLog(paths, TimeProvider.System, () => settingsService.Current.Logging);
        var remoteOverride = new RemoteTargetOverride();
        // Stage 6.2 Task 6: the Record console's per-session matter pick composes the same way -
        // written by the picker, read by SessionViewModel.StartAsync to seed
        // LiveSessionOptions.MatterIds, never persisted to settings.json.
        var matterSelection = new MatterSelectionOverride();
        // Stage 5.4 Phase 3: the Record console's per-session app override composes over the live
        // settings seam - SessionController and the capture provider resolve through Apply at
        // Start/Resume, so an override affects exactly the session it was set for and is never
        // persisted. Identity whenever no override is set or mode is not perProcess.
        // Device selection (design section 3): one shared enumerator backs both the capture provider
        // and the Settings/console pickers. The per-session mic override layers over the SAME live
        // settings seam as the app override; both revert on Idle and never persist to settings.json.
        var micOverride = new MicOverride();
        var deviceEnumerator = new WasapiCaptureDeviceEnumerator();
        var scanner = new WasapiSessionScanner();
        Func<Settings> current = () => micOverride.Apply(remoteOverride.Apply(settingsService.Current));

        var controller = new SessionController(paths, current, new WhisperEngineFactory(),
            () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
            new LiveHardwareProbe(),
            // Tier 1 plan A (2026-08-05): the per-process loopback's own diagnostics finally have
            // a subscriber in the app - activation fallbacks and device-invalidated recovery were
            // visible only to the SpikeRunner console harness before this. The LEVEL is chosen per
            // line by CaptureDiagnosticLevel below (F3, final whole-branch review) - a flat Info
            // meant a capture fault could never latch LastError, so Settings' "Copy last error"
            // reported "No errors have been recorded" on a run that lost the remote leg.
            new WasapiCaptureSourceProvider(current, scanner, deviceEnumerator,
                diagnostic: m => log.Write(CaptureDiagnosticLevel(m), "capture", m)),
            // Tier 1B (2026-08-05, T1-4): the SAME instance that becomes AppComposition.Log (shared
            // contract section 3a) and that Task 1 already handed to MaintenanceService - one
            // process-wide sink, one diag-yyyyMM.jsonl, one single-writer drain. REJECTED: a
            // Core-private log for the controller - two writers appending to one file is the
            // interleaved-line corruption the single-writer drain exists to prevent.
            () => new StopwatchClock(), TimeProvider.System, appVersion,
            availableModels: null, log: log);

        var recycleBin = new ShellRecycleBin();
        // Tier 1B (2026-08-05, T1-2): the ONE process-wide log - the same instance Plan A puts in
        // the AppComposition.Log member, and the same object comp.Log returns everywhere outside
        // this method (shared contract section 3a). REJECTED: a second DiagnosticLog for the
        // maintenance path - two writers appending to one diag-yyyyMM.jsonl is exactly the
        // interleaved-line corruption the single-writer drain exists to prevent.
        var maintenance = new MaintenanceService(paths, settingsService, recycleBin,
            TimeProvider.System, log);

        // Versioned re-transcription (design 2026-07-13 section 3.2): shares the controller's
        // engine-factory/VAD/probe adapters. BOTH one-engine-at-a-time directions are wired here:
        // the runner probes the live controller (forward), and the controller refuses Start
        // while the runner owns the engine (reverse, via the settable seam - the runner is
        // constructed after the controller, so a ctor param cannot express the cycle).
        var retranscription = new RetranscriptionRunner(paths, current, new WhisperEngineFactory(),
            () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
            new LiveHardwareProbe(), () => new StopwatchClock(), TimeProvider.System,
            liveEngineBusy: () => controller.State != SessionState.Idle
                ? "Cannot re-transcribe while a recording is in progress - stop the recording first."
                : !controller.PendingFinalize.IsCompleted
                    ? "The previous recording is still finalizing its transcript - try again in a moment."
                    : null,
            // F2 fix (whole-branch review): share MaintenanceService's per-session gate for the
            // runner's session.json commit, so it can never interleave with an App-side writer
            // (SetActiveVersionAsync, the diarisation Diarised flip, ...) on the same session.json.
            runUnderGate: (sid, work) => maintenance.RunForSessionAsync(sid,
                async gateCt => { await work(gateCt); return true; }, CancellationToken.None),
            // Task 7 (2026-08-11): the same process-wide log everything else here shares (see the
            // controller's own `log: log` above) - so a re-transcription's downgrade codes land in
            // the one diagnostic file, never a second writer.
            log: log);
        // rid is a SessionId (SessionId.cs: yyyy-MM-dd_HHmm_{App}_{Slug(title)}), i.e. it embeds
        // the matter/client name - mark ONLY the variable part (Tier 1 plan A fix round, same
        // shape as StartupOrchestrator's per-session failure context). SessionController.Notice
        // is now durably logged (SessionDiagnosticsRecorder), so an unmarked id here would reach
        // diag-*.jsonl verbatim at the default IncludeTranscriptText=false - the third instance of
        // a leak this plan has already been bitten by twice. SessionViewModel's Notice handler
        // strips the marker again before the string reaches the tray balloon or LastNotice, so the
        // user-visible text is unchanged either way.
        controller.ExternalEngineBusy = () => retranscription.RunningSessionId is string rid
            ? $"Cannot start recording - a re-transcription ({DiagnosticRedaction.Mark(rid)}) is still running."
            : null;

        // Diarisation engine (Stage 5, Task 9): the process-boundary seam. The helper exe is
        // resolved beside THIS app's own base directory - deliberately NOT a ProjectReference to
        // LocalScribe.Diarizer (see the long comment at the bottom of LocalScribe.App.csproj for
        // the full story, including a same-folder-copy approach that was tried and rejected after
        // it was found to corrupt Silero VAD's onnxruntime.dll): a ProjectReference would drag
        // org.k2fsa.sherpa.onnx's onnxruntime.dll into App's own dependency graph, which the
        // Stage 5 design's ORT-isolation finding (section 1.1) forbids. App never constructs a
        // sherpa type directly - only through this out-of-process helper. Until
        // LocalScribe.Diarizer.exe is actually placed here (a manual dev copy or Stage 7's
        // packaging step - see the csproj comment), this path simply does not exist yet; Split
        // speakers then surfaces a DiarisationException (HelperCrash) rather than starting. The
        // "manual dev copy" MUST be a self-contained single-file publish built with BOTH
        // -p:PublishSingleFile=true AND -p:IncludeNativeLibrariesForSelfExtract=true - the second
        // flag is required to actually bundle onnxruntime.dll/sherpa-onnx-c-api.dll inside the
        // exe; without it they extract loose beside it and a "copy the whole folder" workaround
        // reintroduces the exact ORT collision this comment describes (see the csproj comment and
        // docs/plans/2026-07-04-stage-5-smoke-runbook.md's prerequisite section for the full
        // publish command).
        string diarizerExe = Path.Combine(AppContext.BaseDirectory, "LocalScribe.Diarizer.exe");
        // Typed as the concrete class (not IDiarisationEngine) so the SAME instance can be handed
        // out as IEmbeddingEngine too - SherpaHelperDiariser implements both, and constructing a
        // second engine would mean a second helper-process seam for no reason.
        var diarisation = new SherpaHelperDiariser(new ProcessDiarisationHelper(diarizerExe), log);

        // Local assistant (design 2026-07-18 section 7; deployment revised 2026-07-23): an
        // out-of-process LLamaSharp helper published as a FOLDER into an assistant\ subfolder -
        // deliberately NOT single-file like Diarizer, because LLamaSharp probes its
        // runtimes/<rid>/native/<variant>/ layout relative to the helper's own directory
        // (single-file self-extract lands the natives where that probe never looks; every
        // request then failed at NativeApi init, which is how the first deployment shipped
        // broken). The subfolder keeps the helper's own onnxruntime.dll isolated from the
        // App's - the same isolation goal as Diarizer's single-file rule, reached by a
        // different means. Resolution via AssistantHelperLocator (env override -> assistant\
        // subfolder -> repo tools\assistant dev fallback); when absent the UI disables the
        // assistant with the locator's MissingMessage (availability = model AND helper) and
        // this fallback path simply fails visibly if a job is somehow still attempted.
        // AssistantGate probes the SAME recording-busy condition RetranscriptionRunner uses
        // (above): assistant jobs yield to recording, visibly queued; recording is NEVER
        // gated by the assistant.
        string assistantExe = AssistantHelperLocator.FindExe()
            ?? Path.Combine(AppContext.BaseDirectory, "assistant", AssistantHelperLocator.ExeName);
        var assistantProcs = new ProcessAssistantHelper(assistantExe);
        var assistantModels = new AssistantManifestCache(
            ct => Task.Run(() => AssistantModelManifest.LoadAsync(ModelPaths.ModelsRoot, ct), ct));
        var summaries = new SummaryStore(paths);
        var assistantGate = new AssistantGate(() => controller.State != SessionState.Idle
            ? "Waiting for the recording to finish before running the assistant..."
            : !controller.PendingFinalize.IsCompleted
                ? "Waiting for the previous recording to finish finalizing..."
                : null);
        var summarizer = new SummarizationService(paths, current, TimeProvider.System,
            new AssistantJobRunner(assistantProcs), summaries, assistantGate, assistantModels);
        // Reverse direction of "one heavy engine at a time" (design 7.1): a recording START cancels any in-flight
        // assistant job so it yields the engine to live transcription. CancelForRecording is non-blocking + off-thread,
        // safe from StateChanged (a controller worker-thread event that must not be blocked or re-entered). The cancelled
        // job throws before persisting - nothing is saved.
        controller.StateChanged += s => { if (s != SessionState.Idle) summarizer.CancelForRecording(); };
        var assistantChat = new AssistantJobRunner(assistantProcs);   // spawn-per-job chat (Fix A 2026-08-01): each ask a fresh helper, 1x KV

        return new AppComposition(controller, settingsService, paths, maintenance,
            new WindowRegistry(), recycleBin, appVersion, diarisation, diarisation, remoteOverride, matterSelection,
            micOverride, deviceEnumerator, scanner, retranscription,
            summaries, summarizer, assistantModels, assistantChat, assistantGate, buildInfo, log);
    }
}
