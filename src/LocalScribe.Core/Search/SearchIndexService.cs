// src/LocalScribe.Core/Search/SearchIndexService.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
namespace LocalScribe.Core.Search;

/// <summary>The in-memory cross-session search index (design 2026-07-13 section 2.1). Owns one
/// entry per session, seeded by InitializeAsync from the persisted cache with per-session
/// self-healing (fresh stamps -> entry reused; stale/missing -> re-derived via SessionProjection-
/// Loader; orphans dropped; unreadable sessions skipped + surfaced on SessionSkipped, never
/// blocking others; corrupt/newer cache -> silent full rebuild). Incremental updates ride
/// ReindexSessionAsync (the App wires the live-update seams to it); cache rewrites are debounced
/// (saveDebounceMs, tests pass 0) and forceable via FlushAsync. Query is pure and thread-safe over
/// a snapshot. READ-ONLY over session folders - the sole write target is the derived cache file.</summary>
public sealed class SearchIndexService
{
    private readonly StoragePaths _paths;
    private readonly Func<Settings> _settings;
    private readonly TimeProvider _time;
    private readonly int _saveDebounceMs;
    private readonly SearchIndexStore _store;
    private readonly object _lock = new();                       // guards _entries + _pendingSaveCts
    private readonly Dictionary<string, SearchSessionEntry> _entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _reindexGate = new(1, 1);     // serializes disk-deriving work
    private readonly SemaphoreSlim _saveGate = new(1, 1);        // serializes cache writes
    private CancellationTokenSource? _pendingSaveCts;
    private volatile bool _isReady;

    public SearchIndexService(StoragePaths paths, Func<Settings> settings, TimeProvider time,
        int saveDebounceMs = 2000)
    {
        (_paths, _settings, _time, _saveDebounceMs) = (paths, settings, time, saveDebounceMs);
        _store = new SearchIndexStore(paths);
    }

    /// <summary>False until InitializeAsync completes - the App surfaces this as "indexing...".</summary>
    public bool IsReady => _isReady;

    /// <summary>Fired (once) when IsReady flips true. May fire on a background thread.</summary>
    public event Action? ReadyChanged;

    /// <summary>An unreadable session was skipped (design 2.3) - the host logs it; never thrown,
    /// never blocking other sessions' results.</summary>
    public event Action<string, Exception>? SessionSkipped;

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _reindexGate.WaitAsync(ct);
        try
        {
            var cache = await _store.LoadAsync(ct);              // null = missing/corrupt/newer
            var cached = (cache?.Sessions ?? [])
                .ToDictionary(s => s.SessionId, StringComparer.Ordinal);
            var fresh = new Dictionary<string, SearchSessionEntry>(StringComparer.Ordinal);
            bool changed = cache is null;
            if (Directory.Exists(_paths.SessionsDir))
            {
                foreach (string dir in Directory.EnumerateDirectories(_paths.SessionsDir))
                {
                    ct.ThrowIfCancellationRequested();
                    string id = Path.GetFileName(dir);
                    var entry = await DeriveIfStaleAsync(id, cached.GetValueOrDefault(id), ct);
                    if (entry is null) { changed |= cached.ContainsKey(id); continue; }   // skipped
                    fresh[id] = entry;
                    changed |= !ReferenceEquals(entry, cached.GetValueOrDefault(id));
                }
            }
            changed |= cached.Keys.Any(k => !fresh.ContainsKey(k));   // orphans dropped
            lock (_lock)
            {
                _entries.Clear();
                foreach (var kv in fresh) _entries[kv.Key] = kv.Value;
            }
            _isReady = true;
            try { ReadyChanged?.Invoke(); } catch { }
            // B4-2: the cache is derived data and best-effort (same posture as DebouncedSaveAsync).
            // IsReady already flipped and the in-memory index is usable, so a first-run write failure
            // must not fault the returned Task - the self-heal rebuilds next launch. Cancellation
            // still propagates.
            if (changed)
                try { await SaveNowAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch { /* persistent write failure: index is ready in memory; cache self-heals */ }
        }
        finally { _reindexGate.Release(); }
    }

    /// <summary>Single-session incremental re-index on the live-update seams (finalize, edit save,
    /// re-render, re-transcribe/version switch, import, recovery, delete). Re-derives uncondition-
    /// ally (an event means something changed); a gone/unreadable session drops out of the index.
    /// Fire-and-forget safe: catches everything (failures surface on SessionSkipped only).</summary>
    public async Task ReindexSessionAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            await _reindexGate.WaitAsync(ct);
            try
            {
                var entry = Directory.Exists(_paths.SessionDir(sessionId))
                    ? await DeriveIfStaleAsync(sessionId, cachedEntry: null, ct)   // null cache -> force derive
                    : null;
                lock (_lock)
                {
                    if (entry is null) _entries.Remove(sessionId);
                    else _entries[sessionId] = entry;
                }
                ScheduleSave();
            }
            finally { _reindexGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { try { SessionSkipped?.Invoke(sessionId, ex); } catch { } }
    }

    /// <summary>Pure in-memory query over a snapshot (SearchQueryEngine semantics). Safe from any
    /// thread; before InitializeAsync completes the index is simply empty.</summary>
    public IReadOnlyList<SearchResult> Query(SearchQuery query)
    {
        List<SearchSessionEntry> snapshot;
        lock (_lock) snapshot = _entries.Values.ToList();
        return SearchQueryEngine.Run(snapshot, query);
    }

    /// <summary>Cancels any pending debounced save and writes the cache NOW (tests; also usable at
    /// shutdown). Idempotent - writing an unchanged snapshot is harmless (derived data).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        lock (_lock) { _pendingSaveCts?.Cancel(); _pendingSaveCts = null; }
        await SaveNowAsync(ct);
    }

    /// <summary>Reuses the cached entry when its freshness stamps AND active-version id still match
    /// disk; re-derives otherwise. Null = the session is unreadable right now (skipped + logged) or
    /// has no session.json. The record read never fabricates identity (selfForMigration: null,
    /// SessionCatalog's rule).</summary>
    private async Task<SearchSessionEntry?> DeriveIfStaleAsync(string id, SearchSessionEntry? cachedEntry,
        CancellationToken ct)
    {
        try
        {
            var record = await new SessionStore(_paths.SessionJson(id))
                .ReadAsync(selfForMigration: null, ct);
            if (record is null) return null;                              // no session.json: not indexable
            string versionId = record.ActiveVersion;
            var stamps = SearchIndexBuilder.ComputeStamps(_paths, id, versionId);
            if (cachedEntry is not null && cachedEntry.VersionId == versionId
                && cachedEntry.Stamps == stamps)
                return cachedEntry;                                       // fresh: reuse, no projection load
            return await SearchIndexBuilder.BuildEntryAsync(_paths, _settings(), _time, id, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            try { SessionSkipped?.Invoke(id, ex); } catch { }
            return null;
        }
    }

    /// <summary>Debounced cache rewrite (design 2.1): every schedule supersedes the previous one;
    /// the write itself is serialized and best-effort - a failed cache write is never fatal (the
    /// self-heal rebuilds next launch).</summary>
    private void ScheduleSave()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _pendingSaveCts?.Cancel();
            _pendingSaveCts = cts = new CancellationTokenSource();
        }
        _ = DebouncedSaveAsync(cts.Token);
    }

    private async Task DebouncedSaveAsync(CancellationToken ct)
    {
        try
        {
            if (_saveDebounceMs > 0) await Task.Delay(_saveDebounceMs, ct);
            await SaveNowAsync(ct);
        }
        catch (OperationCanceledException) { }    // superseded by a newer save (or shutdown)
        catch { }                                  // derived cache: best-effort by design
    }

    private async Task SaveNowAsync(CancellationToken ct)
    {
        SearchIndexCache snapshot;
        lock (_lock)
            snapshot = new SearchIndexCache
            {
                Sessions = _entries.Values
                    .OrderBy(e => e.SessionId, StringComparer.Ordinal).ToList(),   // deterministic
            };
        await _saveGate.WaitAsync(ct);
        try { await _store.SaveAsync(snapshot, ct); }
        finally { _saveGate.Release(); }
    }
}
