using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes session.json (spec section 1.2). Rejects a newer schema; migration of
/// v1/v2 records is layered on in Task 7 (SessionMigrator).</summary>
public sealed class SessionStore
{
    public const int Version = 4;
    private readonly string _path;
    public SessionStore(string sessionJsonPath) => _path = sessionJsonPath;

    public Task SaveAsync(SessionRecord record, CancellationToken ct)
        => JsonFile.WriteAsync(_path, record with { SchemaVersion = Version }, ct);

    public Task<SessionRecord?> ReadAsync(CancellationToken ct) => ReadAsync(selfForMigration: null, ct);

    public Task<SessionRecord?> ReadAsync(SessionParticipant? selfForMigration, CancellationToken ct)
        => ReadAsync(selfForMigration, persistMigration: true, ct);

    /// <summary>persistMigration:false computes the SAME in-memory migration (returns the fully
    /// migrated SessionRecord) but performs NEITHER the synthesized-meta write NOR the session.json
    /// rewrite - the MCP read-only server's path (spec: structural read-only enforcement; never
    /// write-migrate a corpus file it does not own).</summary>
    public async Task<SessionRecord?> ReadAsync(SessionParticipant? selfForMigration, bool persistMigration, CancellationToken ct)
        => (await ReadWithSynthesizedMetaAsync(selfForMigration, persistMigration, ct)).Session;

    /// <summary>Same migration as ReadAsync, but also surfaces the SessionMeta the v2-&gt;v3 hop
    /// synthesized in memory (null when the session was already current, or migration synthesized
    /// none). persistMigration:true still writes it to meta.json exactly as before; this is for
    /// persistMigration:false callers (SessionProjectionLoader) that need the real title even though
    /// it was never written to disk - the migration must never be lossy just because persistence was
    /// skipped (Task 3b fix pass 1).</summary>
    public async Task<SessionReadResult> ReadWithSynthesizedMetaAsync(
        SessionParticipant? selfForMigration, bool persistMigration, CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return new SessionReadResult(null, null);

        int version = SchemaGuard.ReadVersion(obj);
        SchemaGuard.RejectIfNewer(version, Version, "session.json");
        if (version == Version)
            return new SessionReadResult(await JsonFile.ReadAsync<SessionRecord>(_path, ct), null);

        var result = SessionMigrator.Migrate(obj, selfForMigration);

        if (persistMigration)
        {
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
        }
        return new SessionReadResult(result.Session, result.SynthesizedMeta);
    }
}

/// <summary>Result of SessionStore.ReadWithSynthesizedMetaAsync: the migrated session plus whatever
/// SessionMeta the migration synthesized in memory (null in the common current-schema case, or when
/// migration produced none). SynthesizedMeta is populated regardless of persistMigration so a
/// persisting caller can also inspect it if it ever needs to.</summary>
public sealed record SessionReadResult(SessionRecord? Session, SessionMeta? SynthesizedMeta);
