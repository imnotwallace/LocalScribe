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

    public async Task<SessionRecord?> ReadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return null;
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "session.json");
        return await JsonFile.ReadAsync<SessionRecord>(_path, ct);
    }
}
