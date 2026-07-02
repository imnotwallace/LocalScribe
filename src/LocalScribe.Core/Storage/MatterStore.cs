using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>CRUD over matters (spec section 1.5). Owns matter.json files and the matters.json index.
/// v2 adds archived on matter + index entry (additive; v1 reads as false, matter.json
/// write-migrates on load). SessionCount is persisted as given; recompute against session
/// matterIds is Stage 4's MattersIndexRebuilder. matter.json + index are two atomic writes
/// with a crash window in between: a matter can be missing from ListAsync until its next
/// save. The Stage 4 index rebuild is the self-heal.</summary>
public sealed class MatterStore
{
    public const int Version = 2;
    private readonly string _mattersDir;
    public MatterStore(string mattersDir) => _mattersDir = mattersDir;

    private string IndexPath => Path.Combine(_mattersDir, "matters.json");
    private string MatterPath(string id) => Path.Combine(_mattersDir, id, "matter.json");

    public Task CreateAsync(Matter matter, CancellationToken ct = default) => SaveAsync(matter, ct);

    public async Task SaveAsync(Matter matter, CancellationToken ct = default)
    {
        await JsonFile.WriteAsync(MatterPath(matter.Id), matter with { SchemaVersion = Version }, ct);
        await UpsertIndexAsync(matter, ct);
    }

    public async Task<Matter?> LoadAsync(string matterId, CancellationToken ct = default)
    {
        var obj = await SchemaGuard.ReadObjectAsync(MatterPath(matterId), ct);
        if (obj is null) return null;
        int v = SchemaGuard.ReadVersion(obj);
        SchemaGuard.RejectIfNewer(v, Version, "matter.json");
        var matter = await JsonFile.ReadAsync<Matter>(MatterPath(matterId), ct);
        if (matter is not null && v < Version)
        {
            matter = matter with { SchemaVersion = Version };
            await SaveAsync(matter, ct);    // write-migrate; SaveAsync also (re)upserts the index entry
        }
        return matter;
    }

    public async Task<MattersIndex> ListAsync(CancellationToken ct = default)
    {
        var obj = await SchemaGuard.ReadObjectAsync(IndexPath, ct);
        if (obj is null) return new MattersIndex();
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "matters.json");
        // No write-migration here: a v1 index reads with Archived=false and is rewritten
        // at v2 by the next upsert or the Stage 4 rebuild - ListAsync stays read-only.
        return await JsonFile.ReadAsync<MattersIndex>(IndexPath, ct) ?? new MattersIndex();
    }

    private async Task UpsertIndexAsync(Matter matter, CancellationToken ct)
    {
        var index = await ListAsync(ct);
        var entries = index.Matters.ToList();
        int existing = entries.FindIndex(e => e.Id == matter.Id);
        var entry = new MattersIndexEntry
        {
            Id = matter.Id,
            Name = matter.Name,
            Reference = matter.Reference,
            SessionCount = existing >= 0 ? entries[existing].SessionCount : 0,
            Archived = matter.Archived,
        };
        if (existing >= 0) entries[existing] = entry; else entries.Add(entry);
        await JsonFile.WriteAsync(IndexPath, index with { SchemaVersion = Version, Matters = entries }, ct);
    }
}
