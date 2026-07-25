using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes people\people.json (voiceprint design 2026-07-25). Absent until the
/// first person is created.</summary>
public sealed class PeopleStore
{
    public const int Version = 1;
    private readonly string _path;
    public PeopleStore(string peopleJsonPath) => _path = peopleJsonPath;

    public Task SaveAsync(PeopleRegistry registry, CancellationToken ct)
        => JsonFile.WriteAsync(_path, registry with { SchemaVersion = Version }, ct);

    public async Task<PeopleRegistry?> LoadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return null;
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "people.json");
        return await JsonFile.ReadAsync<PeopleRegistry>(_path, ct);
    }
}
