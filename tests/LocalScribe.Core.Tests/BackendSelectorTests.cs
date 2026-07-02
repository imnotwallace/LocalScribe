using LocalScribe.Core.Model;
using LocalScribe.Core.Transcription;

public class BackendSelectorTests
{
    private static Settings S(Backend backend = Backend.Auto, string model = "auto", string language = "auto")
        => new() { Backend = backend, Model = model, Language = language };

    [Fact]
    public void Big_nvidia_gets_cuda_small_en()
    {
        var plan = BackendSelector.Select(new HardwareInfo(true, 12000, true, 16), S());
        Assert.Equal(Backend.Cuda, plan.Backend);
        Assert.Equal("small.en", plan.ModelName);
    }

    [Fact]
    public void Mid_nvidia_4_to_6_gb_gets_cuda_small_en()
    {
        var plan = BackendSelector.Select(new HardwareInfo(true, 4096, false, 8), S());
        Assert.Equal(Backend.Cuda, plan.Backend);
        Assert.Equal("small.en", plan.ModelName);
    }

    [Fact]
    public void Igpu_gets_vulkan_base_en()
    {
        var plan = BackendSelector.Select(new HardwareInfo(false, 0, true, 8), S());
        Assert.Equal(Backend.Vulkan, plan.Backend);
        Assert.Equal("base.en", plan.ModelName);
    }

    [Theory]
    [InlineData(4, "base.en")]
    [InlineData(8, "small.en")]
    public void Cpu_model_scales_with_fast_cores(int cores, string expected)
    {
        var plan = BackendSelector.Select(new HardwareInfo(false, 0, false, cores), S());
        Assert.Equal(Backend.Cpu, plan.Backend);
        Assert.Equal(expected, plan.ModelName);
    }

    [Fact]
    public void Explicit_user_overrides_always_win()
    {
        var plan = BackendSelector.Select(new HardwareInfo(true, 12000, true, 16),
            S(backend: Backend.Cpu, model: "tiny.en"));
        Assert.Equal(Backend.Cpu, plan.Backend);
        Assert.Equal("tiny.en", plan.ModelName);
    }

    [Fact]
    public void Non_english_language_gets_multilingual_weights()
    {
        var plan = BackendSelector.Select(new HardwareInfo(true, 12000, false, 8), S(language: "de"));
        Assert.Equal("small", plan.ModelName);         // no .en suffix (spec 3)
    }

    [Theory]
    [InlineData("small.en", "base.en")]
    [InlineData("base.en", "tiny.en")]
    [InlineData("tiny.en", null)]
    [InlineData("large-v3", "medium")]
    [InlineData("unknown-model", null)]
    public void Ladder_steps_down_and_stops_at_floor(string from, string? expected)
        => Assert.Equal(expected, ModelLadder.Downgrade(from));
}
