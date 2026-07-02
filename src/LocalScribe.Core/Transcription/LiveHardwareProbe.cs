using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
namespace LocalScribe.Core.Transcription;

/// <summary>Pure parser for `nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits`
/// output: one integer (MiB) per GPU line; first GPU wins.</summary>
public static class NvidiaSmi
{
    public static int? ParseVramMb(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        string first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries |
                                          StringSplitOptions.TrimEntries)[0];
        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mb)
            ? mb : null;
    }
}

/// <summary>Live hardware probe (the Stage-3 adapter behind IHardwareProbe): CUDA via
/// nvidia-smi (ships with every NVIDIA driver), Vulkan via loader presence (a lying loader is
/// caught downstream by the BACKEND_INIT_FAILED cascade - spec 8.2), fast cores via the
/// ProcessorCount/2 physical-core heuristic. Result cached: detection shells out.</summary>
public sealed class LiveHardwareProbe : IHardwareProbe
{
    private readonly Func<string?> _nvidiaSmi;
    private readonly Func<bool> _vulkanPresent;
    private readonly int _processorCount;
    private HardwareInfo? _cached;

    public LiveHardwareProbe()
        : this(RunNvidiaSmi, VulkanLoaderPresent, Environment.ProcessorCount) { }

    public LiveHardwareProbe(Func<string?> nvidiaSmi, Func<bool> vulkanPresent, int processorCount)
        => (_nvidiaSmi, _vulkanPresent, _processorCount) = (nvidiaSmi, vulkanPresent, processorCount);

    public HardwareInfo Probe()
    {
        if (_cached is not null) return _cached;
        int? vram = NvidiaSmi.ParseVramMb(_nvidiaSmi());
        _cached = new HardwareInfo(
            HasCuda: vram is > 0,
            CudaVramMb: vram ?? 0,
            HasVulkan: _vulkanPresent(),
            FastCores: Math.Max(1, _processorCount / 2));
        return _cached;
    }

    private static string? RunNvidiaSmi()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;                      // nvidia-smi absent = no NVIDIA driver = no CUDA
        }
    }

    private static bool VulkanLoaderPresent()
    {
        if (!NativeLibrary.TryLoad("vulkan-1.dll", out nint handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }
}
