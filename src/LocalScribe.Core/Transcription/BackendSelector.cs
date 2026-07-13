using LocalScribe.Core.Model;
namespace LocalScribe.Core.Transcription;

/// <summary>The chosen engine configuration: backend + ggml model name (spec section 3).
/// CpuThreads rides along on every plan (the worker's VRAM-OOM floor fall flips Backend to Cpu
/// via `with {}` without re-running Select) but only takes effect on the CPU backend -
/// EffectiveThreads is what the engine actually applies; null keeps whisper.cpp defaults.</summary>
public sealed record BackendPlan(Backend Backend, string ModelName, int? CpuThreads = null)
{
    public int? EffectiveThreads => Backend == Backend.Cpu ? CpuThreads : null;
}

/// <summary>Pure spec-section 3 selection: probe order CUDA -> Vulkan -> CPU, model per tier,
/// explicit user overrides always win, .en weights when the session language is English.</summary>
public static class BackendSelector
{
    // Worst -> best. Auto never exceeds the per-backend ceiling; it downgrades within this ladder
    // to the best model actually present on disk (design section 1).
    // Final-review Finding 3: intentionally English-only - fetch-models ships only .en weights and
    // English is the primary use case (Webex/Zoom lawyer-jail calls). A non-English `auto` session
    // with only multilingual models present would refuse rather than downgrade; that's a Stage-7
    // concern (multilingual downgrade ladder), not a bug in this ladder.
    private static readonly string[] Ladder = ["tiny.en", "base.en", "small.en"];

    public static (BackendPlan Plan, string? DowngradedFrom) Select(
        HardwareInfo hw, Settings settings, IReadOnlySet<string> availableModels)
    {
        Backend backend = settings.Backend != Backend.Auto
            ? settings.Backend
            : hw.HasCuda && hw.CudaVramMb >= 4096 ? Backend.Cuda
            : hw.HasVulkan ? Backend.Vulkan
            : Backend.Cpu;

        string? downgradedFrom = null;
        string model;
        if (settings.Model != "auto")
        {
            model = settings.Model;                     // explicit: verbatim; Start validates presence
        }
        else
        {
            string ceiling = backend switch
            {
                Backend.Cuda => "small.en",
                Backend.Vulkan => "base.en",
                _ => hw.FastCores >= 8 ? "small.en" : "base.en",
            };
            model = BestPresentAtOrBelow(ceiling, availableModels);
            if (model != ceiling) downgradedFrom = ceiling;   // record the downgrade for a Start notice
        }

        bool english = settings.Language is "en" or "auto";
        if (!english && model.EndsWith(".en", StringComparison.Ordinal))
            model = model[..^3];                        // multilingual weights (spec 3)

        return (new BackendPlan(backend, model, AutoCpuThreads(hw.FastCores)), downgradedFrom);
    }

    /// <summary>whisper.cpp thread count for CPU inference: fast cores - 2 (leave headroom for
    /// the live call + WASAPI capture + UI), floor 2, cap 8 (memory-bandwidth bound past that).
    /// Beats whisper.cpp's own min(4, cores) default on 8+ core machines.</summary>
    public static int AutoCpuThreads(int fastCores) => Math.Clamp(fastCores - 2, 2, 8);

    private static string BestPresentAtOrBelow(string ceiling, IReadOnlySet<string> available)
    {
        int ceilingRank = Array.IndexOf(Ladder, ceiling);
        for (int r = ceilingRank; r >= 0; r--)
            if (available.Contains(Ladder[r])) return Ladder[r];
        // Nothing present at/below the ceiling: return the ceiling name unchanged so Start's
        // fail-fast (Task 3) refuses with a clear "not downloaded" message.
        return ceiling;
    }
}
