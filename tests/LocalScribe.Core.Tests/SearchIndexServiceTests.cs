using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;

public sealed class SearchIndexServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    public SearchIndexServiceTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private SearchIndexService MakeService()
        => new(_paths, () => new Settings(), TimeProvider.System, saveDebounceMs: 0);

    private async Task SeedSessionAsync(string id, string text, DateTimeOffset? started = null)
    {
        var t0 = started ?? new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = t0, EndedAtUtc = t0.AddMinutes(5),
            DurationMs = 300_000,
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta { Title = "T-" + id }, default);
        await new TranscriptStore(_paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, text, "Me"), default);
    }

    [Fact]
    public async Task Initialize_builds_from_disk_persists_the_cache_and_flips_ready()
    {
        await SeedSessionAsync("s-1", "the quick brown fox");
        await SeedSessionAsync("s-2", "totally different words");
        var svc = MakeService();
        bool readyFired = false;
        svc.ReadyChanged += () => readyFired = true;
        Assert.False(svc.IsReady);
        Assert.Empty(svc.Query(new SearchQuery("fox")));                  // not ready -> empty, never throws

        await svc.InitializeAsync(CancellationToken.None);

        Assert.True(svc.IsReady);
        Assert.True(readyFired);
        var r = Assert.Single(svc.Query(new SearchQuery("fox")));
        Assert.Equal("s-1", r.Session.SessionId);
        Assert.True(File.Exists(_paths.SearchIndexJson));                 // cache persisted
    }

    [Fact]
    public async Task Initialize_reuses_fresh_cache_entries_and_rederives_stale_ones()
    {
        await SeedSessionAsync("s-1", "the quick brown fox");
        var first = MakeService();
        await first.InitializeAsync(CancellationToken.None);

        // Tamper the CACHE only (stamps stay fresh): a second Initialize must trust it - the
        // observable proof that fresh entries are reused without re-deriving from session files.
        var store = new SearchIndexStore(_paths);
        var cache = await store.LoadAsync(CancellationToken.None);
        await store.SaveAsync(cache! with
        {
            Sessions = cache.Sessions.Select(s => s with { Title = "TAMPERED" }).ToList(),
        }, CancellationToken.None);
        var second = MakeService();
        await second.InitializeAsync(CancellationToken.None);
        Assert.Equal("TAMPERED",
            Assert.Single(second.Query(new SearchQuery("fox"))).Session.Title);

        // Touch meta.json (what any real edit does): the stamp mismatch makes the entry stale, so
        // the next Initialize re-derives from disk truth and the tampering vanishes.
        await new MetadataStore(_paths.MetaJson("s-1"))
            .SaveAsync(new SessionMeta { Title = "Real title" }, default);
        var third = MakeService();
        await third.InitializeAsync(CancellationToken.None);
        Assert.Equal("Real title",
            Assert.Single(third.Query(new SearchQuery("fox"))).Session.Title);
    }

    [Fact]
    public async Task Initialize_drops_orphans_skips_unreadable_sessions_and_rebuilds_a_corrupt_cache()
    {
        await SeedSessionAsync("s-ok", "indexable content");
        Directory.CreateDirectory(_paths.SessionDir("s-bad"));            // unreadable session.json
        await File.WriteAllTextAsync(_paths.SessionJson("s-bad"), "{ not json");
        await new SearchIndexStore(_paths).SaveAsync(new SearchIndexCache  // orphan cache entry
        {
            Sessions = new[]
            {
                new SearchSessionEntry
                {
                    SessionId = "s-gone", Title = "Ghost",
                    Lines = new[] { new SearchLine(0, 0, 0, "ghost content", null, "Sam") },
                },
            },
        }, CancellationToken.None);

        var svc = MakeService();
        var skipped = new List<string>();
        svc.SessionSkipped += (id, _) => skipped.Add(id);
        await svc.InitializeAsync(CancellationToken.None);

        Assert.Single(svc.Query(new SearchQuery("indexable")));           // healthy session indexed
        Assert.Empty(svc.Query(new SearchQuery("ghost")));                // orphan dropped
        Assert.Equal(new[] { "s-bad" }, skipped.ToArray());               // unreadable: skipped + logged

        await File.WriteAllTextAsync(_paths.SearchIndexJson, "!!!! not json");
        var rebuilt = MakeService();
        await rebuilt.InitializeAsync(CancellationToken.None);            // corrupt cache: silent rebuild
        Assert.Single(rebuilt.Query(new SearchQuery("indexable")));
    }

    [Fact]
    public async Task Initialize_survives_a_failed_cache_write_after_it_is_ready()
    {
        // B4-2: block the cache path with a directory so the first-run persist fails. Initialize
        // must still complete (never fault the returned Task) - IsReady/ReadyChanged already fired
        // and the in-memory index is usable; the derived cache self-heals on the next launch.
        await SeedSessionAsync("s-1", "the quick brown fox");
        Directory.CreateDirectory(_paths.SearchIndexJson);               // a directory where the file must go
        var svc = MakeService();
        bool readyFired = false;
        svc.ReadyChanged += () => readyFired = true;

        await svc.InitializeAsync(CancellationToken.None);               // must NOT throw

        Assert.True(svc.IsReady);
        Assert.True(readyFired);
        Assert.Single(svc.Query(new SearchQuery("fox")));                // in-memory index still usable
    }

    [Fact]
    public async Task Reindex_updates_removes_and_flush_persists()
    {
        await SeedSessionAsync("s-1", "first words");
        var svc = MakeService();
        await svc.InitializeAsync(CancellationToken.None);
        Assert.Empty(svc.Query(new SearchQuery("appended")));

        await new TranscriptStore(_paths.TranscriptJsonl("s-1")).AppendAsync(
            TranscriptLine.Segment(1, TranscriptSource.Local, 2000, 3000,
                "appended after finalize", "Me"), default);
        await svc.ReindexSessionAsync("s-1", CancellationToken.None);
        Assert.Single(svc.Query(new SearchQuery("appended")));

        Directory.Delete(_paths.SessionDir("s-1"), recursive: true);
        await svc.ReindexSessionAsync("s-1", CancellationToken.None);
        Assert.Empty(svc.Query(new SearchQuery("appended")));             // dropped from memory

        await svc.FlushAsync(CancellationToken.None);                     // force the debounced rewrite
        var persisted = await new SearchIndexStore(_paths).LoadAsync(CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Empty(persisted!.Sessions);                                // removal persisted
    }
}
