using LocalScribe.Core.Model;
using LocalScribe.Core.Transcription;

public class BackendSelectorTests
{
    private static Settings S(Backend backend = Backend.Auto, string model = "auto", string language = "auto")
        => new() { Backend = backend, Model = model, Language = language };

    private static IReadOnlySet<string> Present(params string[] m) => new HashSet<string>(m, StringComparer.Ordinal);

    [Fact]
    public void Big_nvidia_gets_cuda_small_en()
    {
        var (plan, _) = BackendSelector.Select(new HardwareInfo(true, 12000, true, 16), S(),
            Present("small.en", "base.en", "tiny.en"));
        Assert.Equal(Backend.Cuda, plan.Backend);
        Assert.Equal("small.en", plan.ModelName);
    }

    [Fact]
    public void Mid_nvidia_4_to_6_gb_gets_cuda_small_en()
    {
        var (plan, _) = BackendSelector.Select(new HardwareInfo(true, 4096, false, 8), S(),
            Present("small.en", "base.en", "tiny.en"));
        Assert.Equal(Backend.Cuda, plan.Backend);
        Assert.Equal("small.en", plan.ModelName);
    }

    [Fact]
    public void Igpu_gets_vulkan_base_en()
    {
        var (plan, _) = BackendSelector.Select(new HardwareInfo(false, 0, true, 8), S(),
            Present("small.en", "base.en", "tiny.en"));
        Assert.Equal(Backend.Vulkan, plan.Backend);
        Assert.Equal("base.en", plan.ModelName);
    }

    [Theory]
    [InlineData(4, "base.en")]
    [InlineData(8, "small.en")]
    public void Cpu_model_scales_with_fast_cores(int cores, string expected)
    {
        var (plan, _) = BackendSelector.Select(new HardwareInfo(false, 0, false, cores), S(),
            Present("small.en", "base.en", "tiny.en"));
        Assert.Equal(Backend.Cpu, plan.Backend);
        Assert.Equal(expected, plan.ModelName);
    }

    [Fact]
    public void Explicit_user_overrides_always_win()
    {
        var (plan, _) = BackendSelector.Select(new HardwareInfo(true, 12000, true, 16),
            S(backend: Backend.Cpu, model: "tiny.en"), Present("small.en", "base.en", "tiny.en"));
        Assert.Equal(Backend.Cpu, plan.Backend);
        Assert.Equal("tiny.en", plan.ModelName);
    }

    [Fact]
    public void Non_english_language_gets_multilingual_weights()
    {
        var (plan, _) = BackendSelector.Select(new HardwareInfo(true, 12000, false, 8), S(language: "de"),
            Present("small.en", "base.en", "tiny.en"));
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

    [Fact]
    public void Auto_downgrades_to_best_present_below_ceiling()
    {
        // CUDA ceiling is small.en, but only base.en/tiny.en are present.
        var (plan, downgradedFrom) = BackendSelector.Select(
            new HardwareInfo(true, 4096, false, 8), S(), Present("base.en", "tiny.en"));
        Assert.Equal("base.en", plan.ModelName);
        Assert.Equal("small.en", downgradedFrom);
    }

    [Fact]
    public void Auto_uses_ceiling_when_present_no_downgrade()
    {
        var (plan, downgradedFrom) = BackendSelector.Select(
            new HardwareInfo(true, 4096, false, 8), S(), Present("small.en", "base.en"));
        Assert.Equal("small.en", plan.ModelName);
        Assert.Null(downgradedFrom);
    }

    [Fact]
    public void Explicit_pick_is_returned_verbatim_even_if_absent()
    {
        var (plan, downgradedFrom) = BackendSelector.Select(
            new HardwareInfo(true, 4096, false, 8), S(model: "small.en"), Present("base.en"));
        Assert.Equal("small.en", plan.ModelName);   // Start validates presence, not Select
        Assert.Null(downgradedFrom);
    }

    [Fact]
    public void Auto_with_no_present_models_returns_ceiling_for_start_to_refuse()
    {
        var (plan, _) = BackendSelector.Select(
            new HardwareInfo(true, 4096, false, 8), S(), Present());
        Assert.Equal("small.en", plan.ModelName);   // absent -> Start's fail-fast handles it (Task 3)
    }
}
