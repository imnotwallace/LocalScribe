using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
namespace LocalScribe.App;

/// <summary>Builds the app's single SessionController over the real adapters, plus the
/// SettingsService that owns the mutable Settings from here on (design 6.2). Construction
/// only - no capture, no models touched until StartAsync. Settings load synchronously at
/// startup (small local file). Task 24 completes the Stage 4 wiring (MainWindow,
/// MaintenanceService, recovery scan); here Build only swaps the raw Settings record for the
/// ISettingsService that owns it.</summary>
public static class CompositionRoot
{
    public static (SessionController Controller, ISettingsService Settings, StoragePaths Paths) Build()
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

        // SettingsService FIRST: everything downstream resolves settings through it.
        // StoragePaths deliberately snapshots the root ONCE - a storage-root change is
        // restart-required by design (6.2), never a live re-point.
        var settingsService = new SettingsService(settingsPath, loaded);
        var paths = new StoragePaths(settingsService.Current.StorageRoot);
        string appVersion = typeof(CompositionRoot).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        var controller = new SessionController(paths, () => settingsService.Current,
            new WhisperEngineFactory(),
            () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
            new LiveHardwareProbe(),
            new WasapiCaptureSourceProvider(() => settingsService.Current, new WasapiSessionScanner()),
            () => new StopwatchClock(), TimeProvider.System, appVersion);
        return (controller, settingsService, paths);
    }
}
