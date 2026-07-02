using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes session.json (spec section 1.2). Rejects a newer schema; migration of
/// v1/v2 records is layered on in Task 7 (SessionMigrator).</summary>
public sealed class SessionStore
{
    public const int Version = 3;
    private readonly string _path;
    public SessionStore(string sessionJsonPath) => _path = sessionJsonPath;

    public Task SaveAsync(SessionRecord record, CancellationToken ct)
        => JsonFile.WriteAsync(_path, record with { SchemaVersion = Version }, ct);

    public Task<SessionRecord?> ReadAsync(CancellationToken ct) => ReadAsync(selfForMigration: null, ct);

    public async Task<SessionRecord?> ReadAsync(SessionParticipant? selfForMigration, CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return null;

        int version = SchemaGuard.ReadVersion(obj);
        SchemaGuard.RejectIfNewer(version, Version, "session.json");
        if (version == Version) return await JsonFile.ReadAsync<SessionRecord>(_path, ct);

        var result = SessionMigrator.Migrate(obj, selfForMigration);

        // meta.json BEFORE session.json: the v2->v3 hop moves title out of session.json, so a
        // crash between the writes must never leave the title in neither file. If we die after
        // meta.json, session.json is still v2 and the migration re-runs; the Exists guard then
        // keeps this meta.
        if (result.SynthesizedMeta is not null)
        {
            string metaPath = Path.Combine(Path.GetDirectoryName(_path)!, "meta.json");
            if (!File.Exists(metaPath))
                await new MetadataStore(metaPath).SaveAsync(result.SynthesizedMeta, ct);
        }
        await JsonFile.WriteAsync(_path, result.Session, ct);          // rewrite at v3 via typed model
        return result.Session;
    }
}
