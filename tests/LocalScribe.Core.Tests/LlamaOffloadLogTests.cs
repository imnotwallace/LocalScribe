using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

public sealed class LlamaOffloadLogTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void Real_cuda_full_offload_log_parses_37_of_37()
    {
        var offload = LlamaOffloadLog.FindOffload(Fixture("llamacpp-log-cuda-full-offload.txt"));
        Assert.Equal((37, 37), offload);
        Assert.True(LlamaOffloadLog.IsFullGpu(offload));
    }

    [Fact]
    public void Real_cpu_log_has_no_offload_line()
    {
        var offload = LlamaOffloadLog.FindOffload(Fixture("llamacpp-log-cpu-no-offload.txt"));
        Assert.Null(offload);
        Assert.False(LlamaOffloadLog.IsFullGpu(offload));
    }

    [Theory]
    [InlineData("load_tensors: offloaded 22/37 layers to GPU", 22, 37)]
    [InlineData("prefix noise load_tensors: offloaded 1/1 layers to GPU suffix", 1, 1)]
    public void Parses_the_offload_line_anywhere_in_the_text(string text, int offloaded, int total)
        => Assert.Equal((offloaded, total), LlamaOffloadLog.FindOffload(text));

    [Fact]
    public void Partial_offload_is_not_a_gpu_run()
        => Assert.False(LlamaOffloadLog.IsFullGpu((22, 37)));   // design section 5: mixed != GPU

    [Fact]
    public void Zero_total_is_not_a_gpu_run()
        => Assert.False(LlamaOffloadLog.IsFullGpu((0, 0)));     // total > 0 required

    [Fact]
    public void Last_offload_line_wins_when_repeated()
        => Assert.Equal((37, 37), LlamaOffloadLog.FindOffload(
            "load_tensors: offloaded 22/37 layers to GPU\nload_tensors: offloaded 37/37 layers to GPU"));

    [Fact]
    public void Fell_phase_constant_is_the_wire_literal()
        => Assert.Equal("cuda-fell-to-cpu", AssistantWire.CudaFellPhase);
}
