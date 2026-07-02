using System.IO;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
namespace LocalScribe.App;

/// <summary>Builds the app's single SessionController over the real adapters. Construction
/// only - no capture, no models touched until StartAsync. Settings load synchronously at
/// startup (small local file).</summary>
public static class CompositionRoot
{
    public static (SessionController Controller, Settings Settings, StoragePaths Paths) Build()
    {
        string settingsPath = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "LocalScribe", "settings.json");
        var settings = new SettingsStore(settingsPath).LoadOrDefaultAsync(default)
            .GetAwaiter().GetResult();
        var paths = new StoragePaths(settings.StorageRoot);
        string appVersion = typeof(CompositionRoot).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        var controller = new SessionController(paths, settings, new WhisperEngineFactory(),
            () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
            new LiveHardwareProbe(),
            new WasapiCaptureSourceProvider(settings, new WasapiSessionScanner()),
            () => new StopwatchClock(), TimeProvider.System, appVersion);
        return (controller, settings, paths);
    }
}
