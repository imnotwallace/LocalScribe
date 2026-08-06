using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>manifest.json per transcript version (Tier 1 T1-7). SessionStore's shape exactly:
/// JsonFile over LocalScribeJson (camelCase, indented, WhenWritingNull, UTC ISO-8601) with the
/// SchemaVersion stamped ON WRITE, and SchemaGuard rejecting a forward version rather than
/// silently mangling it. Writes go through AtomicFile, so a crash mid-refresh leaves the previous
/// seal intact rather than a truncated one.</summary>
public sealed class ManifestStore(string path)
{
    public const int Version = 1;

    /// <summary>Null when no manifest exists - the normal state for every session recorded before
    /// this feature, and a state the verifier reports as "not sealed" rather than as a pass.</summary>
    public async Task<SessionManifest?> ReadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(path, ct);
        if (obj is null) return null;
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "manifest.json");
        return await JsonFile.ReadAsync<SessionManifest>(path, ct);
    }

    public Task SaveAsync(SessionManifest manifest, CancellationToken ct)
        => JsonFile.WriteAsync(path, manifest with { SchemaVersion = Version }, ct);
}
