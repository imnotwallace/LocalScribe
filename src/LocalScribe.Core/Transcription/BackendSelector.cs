using LocalScribe.Core.Model;
namespace LocalScribe.Core.Transcription;

/// <summary>The chosen engine configuration: backend + ggml model name (spec section 3).</summary>
public sealed record BackendPlan(Backend Backend, string ModelName);

/// <summary>Pure spec-section 3 selection: probe order CUDA -> Vulkan -> CPU, model per tier,
/// explicit user overrides always win, .en weights when the session language is English.</summary>
public static class BackendSelector
{
    // Worst -> best. Auto never exceeds the per-backend ceiling; it downgrades within this ladder
    // to the best model actually present on disk (design section 1).
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

        return (new BackendPlan(backend, model), downgradedFrom);
    }

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
