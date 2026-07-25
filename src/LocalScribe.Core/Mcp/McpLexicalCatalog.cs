using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Mcp;

/// <summary>Read-only sibling of SearchIndexService for the standalone MCP server: builds the
/// in-memory lexical index from disk (using index/search-index.json as a read-only SEED), and
/// refreshes on query with an mtime short-circuit. NEVER writes the cache - self-heal writes
/// stay App-only (spec: read-only enforcement is structural).</summary>
public sealed class McpLexicalCatalog(StoragePaths paths, Settings settings, TimeProvider time,
    TimeSpan? refreshInterval = null)
{
    private readonly TimeSpan _interval = refreshInterval ?? TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, SearchSessionEntry> _entries = [];
    private bool _cacheSeedLoaded;
    private IReadOnlyDictionary<string, SearchSessionEntry>? _cacheSeed;

    public DateTimeOffset LastRefreshUtc { get; private set; } = DateTimeOffset.MinValue;
    public int SkippedSessions { get; private set; }

    public async Task<IReadOnlyDictionary<string, SearchSessionEntry>> GetEntriesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (now - LastRefreshUtc < _interval) return _entries;

            if (!_cacheSeedLoaded)
            {
                _cacheSeedLoaded = true;
                var cache = await new SearchIndexStore(paths).LoadAsync(ct);
                _cacheSeed = cache?.Sessions.ToDictionary(s => s.SessionId);
            }

            var next = new Dictionary<string, SearchSessionEntry>();
            int skipped = 0;
            if (Directory.Exists(paths.SessionsDir))
            {
                foreach (string dir in Directory.EnumerateDirectories(paths.SessionsDir))
                {
                    ct.ThrowIfCancellationRequested();
                    string id = Path.GetFileName(dir);
                    var known = _entries.GetValueOrDefault(id) ?? _cacheSeed?.GetValueOrDefault(id);
                    try
                    {
                        if (known is not null &&
                            SearchIndexBuilder.ComputeStamps(paths, id, known.VersionId) == known.Stamps)
                        { next[id] = known; continue; }
                        next[id] = await SearchIndexBuilder.BuildEntryAsync(paths, settings, time, id, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { skipped++; }
                }
            }
            (_entries, SkippedSessions, LastRefreshUtc) = (next, skipped, now);
            return _entries;
        }
        finally { _gate.Release(); }
    }
}
