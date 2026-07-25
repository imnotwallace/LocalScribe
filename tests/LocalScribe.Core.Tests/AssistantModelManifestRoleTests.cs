using System.Security.Cryptography;
using LocalScribe.Core.Assistant;

public sealed class AssistantModelManifestRoleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    public AssistantModelManifestRoleTests() => Directory.CreateDirectory(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private async Task<string> WriteModelAsync(string name)
    {
        string path = Path.Combine(_root, name);
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        return Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }

    private Task WriteManifestAsync(string modelsJsonArray) => File.WriteAllTextAsync(
        Path.Combine(_root, "assistant-manifest.json"),
        "{\"schemaVersion\":1,\"models\":[" + modelsJsonArray + "]}");

    [Fact]
    public async Task Embedding_role_entry_is_exposed_and_never_becomes_chat_default()
    {
        string shaChat = await WriteModelAsync("chat.gguf");
        string shaEmb = await WriteModelAsync("embed.gguf");
        await WriteManifestAsync(
            $"{{\"canonicalName\":\"Chat\",\"file\":\"chat.gguf\",\"sha256\":\"{shaChat}\",\"nativeCtx\":4096,\"license\":\"Apache-2.0\"}}," +
            $"{{\"canonicalName\":\"Embed\",\"file\":\"embed.gguf\",\"sha256\":\"{shaEmb}\",\"nativeCtx\":2048,\"license\":\"Gemma\",\"role\":\"embedding\"}}");

        var m = await AssistantModelManifest.LoadAsync(_root, CancellationToken.None);

        Assert.Equal("Chat", m.DefaultModel?.CanonicalName);          // role-less entry = chat
        Assert.Equal("Embed", m.EmbeddingModel?.CanonicalName);
        Assert.Equal("embedding", m.EmbeddingModel?.Role);
    }

    [Fact]
    public async Task Manifest_with_only_an_embedding_model_has_no_chat_default()
    {
        string shaEmb = await WriteModelAsync("embed.gguf");
        await WriteManifestAsync(
            $"{{\"canonicalName\":\"Embed\",\"file\":\"embed.gguf\",\"sha256\":\"{shaEmb}\",\"nativeCtx\":2048,\"license\":\"Gemma\",\"role\":\"embedding\"}}");

        var m = await AssistantModelManifest.LoadAsync(_root, CancellationToken.None);

        Assert.Null(m.DefaultModel);                                   // an embedder must never chat
        Assert.NotNull(m.EmbeddingModel);
    }

    [Fact]
    public async Task Missing_role_defaults_to_chat_so_existing_manifests_parse_unchanged()
    {
        string sha = await WriteModelAsync("chat.gguf");
        await WriteManifestAsync(
            $"{{\"canonicalName\":\"Chat\",\"file\":\"chat.gguf\",\"sha256\":\"{sha}\",\"nativeCtx\":4096,\"license\":\"Apache-2.0\"}}");

        var m = await AssistantModelManifest.LoadAsync(_root, CancellationToken.None);

        Assert.Equal("chat", Assert.Single(m.Installed).Role);
        Assert.Null(m.EmbeddingModel);
    }
}
