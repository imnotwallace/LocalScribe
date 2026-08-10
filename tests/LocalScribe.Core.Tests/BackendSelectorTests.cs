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
        => Assert.Equal(expected, ModelLadder.Downgrade(from, _ => true));

    [Fact]
    public void Downgrade_skips_rungs_whose_weights_are_not_on_disk()
    {
        // Only "small" is installed: turbo must step straight past large-v3 and medium.
        Assert.Equal("small", ModelLadder.Downgrade("large-v3-turbo", m => m == "small"));
    }

    [Fact]
    public void Downgrade_returns_null_when_no_lower_rung_is_installed()
    {
        // The live 2026-08-11 case: large-v3-turbo is the only model on disk. Null means
        // "at the floor" and the worker falls to CPU on the SAME weights, which works.
        Assert.Null(ModelLadder.Downgrade("large-v3-turbo", m => m == "large-v3-turbo"));
    }

    [Fact]
    public void Downgrade_preserves_the_en_suffix_and_will_not_cross_to_multilingual_weights()
    {
        // A bundled base.en must NOT satisfy a multilingual walk: switching a multilingual run
        // onto English-only weights mid-session is the language-lock fix-up's decision, not the
        // ladder's. "base" is absent, so the walk continues past it.
        Assert.Null(ModelLadder.Downgrade("medium", m => m == "base.en"));
        Assert.Equal("base.en", ModelLadder.Downgrade("medium.en", m => m == "base.en"));
    }

    [Fact]
    public void Downgrade_returns_null_for_an_unknown_model_name()
        => Assert.Null(ModelLadder.Downgrade("not-a-model", _ => true));

    [Fact]
    public void IsAvailable_accepts_a_quantized_only_disk()
    {
        // A q8_0-only disk must read as available: quantization is a per-backend file detail,
        // not a different model (ModelFileResolver.cs:11-16).
        Assert.True(ModelFileResolver.IsAvailable(Backend.Cpu, "small.en",
            f => f == "ggml-small.en-q8_0.bin"));
        Assert.False(ModelFileResolver.IsAvailable(Backend.Cpu, "small.en", _ => false));
    }

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
    public void Explicit_quantized_name_canonicalizes_so_the_start_gate_matches_disk()
    {
        // Review finding (2026-07-13): AvailableModels canonicalizes quantized files, so a
        // persisted/hand-edited Model="small.en-q8_0" compared verbatim would be refused at
        // Start with a false "not downloaded" even though its file is on disk. Select maps
        // the name to canonical; ModelFileResolver then picks the best FILE per backend.
        var (plan, _) = BackendSelector.Select(new HardwareInfo(false, 0, false, 8),
            S(model: "small.en-q8_0"), Present("small.en"));
        Assert.Equal("small.en", plan.ModelName);
    }

    [Fact]
    public void Explicit_unknown_suffix_name_stays_verbatim()
    {
        // Unknown quant styles are not canonicalized anywhere (AvailableModels keeps the raw
        // name too), so the raw-name path stays consistent end to end and loads verbatim.
        var (plan, _) = BackendSelector.Select(new HardwareInfo(false, 0, false, 8),
            S(model: "small.en-q9_9"), Present("small.en-q9_9"));
        Assert.Equal("small.en-q9_9", plan.ModelName);
    }

    [Fact]
    public void Auto_with_no_present_models_returns_ceiling_for_start_to_refuse()
    {
        var (plan, _) = BackendSelector.Select(
            new HardwareInfo(true, 4096, false, 8), S(), Present());
        Assert.Equal("small.en", plan.ModelName);   // absent -> Start's fail-fast handles it (Task 3)
    }

    // ---------- CPU thread planning ----------
    // max(whisper.cpp's own default min(4, logical~=2*fastCores), fastCores - 2), floor 2, cap 8.
    // Review finding (2026-07-13): a bare fastCores-2 DROPPED BELOW the whisper.cpp default on
    // <=5-fastCore machines (quad-core laptop: 2 threads vs the 4 master ran) - the auto value
    // must never be slower than the default it exists to beat. Headroom (cores-2) engages on
    // 8+ core machines; cap 8 because whisper.cpp is memory-bandwidth bound past that.

    [Theory]
    [InlineData(1, 2)]    // 2 logical: whisper default min(4,2)=2 - parity, floor holds
    [InlineData(2, 4)]    // 4 logical: parity with whisper default 4
    [InlineData(4, 4)]    // 8 logical: parity (a bare cores-2 would regress to 2)
    [InlineData(5, 4)]    // parity
    [InlineData(6, 4)]    // cores-2 meets the default
    [InlineData(7, 5)]    // headroom rule takes over
    [InlineData(8, 6)]
    [InlineData(10, 8)]   // cap: never above 8
    [InlineData(16, 8)]
    public void Auto_cpu_threads_never_drops_below_whisper_default_and_caps_at_8(int fastCores, int expected)
        => Assert.Equal(expected, BackendSelector.AutoCpuThreads(fastCores));

    [Fact]
    public void Cpu_plan_activates_auto_threads()
    {
        var (plan, _) = BackendSelector.Select(new HardwareInfo(false, 0, false, 8), S(),
            Present("small.en", "base.en", "tiny.en"));
        Assert.Equal(Backend.Cpu, plan.Backend);
        Assert.Equal(6, plan.CpuThreads);
        Assert.Equal(6, plan.EffectiveThreads);
    }

    [Fact]
    public void Gpu_plan_carries_cpu_threads_dormant()
    {
        // Threads ride along on every plan but only take effect on the CPU backend, so the
        // engine keeps whisper.cpp defaults on CUDA/Vulkan.
        var (plan, _) = BackendSelector.Select(new HardwareInfo(true, 12000, true, 16), S(),
            Present("small.en", "base.en", "tiny.en"));
        Assert.Equal(Backend.Cuda, plan.Backend);
        Assert.Equal(8, plan.CpuThreads);
        Assert.Null(plan.EffectiveThreads);
    }

    [Fact]
    public void Falling_to_the_cpu_floor_activates_the_carried_threads()
    {
        // TranscriptionWorker's VRAM-OOM floor fall does `plan with { Backend = Cpu }`
        // (never re-runs Select), so the carried value must activate by construction.
        var (plan, _) = BackendSelector.Select(new HardwareInfo(true, 12000, false, 8), S(),
            Present("small.en", "base.en", "tiny.en"));
        Assert.Null(plan.EffectiveThreads);
        var floored = plan with { Backend = Backend.Cpu };
        Assert.Equal(6, floored.EffectiveThreads);
    }

    [Fact]
    public void Explicit_cpu_backend_override_also_gets_auto_threads()
    {
        var (plan, _) = BackendSelector.Select(new HardwareInfo(true, 12000, true, 16),
            S(backend: Backend.Cpu), Present("small.en", "base.en", "tiny.en"));
        Assert.Equal(Backend.Cpu, plan.Backend);
        Assert.Equal(8, plan.EffectiveThreads);
    }

    [Theory]
    [InlineData("large-v3-turbo", "large-v3")]
    [InlineData("large-v3", "medium")]
    [InlineData("medium", "small")]
    [InlineData("small", "base")]
    [InlineData("base", "tiny")]
    [InlineData("tiny", null)]
    public void Turbo_is_the_top_rung_so_an_explicit_turbo_pick_can_still_step_down(
        string from, string? expected)
    {
        // Tier 1 T1-6 (spec 2026-08-05 :78-81): with turbo absent from Rungs, Downgrade returned
        // null and DowngradeAsync's null branch only flipped Backend to Cpu - so an explicit
        // large-v3-turbo pick that VRAM-OOMed on CUDA fell straight to CPU with no ladder step,
        // and a floor OOM then retried the SAME segment forever.
        Assert.Equal(expected, ModelLadder.Downgrade(from, _ => true));
    }

    [Fact]
    public void Turbo_is_a_known_stem_but_has_no_english_weights()
    {
        // Finding I2 guard: there is no ggml-large-v3-turbo.en.bin. If HasEnglishVariant ever
        // returns true for it, TranscriptionWorker's language-lock swap tries to create an engine
        // over a nonexistent file and fails SILENTLY (the swap's catch only raises
        // MODEL_DOWNLOAD_FAILED). A green suite would not otherwise catch that.
        Assert.True(ModelLadder.IsKnownStem("large-v3-turbo"));
        Assert.False(ModelLadder.HasEnglishVariant("large-v3-turbo"));
        Assert.False(ModelLadder.HasEnglishVariant("large-v3"));
    }

    [Fact]
    public void Adding_turbo_to_the_downgrade_ladder_does_not_move_the_live_ceiling()
    {
        // Owner ruling 2026-08-05: the live cap stays. BackendSelector.Ladder is a SEPARATE,
        // English-only, 3-rung array; turbo present on disk must NOT raise the CUDA auto ceiling
        // above small.en. Standing guard alongside Big_nvidia_gets_cuda_small_en above.
        var (plan, downgradedFrom) = BackendSelector.Select(new HardwareInfo(true, 12000, true, 16),
            S(), Present("large-v3-turbo", "large-v3", "small.en", "base.en", "tiny.en"));
        Assert.Equal("small.en", plan.ModelName);
        Assert.Null(downgradedFrom);
    }
}
