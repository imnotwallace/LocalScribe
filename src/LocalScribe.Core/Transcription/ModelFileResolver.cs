using System.Text.RegularExpressions;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Transcription;

/// <summary>Pure ggml model-file selection (spec 3: quantized weights on CPU/iGPU, fp16 on
/// CUDA). Model NAMES stay canonical everywhere (ladder, language fix-up, session records);
/// quantization is a file-level detail resolved here, per backend, at engine-creation time.
/// q8_0 outranks q5_1 on CPU/Vulkan: near-lossless accuracy (evidentiary transcripts) while
/// still roughly halving memory traffic vs f16.</summary>
public static partial class ModelFileResolver
{
    [GeneratedRegex(@"-q\d+_\d+$")]
    private static partial Regex QuantSuffix();

    /// <summary>Candidate file names in preference order for the given backend.</summary>
    public static IReadOnlyList<string> CandidateFiles(Backend backend, string modelName)
        => backend == Backend.Cuda
            ? [$"ggml-{modelName}.bin", $"ggml-{modelName}-q8_0.bin", $"ggml-{modelName}-q5_1.bin"]
            : [$"ggml-{modelName}-q8_0.bin", $"ggml-{modelName}-q5_1.bin", $"ggml-{modelName}.bin"];

    /// <summary>First candidate that exists; else the plain canonical file name, so
    /// ModelPaths.Require's "not downloaded" error names the file fetch-models documents.</summary>
    public static string Resolve(Backend backend, string modelName, Func<string, bool> exists)
    {
        foreach (string candidate in CandidateFiles(backend, modelName))
            if (exists(candidate)) return candidate;
        return $"ggml-{modelName}.bin";
    }

    /// <summary>"small.en-q8_0" -> "small.en"; names without a quant suffix pass through
    /// (version suffixes like "large-v3" are not quant suffixes).</summary>
    public static string CanonicalName(string modelName) => QuantSuffix().Replace(modelName, "");
}
