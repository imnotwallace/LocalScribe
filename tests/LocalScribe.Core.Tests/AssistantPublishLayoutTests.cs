using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

public sealed class AssistantPublishLayoutTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-pub-layout-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private void MakeCompleteFakeTree()
    {
        foreach (string rel in AssistantPublishLayout.RequiredRelativePaths)
        {
            string p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "x");   // non-empty
        }
    }

    [Fact]
    public void The_contract_is_the_exact_verified_deployment_shape()
    {
        // 4 CPU variants x 5 dlls + cuda12 x 6 (incl. the co-located avx2 ggml-cpu.dll:
        // the CUDA ggml.dll imports it at LOAD time - verified 2026-07-23) + the exe.
        Assert.Equal(27, AssistantPublishLayout.RequiredRelativePaths.Count);
        Assert.Contains("LocalScribe.Assistant.exe", AssistantPublishLayout.RequiredRelativePaths);
        Assert.Contains("runtimes/win-x64/native/cuda12/ggml-cpu.dll", AssistantPublishLayout.RequiredRelativePaths);
        Assert.Contains("runtimes/win-x64/native/cuda12/llama.dll", AssistantPublishLayout.RequiredRelativePaths);
        Assert.Contains("runtimes/win-x64/native/avx2/llama.dll", AssistantPublishLayout.RequiredRelativePaths);
    }

    [Fact]
    public void Complete_tree_has_nothing_missing()
    {
        MakeCompleteFakeTree();
        Assert.Empty(AssistantPublishLayout.FindMissing(_root));
    }

    [Fact]
    public void Missing_and_empty_files_are_both_flagged()
    {
        MakeCompleteFakeTree();
        File.Delete(Path.Combine(_root, "runtimes", "win-x64", "native", "cuda12", "llama.dll"));
        File.WriteAllText(Path.Combine(_root, "runtimes", "win-x64", "native", "avx2", "ggml-cpu.dll"), "");
        var missing = AssistantPublishLayout.FindMissing(_root);
        Assert.Equal(2, missing.Count);
        Assert.Contains("runtimes/win-x64/native/cuda12/llama.dll", missing);
        Assert.Contains("runtimes/win-x64/native/avx2/ggml-cpu.dll", missing);
    }

    [Fact]
    public void Guard_script_lists_every_required_path_verbatim()
    {
        // Drift guard: tools/verify-assistant-publish.ps1 re-states the list (PowerShell cannot
        // call Core); this pins the two copies together. Repo root found the FfmpegLocator way.
        string? repo = null;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx"))) { repo = d.FullName; break; }
        Assert.NotNull(repo);
        string script = File.ReadAllText(Path.Combine(repo!, "tools", "verify-assistant-publish.ps1"));
        foreach (string rel in AssistantPublishLayout.RequiredRelativePaths)
            Assert.Contains(rel, script);
    }
}
