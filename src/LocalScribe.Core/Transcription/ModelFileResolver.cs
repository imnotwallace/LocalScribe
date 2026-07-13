using LocalScribe.Core.Model;
namespace LocalScribe.Core.Transcription;

/// <summary>Pure ggml model-file selection (spec 3: quantized weights on CPU/iGPU, fp16 on
/// CUDA). Model NAMES stay canonical everywhere (ladder, language fix-up, session records);
/// quantization is a file-level detail resolved here, per backend, at engine-creation time.
/// The resolved file is traced: engines expose it as WeightsFile, the worker markers any
/// mid-session change, and session.json records it (review findings 2026-07-13).</summary>
public static class ModelFileResolver
{
    /// <summary>Single source of truth, descending fidelity. CanonicalName strips EXACTLY this
    /// set and CandidateFiles probes EXACTLY this set, so every canonicalized name is loadable
    /// (review finding: a q5_0-only disk used to advertise a model Start accepted but the
    /// factory could not load - HF ships medium/large quantized only as q5_0). Suffixes outside
    /// this list (e.g. a future q9_9) stay raw everywhere and load verbatim.</summary>
    private static readonly string[] QuantSuffixes = ["q8_0", "q5_1", "q5_0", "q4_1", "q4_0"];

    /// <summary>Candidate file names in preference order for the given backend. q8_0 leads on
    /// CPU/Vulkan: near-lossless accuracy (evidentiary transcripts) at roughly half the f16
    /// memory traffic.</summary>
    public static IReadOnlyList<string> CandidateFiles(Backend backend, string modelName)
    {
        var quantized = QuantSuffixes.Select(q => $"ggml-{modelName}-{q}.bin");
        return backend == Backend.Cuda
            ? [$"ggml-{modelName}.bin", .. quantized]
            : [.. quantized, $"ggml-{modelName}.bin"];
    }

    /// <summary>First candidate that exists; else the plain canonical file name, so
    /// ModelPaths.Require's "not downloaded" error names the file fetch-models documents.</summary>
    public static string Resolve(Backend backend, string modelName, Func<string, bool> exists)
    {
        foreach (string candidate in CandidateFiles(backend, modelName))
            if (exists(candidate)) return candidate;
        return $"ggml-{modelName}.bin";
    }

    /// <summary>"small.en-q8_0" -> "small.en"; only known quant suffixes are stripped (version
    /// suffixes like "large-v3" and unknown quant styles pass through untouched). Idempotent:
    /// a stacked-suffix name (hand-renamed ggml-small.en-q5_1-q8_0.bin) is treated as raw -
    /// stripping once would desync surfaces that canonicalize different numbers of times, and
    /// stripping twice would advertise a name Resolve cannot load (re-verify 2026-07-13).</summary>
    public static string CanonicalName(string modelName)
    {
        foreach (string q in QuantSuffixes)
            if (modelName.EndsWith("-" + q, StringComparison.Ordinal))
            {
                string stripped = modelName[..^(q.Length + 1)];
                return HasKnownQuantSuffix(stripped) ? modelName : stripped;
            }
        return modelName;
    }

    private static bool HasKnownQuantSuffix(string name)
        => QuantSuffixes.Any(q => name.EndsWith("-" + q, StringComparison.Ordinal));
}
