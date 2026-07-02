namespace LocalScribe.Core.Transcription;

/// <summary>Probe result feeding the spec section 3 selection table. The live probe is a Stage 3
/// adapter; 2b uses StaticHardwareProbe (config/CLI-driven).</summary>
public sealed record HardwareInfo(bool HasCuda, int CudaVramMb, bool HasVulkan, int FastCores);

public interface IHardwareProbe { HardwareInfo Probe(); }

public sealed class StaticHardwareProbe(HardwareInfo info) : IHardwareProbe
{
    public HardwareInfo Probe() => info;
}
