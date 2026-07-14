// src/LocalScribe.Core/Search/SearchIndexStore.cs
using LocalScribe.Core.Storage;
namespace LocalScribe.Core.Search;

/// <summary>Load/save for the persisted search cache (design 2026-07-13 section 2.1). Writes are
/// atomic (JsonFile -> AtomicFile) and schema-stamped; loads return null for a missing, corrupt,
/// or newer-schema cache so the service does a SILENT full rebuild - the cache is derived data
/// (matters-index self-heal philosophy), never worth an error and never evidence.</summary>
public sealed class SearchIndexStore(StoragePaths paths)
{
    public const int Version = 1;

    public async Task<SearchIndexCache?> LoadAsync(CancellationToken ct)
    {
        try
        {
            var obj = await SchemaGuard.ReadObjectAsync(paths.SearchIndexJson, ct);
            if (obj is null) return null;                                  // missing
            if (SchemaGuard.ReadVersion(obj) > Version) return null;       // newer app wrote it
            return await JsonFile.ReadAsync<SearchIndexCache>(paths.SearchIndexJson, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }                                             // corrupt -> silent rebuild
    }

    public Task SaveAsync(SearchIndexCache cache, CancellationToken ct)
        => JsonFile.WriteAsync(paths.SearchIndexJson, cache with { SchemaVersion = Version }, ct);
}
