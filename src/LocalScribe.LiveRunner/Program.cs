// src/LocalScribe.LiveRunner/Program.cs
//
// Stage 3a manual smoke harness: records a REAL meeting live through the full pipeline.
// MTA main thread (console default, no [STAThread]) - required by ProcessLoopbackCapture.
//
// Keys:  R = start   P = pause/resume   S = stop (finalize)   Q = quit
// Flags: --model <name>  --backend <auto|cuda|vulkan|cpu>  --vram <mb>  --no-preflight
//        --app <image>   (explicit perProcess target, e.g. CiscoCollabHost)
//        --system-mix    (force full-system EXCLUDE-self remote capture)
//        --auto <seconds>  (headless smoke affordance: start immediately, record N seconds,
//                           stop, and exit - no console keys required. Prints the same
//                           StateChanged/Notice/ErrorRaised/LineInserted stream as interactive
//                           mode so a smoke run can be captured to a log file.)

using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;
using Whisper.net.LibraryLoader;

// Host responsibility (see OfflineRunner): set the native backend order once.
RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

var settingsPath = Path.Combine(Environment.GetFolderPath(
    Environment.SpecialFolder.ApplicationData), "LocalScribe", "settings.json");
var settings = await new SettingsStore(settingsPath).LoadOrDefaultAsync(default);
if (Arg("--model") is { } model) settings = settings with { Model = model };
if (Arg("--backend") is { } backend)
    settings = settings with { Backend = Enum.Parse<Backend>(backend, ignoreCase: true) };
if (args.Contains("--system-mix"))
    settings = settings with { Remote = settings.Remote with { Mode = RemoteMode.SystemMix } };
else if (Arg("--app") is { } app)
    settings = settings with { Remote = new RemoteSetting { Mode = RemoteMode.PerProcess, App = app } };

IHardwareProbe hardware = Arg("--vram") is { } vram && int.TryParse(vram, out int mb)
    ? new StaticHardwareProbe(new HardwareInfo(mb > 0, mb, false, Environment.ProcessorCount / 2))
    : new LiveHardwareProbe();
var hw = hardware.Probe();
Console.WriteLine($"Hardware: cuda={hw.HasCuda} vram={hw.CudaVramMb}MB vulkan={hw.HasVulkan} fastCores={hw.FastCores}");
Console.WriteLine($"Backend plan: {BackendSelector.Select(hw, settings, ModelPaths.AvailableModels()).Plan}");

string appVersion = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
var controller = new SessionController(
    new StoragePaths(settings.StorageRoot), settings, new WhisperEngineFactory(),
    () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
    hardware, new WasapiCaptureSourceProvider(settings, new WasapiSessionScanner()),
    () => new StopwatchClock(), TimeProvider.System, appVersion);

static string Ts(long ms) => TimeSpan.FromMilliseconds(ms).ToString(
    ms >= 3_600_000 ? @"h\:mm\:ss" : @"mm\:ss", System.Globalization.CultureInfo.InvariantCulture);

controller.StateChanged += s => Console.WriteLine($"-- state: {s}");
controller.Notice += n => Console.WriteLine($"-- notice: {n}");
controller.ErrorRaised += e => Console.WriteLine($"-- error: {e}");
controller.LineInserted += (_, line) => Console.WriteLine(
    line.Kind == TranscriptKind.Marker
        ? $"  [{Ts(line.StartMs)}] _[{line.Text}]_"
        : $"  [{Ts(line.StartMs)}] {line.SpeakerLabel}: {line.Text}");

var options = new LiveSessionOptions
{ App = AppKind.Webex, RunPreflightProbe = !args.Contains("--no-preflight") };

// --auto <seconds>: headless smoke path (no console keys). Start, wait, stop, exit.
if (Arg("--auto") is { } autoSeconds && double.TryParse(autoSeconds, out double seconds))
{
    try
    {
        string? autoId = await controller.StartAsync(options, default);
        if (autoId is null)
        {
            Console.WriteLine("FAULT: StartAsync returned null (see notices above).");
            return 1;
        }
        Console.WriteLine($"recording -> {autoId}");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        string? autoStopped = await controller.StopAsync(default);
        if (autoStopped is not null)
            Console.WriteLine($"finalized -> {new StoragePaths(settings.StorageRoot).SessionDir(autoStopped)}");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAULT: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        return 1;
    }
}

Console.WriteLine("R = start, P = pause/resume, S = stop, Q = quit");
while (true)
{
    var key = Console.ReadKey(intercept: true).Key;
    try
    {
        switch (key)
        {
            case ConsoleKey.R:
                string? id = await controller.StartAsync(options, default);
                if (id is not null) Console.WriteLine($"recording -> {id}");
                break;
            case ConsoleKey.P:
                if (controller.State == SessionState.Paused) await controller.ResumeAsync(default);
                else await controller.PauseAsync(default);
                break;
            case ConsoleKey.S:
                string? stopped = await controller.StopAsync(default);
                if (stopped is not null)
                    Console.WriteLine($"finalized -> {new StoragePaths(settings.StorageRoot).SessionDir(stopped)}");
                break;
            case ConsoleKey.Q:
                if (controller.State is SessionState.Recording or SessionState.Paused)
                    await controller.StopAsync(default);
                return 0;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAULT: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
    }
}
