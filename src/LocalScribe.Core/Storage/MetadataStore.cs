using LocalScribe.Core.Model;

namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes meta.json (spec section 1.4) - the only file user edits touch.
/// v2 adds archived (additive); a v1 file loads with Archived=false and is write-migrated
/// to v2 on load (reject-higher/migrate-lower, schema-version policy).</summary>
public sealed class MetadataStore
{
    public const int Version = 2;
    private readonly string _path;
    public MetadataStore(string metaJsonPath) => _path = metaJsonPath;

    public Task SaveAsync(SessionMeta meta, CancellationToken ct)
        => JsonFile.WriteAsync(_path, meta with { SchemaVersion = Version }, ct);

    public async Task<SessionMeta?> LoadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return null;
        int v = SchemaGuard.ReadVersion(obj);
        SchemaGuard.RejectIfNewer(v, Version, "meta.json");
        var meta = await JsonFile.ReadAsync<SessionMeta>(_path, ct);
        if (meta is not null && v < Version)
        {
            meta = meta with { SchemaVersion = Version };
            await SaveAsync(meta, ct);      // write-migrate: additive fields stay at defaults
        }
        return meta;
    }
}
