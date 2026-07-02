// src/LocalScribe.Core/Storage/SessionCatalog.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>One session row for the manager list: folder id + system truth + user meta (design 3.1).</summary>
public sealed record SessionListItem(string Id, SessionRecord Session, SessionMeta Meta);

/// <summary>Enumeration result: readable sessions newest-first plus the count of skipped
/// unreadable folders (surfaced as a footer note, never thrown - design 3.1).</summary>
public sealed record SessionCatalogResult(IReadOnlyList<SessionListItem> Sessions, int UnreadableCount);

/// <summary>Enumerates storageRoot/sessions/* through the existing stores - no sessions index
/// file, files stay the truth (design 3.1). Reads are the migration event for old roots
/// (SessionStore write-migrates v1/v2 -> v3 and synthesizes meta.json); every read passes
/// selfForMigration: null - never fabricate today's identity into old sessions. Callers that
/// need serialization against recovery/finalize route calls through the maintenance service's
/// per-session queue (design 7.3); this class is mechanism only.</summary>
public sealed class SessionCatalog(StoragePaths paths)
{
    public async Task<SessionCatalogResult> ListAsync(CancellationToken ct)
    {
        if (!Directory.Exists(paths.SessionsDir))
            return new SessionCatalogResult([], 0);

        var items = new List<SessionListItem>();
        int unreadable = 0;
        foreach (string dir in Directory.EnumerateDirectories(paths.SessionsDir))
        {
            ct.ThrowIfCancellationRequested();
            string id = Path.GetFileName(dir);
            try
            {
                var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(selfForMigration: null, ct);
                if (session is null) { unreadable++; continue; }    // session.json absent

                // Same fallback SessionWriter.RegenerateProjectionsAsync uses (SessionWriter.cs:19-30):
                // the session's own recorded offset; machine zone only for pre-v3 records (null offset).
                var startedLocal = session.UtcOffsetMinutes is int offsetMin
                    ? session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
                    : session.StartedAtUtc.ToLocalTime();
                var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(ct)
                           ?? SessionMeta.CreateDefault(session.App, startedLocal, self: null);
                items.Add(new SessionListItem(id, session, meta));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unreadable++;   // corrupt / future-schema / IO-failed folder: counted, never thrown
            }
        }
        return new SessionCatalogResult(
            items.OrderByDescending(i => i.Session.StartedAtUtc).ToList(), unreadable);
    }
}
