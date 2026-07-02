using LocalScribe.Core.Transcription;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class LiveHardwareProbeTests
{
    [Theory]
    [InlineData("4096", 4096)]
    [InlineData("4096\n", 4096)]
    [InlineData("24576\n24576\n", 24576)]     // multi-GPU: first line wins
    [InlineData(" 8192 ", 8192)]
    public void ParseVramMb_parses_nvidia_smi_output(string output, int expected)
        => Assert.Equal(expected, NvidiaSmi.ParseVramMb(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NVIDIA-SMI has failed")]
    [InlineData("not a number")]
    public void ParseVramMb_returns_null_on_garbage(string? output)
        => Assert.Null(NvidiaSmi.ParseVramMb(output));

    [Fact]
    public void Probe_with_cuda_and_vulkan()
    {
        var probe = new LiveHardwareProbe(() => "4096\n", () => true, processorCount: 12);
        var hw = probe.Probe();
        Assert.True(hw.HasCuda);
        Assert.Equal(4096, hw.CudaVramMb);
        Assert.True(hw.HasVulkan);
        Assert.Equal(6, hw.FastCores);
    }

    [Fact]
    public void Probe_without_nvidia_smi_reports_no_cuda()
    {
        var probe = new LiveHardwareProbe(() => null, () => false, processorCount: 8);
        var hw = probe.Probe();
        Assert.False(hw.HasCuda);
        Assert.Equal(0, hw.CudaVramMb);
        Assert.False(hw.HasVulkan);
        Assert.Equal(4, hw.FastCores);
    }

    [Fact]
    public void Probe_caches_and_runs_detection_once()
    {
        int calls = 0;
        var probe = new LiveHardwareProbe(() => { calls++; return "2048"; }, () => true, 4);
        probe.Probe();
        probe.Probe();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void FastCores_is_at_least_one()
    {
        var probe = new LiveHardwareProbe(() => null, () => false, processorCount: 1);
        Assert.Equal(1, probe.Probe().FastCores);
    }
}
