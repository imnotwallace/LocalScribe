// src/LocalScribe.Core/Storage/MatterDeleter.cs
namespace LocalScribe.Core.Storage;

/// <summary>Matter delete (design 4.1): BLOCKED while any session meta references the matter
/// (throws InvalidOperationException naming the count - the dialog suggests archiving instead).
/// Unreferenced delete recycles matters/&lt;id&gt;/ and removes the index entry. This deletes
/// organizational data only: blocked-while-referenced guarantees no session content references
/// it, so the evidentiary invariant (spec 1.1 - coarse whole-session delete is the only
/// session-data deletion) is untouched. Callers serialize via the maintenance service.</summary>
public sealed class MatterDeleter(StoragePaths paths, IRecycleBin bin)
{
    public async Task<int> CountReferencesAsync(string matterId, CancellationToken ct)
    {
        var catalog = await new SessionCatalog(paths).ListAsync(ct);
        return catalog.Sessions.Count(s => s.Meta.MatterIds.Contains(matterId));
    }

    public async Task DeleteAsync(string matterId, CancellationToken ct)
    {
        int references = await CountReferencesAsync(matterId, ct);
        if (references > 0)
            throw new InvalidOperationException(
                $"Matter '{matterId}' is referenced by {references} session(s) and cannot be deleted; archive it instead.");

        // Recycle the folder when present; a vanished folder (crash-window half-state,
        // MatterStore.cs:6-7) still gets its index entry healed away below.
        string dir = Path.Combine(paths.MattersDir, matterId);
        if (Directory.Exists(dir)) bin.SendToRecycleBin(dir);

        // Read-modify-write of matters.json, mirroring MatterStore.UpsertIndexAsync
        // (MatterStore.cs:41-55): ListAsync + direct JsonFile write stamped with the Version.
        var index = await new MatterStore(paths.MattersDir).ListAsync(ct);
        var entries = index.Matters.Where(e => e.Id != matterId).ToList();
        await JsonFile.WriteAsync(paths.MattersIndexJson,
            index with { SchemaVersion = MatterStore.Version, Matters = entries }, ct);
    }
}
