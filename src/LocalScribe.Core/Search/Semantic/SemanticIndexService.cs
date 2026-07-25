using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Search.Semantic;

/// <summary>The Related-section query seam the Search page consumes (fake-able in VM tests).</summary>
public interface ISemanticSearch
{
    /// <summary>Fresh = eligible sessions with a current-method, current-stamps sidecar in
    /// memory and not queued for rework; Eligible = sessions in the lexical index. Fuels the
    /// "searched N of M sessions" coverage note (design 2026-07-25).</summary>
    (int Fresh, int Eligible) Coverage { get; }
    /// <summary>Vectors or coverage changed. May fire on a background thread.</summary>
    event Action? Changed;
    Task<IReadOnlyList<SemanticResult>> QueryAsync(SearchQuery query,
        IReadOnlyList<SearchResult> lexicalResults, CancellationToken ct);
}

/// <summary>The semantic-index orchestrator (design 2026-07-25) - SearchIndexService's mirror.
/// READ-ONLY over session folders; sole write target is index\semantic\. Eligibility + facet
/// metadata come from the LEXICAL index snapshot (one definition of searchable). Staleness =
/// the lexical rule + method: VersionId AND stamps AND method must all match. The backfill
/// worker processes one session at a time and PARKS while a recording is active, RELEASING the
/// warm helper process so its memory is freed for the live pipeline (the 32GB-laptop rule);
/// queries stay exempt - one sentence on CPU is negligible. All failures degrade per-session
/// (SessionSkipped), never fault the caller.</summary>
public sealed class SemanticIndexService : ISemanticSearch, IAsyncDisposable
{
    private readonly StoragePaths _paths;
    private readonly Func<Settings> _settings;
    private readonly TimeProvider _time;
    private readonly IEmbeddingClient _embeddings;
    private readonly string _method;
    private readonly int _dim;
    private readonly Func<string?> _recordingBusy;
    private readonly Func<IReadOnlyDictionary<string, SearchSessionEntry>> _lexicalSnapshot;
    private readonly int _pollMs;
    private readonly int _batchSize;
    private readonly SemanticIndexStore _store;
    private readonly object _lock = new();
    private readonly Dictionary<string, SemanticSidecar> _sidecars = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _workGate = new(1, 1);   // one drain at a time (worker + tests)
    private readonly CancellationTokenSource _disposeCts = new();
    private Task? _worker;

    public event Action? Changed;
    public event Action<string, Exception>? SessionSkipped;

    public SemanticIndexService(StoragePaths paths, Func<Settings> settings, TimeProvider time,
        IEmbeddingClient embeddings, string method, int dim,
        Func<string?> recordingBusy,
        Func<IReadOnlyDictionary<string, SearchSessionEntry>> lexicalSnapshot,
        int pollMs = 1000, int batchSize = 32)
        => (_paths, _settings, _time, _embeddings, _method, _dim, _recordingBusy,
            _lexicalSnapshot, _pollMs, _batchSize, _store)
            = (paths, settings, time, embeddings, method, dim, recordingBusy,
               lexicalSnapshot, pollMs, batchSize, new SemanticIndexStore(paths));

    public (int Fresh, int Eligible) Coverage
    {
        get
        {
            var lexical = _lexicalSnapshot();
            lock (_lock)
            {
                int fresh = lexical.Keys.Count(id => _sidecars.ContainsKey(id) && !_pending.Contains(id));
                return (fresh, lexical.Count);
            }
        }
    }

    /// <summary>Loads persisted sidecars (dropping wrong-method ones), enqueues EVERY eligible
    /// session (the per-session freshness check inside ProcessPending makes fresh ones a cheap
    /// no-op - one session.json read + four file stamps, no projection, no embed), and starts
    /// the background worker.</summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        foreach (string id in _store.ListSessionIds())
        {
            ct.ThrowIfCancellationRequested();
            var sidecar = await _store.LoadAsync(id, ct);
            if (sidecar is null || sidecar.Method != _method) { _store.Delete(id); continue; }
            lock (_lock) _sidecars[id] = sidecar;
        }
        foreach (string id in _lexicalSnapshot().Keys) Enqueue(id);
        _worker = Task.Run(() => WorkerLoopAsync(_disposeCts.Token), CancellationToken.None);
        RaiseChanged();
    }

    public void Enqueue(string sessionId)
    {
        lock (_lock) { if (!_pending.Add(sessionId)) return; }
        _signal.Release();
    }

    /// <summary>Drains the pending set NOW. The production worker calls this on signal; tests
    /// call it directly for deterministic completion. Honors the recording pause between
    /// sessions AND between batches.</summary>
    public async Task ProcessPendingAsync(CancellationToken ct)
    {
        await _workGate.WaitAsync(ct);
        try
        {
            while (true)
            {
                string? id;
                lock (_lock) id = _pending.FirstOrDefault();
                if (id is null) return;
                await WaitWhileRecordingAsync(ct);
                try { await ReindexOneAsync(id, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { try { SessionSkipped?.Invoke(id, ex); } catch { } }
                lock (_lock) _pending.Remove(id);
                RaiseChanged();
            }
        }
        finally { _workGate.Release(); }
    }

    public async Task<IReadOnlyList<SemanticResult>> QueryAsync(SearchQuery query,
        IReadOnlyList<SearchResult> lexicalResults, CancellationToken ct)
    {
        var batch = await _embeddings.EmbedAsync("query", [query.Text ?? ""], ct);
        float[] qv = batch.Embeddings.Count > 0 ? batch.Embeddings[0] : [];
        Dictionary<string, SemanticSidecar> sidecars;
        lock (_lock) sidecars = new(_sidecars, StringComparer.Ordinal);
        var metadata = _lexicalSnapshot();
        return await Task.Run(
            () => SemanticQueryEngine.Run(qv, metadata, sidecars, query, lexicalResults), ct);
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        if (_worker is { } w) { try { await w; } catch { } }
        await _embeddings.DisposeAsync();
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct);
                try { await ProcessPendingAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch { }                                        // per-session failures already skipped
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Park while a recording is active, RELEASING the warm helper once (its ~600MB
    /// goes back to the live pipeline); polls until idle. Query embedding never routes here.</summary>
    private async Task WaitWhileRecordingAsync(CancellationToken ct)
    {
        bool released = false;
        while (_recordingBusy() is not null)
        {
            if (!released) { await _embeddings.ReleaseAsync(); released = true; }
            await Task.Delay(_pollMs, ct);
        }
    }

    private async Task ReindexOneAsync(string id, CancellationToken ct)
    {
        var lexical = _lexicalSnapshot();
        if (!lexical.ContainsKey(id) || !Directory.Exists(_paths.SessionDir(id)))
        {
            lock (_lock) _sidecars.Remove(id);
            _store.Delete(id);
            return;
        }
        var record = await new SessionStore(_paths.SessionJson(id)).ReadAsync(selfForMigration: null, ct);
        if (record is null) { lock (_lock) _sidecars.Remove(id); _store.Delete(id); return; }
        string versionId = record.ActiveVersion;
        // Stamps BEFORE the projection load (the safe direction: content changing after the stamp
        // makes the stamp older than reality, so the next content event re-derives - never fresher).
        var stamps = SearchIndexBuilder.ComputeStamps(_paths, id, versionId);
        lock (_lock)
            if (_sidecars.TryGetValue(id, out var existing) && existing.Method == _method
                && existing.VersionId == versionId && existing.Stamps == stamps)
                return;                                           // fresh - cheap no-op

        var loaded = await SessionProjectionLoader.LoadAsync(_paths, _settings(), _time, id, ct);
        var chunks = SemanticChunker.Chunk(loaded.Rows);
        var vectors = new List<float[]>(chunks.Count);
        for (int i = 0; i < chunks.Count; i += _batchSize)
        {
            await WaitWhileRecordingAsync(ct);                    // park between batches too
            var texts = chunks.Skip(i).Take(_batchSize).Select(c => c.Text).ToList();
            var result = await _embeddings.EmbedAsync("document", texts, ct);
            vectors.AddRange(result.Embeddings);
        }
        var sidecar = new SemanticSidecar(_method, versionId, stamps, _dim, chunks, vectors);
        await _store.SaveAsync(id, sidecar, ct);
        lock (_lock) _sidecars[id] = sidecar;
    }

    private void RaiseChanged() { try { Changed?.Invoke(); } catch { } }
}
