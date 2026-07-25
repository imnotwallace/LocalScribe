using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;

public sealed class SemanticIndexServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    private string? _recordingBusy;                        // null = idle

    public SemanticIndexServiceTests()
    { _paths = new StoragePaths(_root); Directory.CreateDirectory(_paths.SessionsDir); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FakeEmbeddingClient : IEmbeddingClient
    {
        public int Calls; public int Released;
        public string Method = "fake@2";
        public Task<EmbeddingBatch> EmbedAsync(string kind, IReadOnlyList<string> texts,
            CancellationToken ct)
        {
            Calls++;
            var vectors = texts.Select(t => new[] { 1f, 0f }).ToList();   // deterministic unit vector
            return Task.FromResult(new EmbeddingBatch(vectors, Method));
        }
        public ValueTask ReleaseAsync() { Released++; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private async Task SeedSessionAsync(string id, string text)
    {
        var t0 = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = t0, EndedAtUtc = t0.AddMinutes(5),
            DurationMs = 300_000,
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta { Title = "T-" + id }, default);
        await new TranscriptStore(_paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, text, "Me"), default);
    }

    private async Task<(SemanticIndexService Svc, FakeEmbeddingClient Client, SearchIndexService Lex)>
        MakeAsync(int pollMs = 1)
    {
        var lex = new SearchIndexService(_paths, () => new Settings(), TimeProvider.System, 0);
        await lex.InitializeAsync(CancellationToken.None);
        var client = new FakeEmbeddingClient();
        var svc = new SemanticIndexService(_paths, () => new Settings(), TimeProvider.System,
            client, method: "fake@2", dim: 2,
            recordingBusy: () => _recordingBusy,
            lexicalSnapshot: lex.SnapshotEntries, pollMs: pollMs);
        return (svc, client, lex);
    }

    [Fact]
    public async Task ProcessPending_builds_persists_and_query_finds_the_session()
    {
        await SeedSessionAsync("s-1", "we could settle at three fifty");
        var (svc, client, _) = await MakeAsync();
        await svc.InitializeAsync(CancellationToken.None);      // enqueues all eligible
        await svc.ProcessPendingAsync(CancellationToken.None);

        Assert.True(File.Exists(_paths.SemanticSidecarFile("s-1")));
        Assert.Equal((1, 1), svc.Coverage);
        var results = await svc.QueryAsync(new SearchQuery("settlement figure"), [],
            CancellationToken.None);
        Assert.Equal("s-1", Assert.Single(results).Session.SessionId);
        Assert.True(client.Calls >= 2);                          // 1+ document batch + 1 query
    }

    [Fact]
    public async Task Fresh_sidecar_is_skipped_without_re_embedding()
    {
        await SeedSessionAsync("s-1", "content");
        var (svc, client, _) = await MakeAsync();
        await svc.InitializeAsync(CancellationToken.None);
        await svc.ProcessPendingAsync(CancellationToken.None);
        int after = client.Calls;

        svc.Enqueue("s-1");                                     // same content: stamps fresh
        await svc.ProcessPendingAsync(CancellationToken.None);
        Assert.Equal(after, client.Calls);                       // no new embed batch
    }

    [Fact]
    public async Task Edit_stamp_change_triggers_re_embed()
    {
        await SeedSessionAsync("s-1", "content");
        var (svc, client, _) = await MakeAsync();
        await svc.InitializeAsync(CancellationToken.None);
        await svc.ProcessPendingAsync(CancellationToken.None);
        int after = client.Calls;

        await File.WriteAllTextAsync(_paths.EditsJson("s-1"), "{\"schemaVersion\":1,\"corrections\":{}}");
        svc.Enqueue("s-1");
        await svc.ProcessPendingAsync(CancellationToken.None);
        Assert.True(client.Calls > after);
    }

    [Fact]
    public async Task Wrong_method_sidecar_is_discarded_at_initialize()
    {
        await SeedSessionAsync("s-1", "content");
        var store = new SemanticIndexStore(_paths);
        await store.SaveAsync("s-1", new SemanticSidecar("OLD@9", "v1",
            new SearchFreshnessStamps(), 2,
            [new SemanticChunk(0, 0, 0, 0, 1000, "stale")], [[1f, 0f]]), CancellationToken.None);

        var (svc, client, _) = await MakeAsync();
        await svc.InitializeAsync(CancellationToken.None);
        Assert.Equal(0, svc.Coverage.Fresh);                     // old-method sidecar not counted
        await svc.ProcessPendingAsync(CancellationToken.None);
        Assert.True(client.Calls > 0);                           // re-embedded under the new method
        Assert.Equal((1, 1), svc.Coverage);
    }

    [Fact]
    public async Task Recording_pause_parks_the_worker_and_releases_the_helper()
    {
        await SeedSessionAsync("s-1", "content");
        var (svc, client, _) = await MakeAsync(pollMs: 10);
        await svc.InitializeAsync(CancellationToken.None);

        _recordingBusy = "recording";
        var pending = svc.ProcessPendingAsync(CancellationToken.None);
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);                       // parked, not processing
        Assert.True(client.Released >= 1);                       // helper memory freed (32GB rule)
        Assert.Equal(0, client.Calls);

        _recordingBusy = null;
        await pending;                                           // resumes and completes
        Assert.True(client.Calls > 0);
    }

    [Fact]
    public async Task Query_is_exempt_from_the_recording_pause()
    {
        await SeedSessionAsync("s-1", "content");
        var (svc, _, _) = await MakeAsync();
        await svc.InitializeAsync(CancellationToken.None);
        await svc.ProcessPendingAsync(CancellationToken.None);

        _recordingBusy = "recording";
        var results = await svc.QueryAsync(new SearchQuery("anything"), [], CancellationToken.None);
        Assert.NotEmpty(results);                                // still answers mid-recording
    }

    [Fact]
    public async Task Gone_session_drops_its_sidecar()
    {
        await SeedSessionAsync("s-1", "content");
        var (svc, _, lex) = await MakeAsync();
        await svc.InitializeAsync(CancellationToken.None);
        await svc.ProcessPendingAsync(CancellationToken.None);

        Directory.Delete(_paths.SessionDir("s-1"), true);
        await lex.ReindexSessionAsync("s-1", CancellationToken.None);   // lexical drops it too
        svc.Enqueue("s-1");
        await svc.ProcessPendingAsync(CancellationToken.None);
        Assert.False(File.Exists(_paths.SemanticSidecarFile("s-1")));
        Assert.Equal((0, 0), svc.Coverage);
    }

    [Fact]
    public async Task Embed_failure_skips_the_session_and_counts_against_coverage()
    {
        await SeedSessionAsync("s-1", "content");
        var lex = new SearchIndexService(_paths, () => new Settings(), TimeProvider.System, 0);
        await lex.InitializeAsync(CancellationToken.None);
        var failing = new ThrowingClient();
        var svc = new SemanticIndexService(_paths, () => new Settings(), TimeProvider.System,
            failing, "fake@2", 2, () => null, lex.SnapshotEntries, pollMs: 1);
        string? skipped = null;
        svc.SessionSkipped += (id, _) => skipped = id;

        await svc.InitializeAsync(CancellationToken.None);
        await svc.ProcessPendingAsync(CancellationToken.None);

        Assert.Equal("s-1", skipped);
        Assert.Equal((0, 1), svc.Coverage);                      // honest coverage note fuel
    }

    private sealed class ThrowingClient : IEmbeddingClient
    {
        public Task<EmbeddingBatch> EmbedAsync(string kind, IReadOnlyList<string> texts,
            CancellationToken ct) => throw new InvalidOperationException("helper down");
        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
