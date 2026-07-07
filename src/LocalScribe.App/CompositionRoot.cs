using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
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
    RemoteAppOverride RemoteOverride,
    MatterSelectionOverride MatterSelection);

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
        var remoteOverride = new RemoteAppOverride();
        // Stage 6.2 Task 6: the Record console's per-session matter pick composes the same way -
        // written by the picker, read by SessionViewModel.StartAsync to seed
        // LiveSessionOptions.MatterIds, never persisted to settings.json.
        var matterSelection = new MatterSelectionOverride();
        // Stage 5.4 Phase 3: the Record console's per-session app override composes over the live
        // settings seam - SessionController and the capture provider resolve through Apply at
        // Start/Resume, so an override affects exactly the session it was set for and is never
        // persisted. Identity whenever no override is set or mode is not perProcess.
        Func<Settings> current = () => remoteOverride.Apply(settingsService.Current);

        var controller = new SessionController(paths, current, new WhisperEngineFactory(),
            () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
            new LiveHardwareProbe(),
            new WasapiCaptureSourceProvider(current, new WasapiSessionScanner()),
            () => new StopwatchClock(), TimeProvider.System, appVersion);

        var recycleBin = new ShellRecycleBin();
        var maintenance = new MaintenanceService(paths, settingsService, recycleBin, TimeProvider.System);

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

        return new AppComposition(controller, settingsService, paths, maintenance,
            new WindowRegistry(), recycleBin, appVersion, diarisation, remoteOverride, matterSelection);
    }
}
