using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Audio;
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
public sealed record AppComposition(
    SessionController Controller,
    ISettingsService Settings,
    StoragePaths Paths,
    MaintenanceService Maintenance,
    WindowRegistry Windows,
    IRecycleBin RecycleBin,
    string AppVersion,
    IDiarisationEngine Diarisation,
    RemoteTargetOverride RemoteOverride,
    MatterSelectionOverride MatterSelection,
    MicOverride MicOverride,
    ICaptureDeviceEnumerator DeviceEnumerator,
    IAudioSessionScanner Scanner,
    RetranscriptionRunner Retranscription,
    SummaryStore Summaries,
    SummarizationService Summarizer,
    AssistantManifestCache AssistantModels,
    IAssistantChatSessionFactory AssistantChat);

/// <summary>Builds the app's object graph over the real adapters. Construction only - no
/// capture, no models touched until StartAsync. Settings load synchronously at startup
/// (small local file).</summary>
public static class CompositionRoot
{
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
        var paths = new StoragePaths(settingsService.Current.StorageRoot);   // once; restart-required
        string appVersion = typeof(CompositionRoot).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
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
            new WasapiCaptureSourceProvider(current, scanner, deviceEnumerator),
            () => new StopwatchClock(), TimeProvider.System, appVersion);

        var recycleBin = new ShellRecycleBin();
        var maintenance = new MaintenanceService(paths, settingsService, recycleBin, TimeProvider.System);

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
                async gateCt => { await work(gateCt); return true; }, CancellationToken.None));
        controller.ExternalEngineBusy = () => retranscription.RunningSessionId is string rid
            ? $"Cannot start recording - a re-transcription ({rid}) is still running."
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
        IDiarisationEngine diarisation = new SherpaHelperDiariser(new ProcessDiarisationHelper(diarizerExe));

        // Local assistant (design 2026-07-18 section 7): out-of-process LLamaSharp helper,
        // resolved beside the app exactly like Diarizer - no ProjectReference, no auto-copy
        // (native-DLL isolation, see the csproj comment). AssistantGate probes the SAME
        // recording-busy condition RetranscriptionRunner uses (above): assistant jobs yield
        // to recording, visibly queued; recording is NEVER gated by the assistant.
        string assistantExe = Path.Combine(AppContext.BaseDirectory, "LocalScribe.Assistant.exe");
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
        var assistantChat = new AssistantChatSessionFactory(assistantProcs);   // consumed by feat/matter-qa

        return new AppComposition(controller, settingsService, paths, maintenance,
            new WindowRegistry(), recycleBin, appVersion, diarisation, remoteOverride, matterSelection,
            micOverride, deviceEnumerator, scanner, retranscription,
            summaries, summarizer, assistantModels, assistantChat);
    }
}
