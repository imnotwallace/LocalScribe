using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
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
    string AppVersion);

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
        Func<Settings> current = () => settingsService.Current;              // Task 10 seam

        var controller = new SessionController(paths, current, new WhisperEngineFactory(),
            () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
            new LiveHardwareProbe(),
            new WasapiCaptureSourceProvider(current, new WasapiSessionScanner()),
            () => new StopwatchClock(), TimeProvider.System, appVersion);

        var recycleBin = new ShellRecycleBin();
        var maintenance = new MaintenanceService(paths, settingsService, recycleBin, TimeProvider.System);
        return new AppComposition(controller, settingsService, paths, maintenance,
            new WindowRegistry(), recycleBin, appVersion);
    }
}
