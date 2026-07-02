// src/LocalScribe.Core/Storage/MattersIndexRebuilder.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>The Stage 4 index self-heal promised by MatterStore.cs:5-7 (design 4.3). Rebuild
/// makes matters.json exactly the set of loadable matters/*/matter.json files (orphans adopted,
/// vanished folders dropped) with SessionCount recomputed from all session metas' matterIds and
/// Archived taken from matter.json. Between rebuilds, ApplyTagDeltaAsync keeps counts current
/// incrementally. All calls are serialized by the maintenance service (design 7.3); the
/// single-instance guard (design 7.2) removes the cross-process race.</summary>
public sealed class MattersIndexRebuilder(StoragePaths paths)
{
    public async Task<MattersIndex> RebuildAsync(CancellationToken ct)
    {
        // Recompute tag counts from the metas (migration-tolerant reads via the catalog).
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var catalog = await new SessionCatalog(paths).ListAsync(ct);
        foreach (var item in catalog.Sessions)
            foreach (string mid in item.Meta.MatterIds)
                counts[mid] = counts.TryGetValue(mid, out int n) ? n + 1 : 1;

        var store = new MatterStore(paths.MattersDir);
        var entries = new List<MattersIndexEntry>();
        if (Directory.Exists(paths.MattersDir))
        {
            foreach (string dir in Directory.EnumerateDirectories(paths.MattersDir))
            {
                ct.ThrowIfCancellationRequested();
                string id = Path.GetFileName(dir);   // id doubles as the folder name (design 4.2)
                Matter? matter;
                try { matter = await store.LoadAsync(id, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    continue;   // unreadable matter.json: skipped this rebuild; next save re-adds it
                }
                if (matter is null) continue;         // folder without matter.json: nothing to adopt
                entries.Add(new MattersIndexEntry
                {
                    Id = id,
                    Name = matter.Name,
                    Reference = matter.Reference,
                    Archived = matter.Archived,
                    SessionCount = counts.TryGetValue(id, out int n) ? n : 0,
                });
            }
        }

        var rebuilt = new MattersIndex
        {
            SchemaVersion = MatterStore.Version,
            Matters = entries.OrderBy(e => e.Id, StringComparer.Ordinal).ToList(),   // deterministic
        };
        await JsonFile.WriteAsync(paths.MattersIndexJson, rebuilt, ct);              // atomic
        return rebuilt;
    }
}
