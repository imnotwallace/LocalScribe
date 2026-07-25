using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes a version-scoped embeddings.json. Derived data: absent, corrupt, or
/// forward-versioned files all load null (feature degrades to "no suggestions", never blocks).</summary>
public sealed class ClusterEmbeddingsStore
{
    public const int Version = 1;
    private readonly string _path;
    public ClusterEmbeddingsStore(string embeddingsJsonPath) => _path = embeddingsJsonPath;

    public Task SaveAsync(ClusterEmbeddings embeddings, CancellationToken ct)
        => JsonFile.WriteAsync(_path, embeddings with { SchemaVersion = Version }, ct);

    public async Task<ClusterEmbeddings?> LoadAsync(CancellationToken ct)
    {
        try
        {
            var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
            if (obj is null || SchemaGuard.ReadVersion(obj) > Version) return null;
            return await JsonFile.ReadAsync<ClusterEmbeddings>(_path, ct);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    public void Delete() { if (File.Exists(_path)) File.Delete(_path); }
}
