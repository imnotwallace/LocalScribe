namespace LocalScribe.Core.Assistant;

/// <summary>The assistant helper's REQUIRED publish shape (design 2026-07-23 sections 1-2).
/// The csproj target `_PreserveLlamaCppNativeLayout` is load-bearing: if it regresses, the
/// publish silently reverts to a flattened noavx layout (measured: a 1,145-token summary that
/// avx2 finishes in 112s did not finish in 600s on noavx). This list is what the layout guard
/// asserts - tools/verify-assistant-publish.ps1 mirrors it verbatim (drift-pinned by test).
/// cuda12 carries SIX files: the four package natives + ggml-cuda.dll + a co-located avx2
/// ggml-cpu.dll, because the CUDA ggml.dll imports ggml-cpu.dll at load time (verified
/// 2026-07-23 - removing it makes the whole CUDA set fail to load).</summary>
public static class AssistantPublishLayout
{
    private static readonly string[] CpuVariants = ["avx", "avx2", "avx512", "noavx"];
    private static readonly string[] PerVariantFiles =
        ["ggml-base.dll", "ggml-cpu.dll", "ggml.dll", "llama.dll", "mtmd.dll"];
    private static readonly string[] Cuda12Files =
        ["ggml-base.dll", "ggml-cpu.dll", "ggml-cuda.dll", "ggml.dll", "llama.dll", "mtmd.dll"];

    public static readonly IReadOnlyList<string> RequiredRelativePaths =
    [
        "LocalScribe.Assistant.exe",
        .. CpuVariants.SelectMany(v => PerVariantFiles.Select(f => $"runtimes/win-x64/native/{v}/{f}")),
        .. Cuda12Files.Select(f => $"runtimes/win-x64/native/cuda12/{f}"),
    ];

    /// <summary>Missing or zero-byte required files under publishDir (forward-slash relative
    /// paths, exactly as listed). Empty = the deployment is complete.</summary>
    public static IReadOnlyList<string> FindMissing(string publishDir)
        => RequiredRelativePaths.Where(rel =>
        {
            var f = new FileInfo(Path.Combine(publishDir, rel.Replace('/', Path.DirectorySeparatorChar)));
            return !f.Exists || f.Length == 0;
        }).ToList();
}
