using System.Security.Cryptography;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

public sealed class AssistantModelManifestTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-manifest-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private async Task WriteManifestAsync(params AssistantManifestEntry[] entries)
        => await JsonFile.WriteAsync(Path.Combine(_root, "assistant-manifest.json"),
            new AssistantManifestFile { Models = entries }, CancellationToken.None);

    private string WriteModel(string file, string content)
    {
        string path = Path.Combine(_root, file);
        File.WriteAllText(path, content);
        return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task Missing_manifest_yields_an_empty_manifest_not_a_throw()
    {
        // Design 7.7: model missing -> features off with explainer; loading must never fault the app.
        var m = await AssistantModelManifest.LoadAsync(_root, CancellationToken.None);
        Assert.Empty(m.Installed);
        Assert.Null(m.DefaultModel);
    }

    [Fact]
    public async Task Verified_entries_install_and_the_locked_default_is_preferred()
    {
        string shaQ = WriteModel("q4b.gguf", "fake qwen weights");
        string shaS = WriteModel("q17b.gguf", "fake small weights");
        await WriteManifestAsync(
            new AssistantManifestEntry { CanonicalName = "Qwen3-1.7B-Instruct", File = "q17b.gguf", Sha256 = shaS, NativeCtx = 32768, License = "Apache-2.0" },
            new AssistantManifestEntry { CanonicalName = "Qwen3-4B-Instruct-2507", File = "q4b.gguf", Sha256 = shaQ, NativeCtx = 262144, License = "Apache-2.0" });

        var m = await AssistantModelManifest.LoadAsync(_root, CancellationToken.None);
        Assert.Equal(2, m.Installed.Count);
        Assert.Equal("Qwen3-4B-Instruct-2507", m.DefaultModel!.CanonicalName);   // LOCKED default
        Assert.Equal(Path.Combine(_root, "q4b.gguf"), m.DefaultModel.FilePath);
        Assert.Equal(262144, m.DefaultModel.NativeCtx);
        Assert.Empty(m.Notes);
    }

    [Fact]
    public async Task Missing_or_tampered_files_are_excluded_with_a_note_never_silently()
    {
        string sha = WriteModel("ok.gguf", "good");
        WriteModel("bad.gguf", "tampered");   // hash will NOT match the manifest pin below
        await WriteManifestAsync(
            new AssistantManifestEntry { CanonicalName = "Qwen3-4B-Instruct-2507", File = "ok.gguf", Sha256 = sha, NativeCtx = 262144, License = "Apache-2.0" },
            new AssistantManifestEntry { CanonicalName = "Qwen3-1.7B-Instruct", File = "bad.gguf", Sha256 = new string('0', 64), NativeCtx = 32768, License = "Apache-2.0" },
            new AssistantManifestEntry { CanonicalName = "Gemma-4-E2B-QAT", File = "absent.gguf", Sha256 = sha, NativeCtx = 32768, License = "Gemma Terms of Use" });

        var m = await AssistantModelManifest.LoadAsync(_root, CancellationToken.None);
        Assert.Single(m.Installed);                                   // fail-closed: corrupt never offered
        Assert.Equal("Qwen3-4B-Instruct-2507", m.Installed[0].CanonicalName);
        Assert.Equal(2, m.Notes.Count);                               // surfaced, never silent
        Assert.Contains(m.Notes, n => n.Contains("bad.gguf") && n.Contains("sha256"));
        Assert.Contains(m.Notes, n => n.Contains("absent.gguf") && n.Contains("missing"));
    }

    [Fact]
    public async Task Default_falls_back_to_the_first_installed_when_the_locked_default_is_absent()
    {
        string sha = WriteModel("q17b.gguf", "small");
        await WriteManifestAsync(new AssistantManifestEntry
        { CanonicalName = "Qwen3-1.7B-Instruct", File = "q17b.gguf", Sha256 = sha, NativeCtx = 32768, License = "Apache-2.0" });
        var m = await AssistantModelManifest.LoadAsync(_root, CancellationToken.None);
        Assert.Equal("Qwen3-1.7B-Instruct", m.DefaultModel!.CanonicalName);
    }

    [Fact]
    public async Task Cache_loads_once_and_reloads_after_invalidate()
    {
        int loads = 0;
        var cache = new AssistantManifestCache(_ =>
        { loads++; return Task.FromResult(new AssistantModelManifest([], null, [])); });
        await cache.GetAsync(CancellationToken.None);
        await cache.GetAsync(CancellationToken.None);
        Assert.Equal(1, loads);
        cache.Invalidate();
        await cache.GetAsync(CancellationToken.None);
        Assert.Equal(2, loads);
    }
}
