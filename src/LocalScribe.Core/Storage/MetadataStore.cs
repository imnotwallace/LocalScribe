using LocalScribe.Core.Model;

namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes meta.json (spec section 1.4) - the only file user edits touch.</summary>
public sealed class MetadataStore
{
    public const int Version = 1;
    private readonly string _path;
    public MetadataStore(string metaJsonPath) => _path = metaJsonPath;

    public Task SaveAsync(SessionMeta meta, CancellationToken ct)
        => JsonFile.WriteAsync(_path, meta with { SchemaVersion = Version }, ct);

    public async Task<SessionMeta?> LoadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return null;
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "meta.json");
        return await JsonFile.ReadAsync<SessionMeta>(_path, ct);
    }
}
