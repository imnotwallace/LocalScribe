// src/LocalScribe.OfflineRunner/Program.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using LocalScribe.Core.Vad;

static string? Arg(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string? local = Arg(args, "--local");
string? remote = Arg(args, "--remote");
if (local is null && remote is null)
{
    Console.Error.WriteLine("usage: LocalScribe.OfflineRunner --local <wav> [--remote <wav>] " +
        "[--out <storageRoot>] [--model <name>] [--backend auto|cuda|vulkan|cpu] [--vram <mb>] [--cores <n>]");
    return 2;
}

// Native backend preference: CUDA -> Vulkan -> CPU (spec 3 cascade at the whisper.cpp level).
// Whisper.net probes this order and falls through automatically when a runtime cannot load.
Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder =
[
    Whisper.net.LibraryLoader.RuntimeLibrary.Cuda,
    Whisper.net.LibraryLoader.RuntimeLibrary.Vulkan,
    Whisper.net.LibraryLoader.RuntimeLibrary.Cpu,
];

var settingsStore = new SettingsStore(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalScribe", "settings.json"));
var settings = await settingsStore.LoadOrDefaultAsync(default);
if (Arg(args, "--out") is { } outRoot) settings = settings with { StorageRoot = outRoot };
if (Arg(args, "--model") is { } model) settings = settings with { Model = model };
if (Arg(args, "--backend") is { } backend)
    settings = settings with { Backend = Enum.Parse<Backend>(backend, ignoreCase: true) };

var hardware = new StaticHardwareProbe(new HardwareInfo(
    HasCuda: int.TryParse(Arg(args, "--vram"), out int vram) && vram > 0,
    CudaVramMb: vram,
    HasVulkan: false,
    FastCores: int.TryParse(Arg(args, "--cores"), out int cores) ? cores : Environment.ProcessorCount / 2));

var runner = new OfflinePipelineRunner(
    new StoragePaths(settings.StorageRoot), settings,
    new WhisperEngineFactory(),
    () => new SileroVadModel(ModelPaths.Require("silero_vad.onnx")),
    hardware, new StopwatchClock(), TimeProvider.System,
    appVersion: typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

string id = await runner.RunAsync(new OfflineRunOptions { LocalWavPath = local, RemoteWavPath = remote }, default);
var paths = new StoragePaths(settings.StorageRoot);
Console.WriteLine($"session: {id}");
Console.WriteLine($"folder:  {paths.SessionDir(id)}");
Console.WriteLine($"read:    {paths.TranscriptMd(id)}");
return 0;
