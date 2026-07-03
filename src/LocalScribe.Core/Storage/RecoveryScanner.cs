// src/LocalScribe.Core/Storage/RecoveryScanner.cs
namespace LocalScribe.Core.Storage;

/// <summary>Finds sessions the startup recovery scan must finalize: session.json reads OK and
/// EndedAtUtc == null (design 7.1). Reads use selfForMigration: null and may be the first
/// migration event for an old root - accepted. Unreadable folders are skipped silently: they
/// are the catalog's unreadable-count concern (design 3.1), not recovery's. Callers route the
/// per-session RecoverIfNeededAsync calls through the maintenance service's per-session queue.</summary>
public sealed class RecoveryScanner(StoragePaths paths)
{
    public async Task<IReadOnlyList<string>> FindUnendedAsync(CancellationToken ct)
    {
        if (!Directory.Exists(paths.SessionsDir)) return [];

        var unended = new List<string>();
        foreach (string dir in Directory.EnumerateDirectories(paths.SessionsDir))
        {
            ct.ThrowIfCancellationRequested();
            string id = Path.GetFileName(dir);
            try
            {
                var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(selfForMigration: null, ct);
                if (session is not null && session.EndedAtUtc is null) unended.Add(id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Skipped: unreadable folders surface via SessionCatalog.UnreadableCount instead.
            }
        }
        unended.Sort(StringComparer.Ordinal);   // deterministic scan order
        return unended;
    }
}
