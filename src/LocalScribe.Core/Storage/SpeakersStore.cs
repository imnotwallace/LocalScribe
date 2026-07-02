using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes speakers.json (spec section 1.3). Absent until diarisation or a pinned reassignment.</summary>
public sealed class SpeakersStore
{
    public const int Version = 1;
    private readonly string _path;
    public SpeakersStore(string speakersJsonPath) => _path = speakersJsonPath;

    public Task SaveAsync(Speakers speakers, CancellationToken ct)
        => JsonFile.WriteAsync(_path, speakers with { SchemaVersion = Version }, ct);

    public async Task<Speakers?> LoadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return null;
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "speakers.json");
        return await JsonFile.ReadAsync<Speakers>(_path, ct);
    }
}
