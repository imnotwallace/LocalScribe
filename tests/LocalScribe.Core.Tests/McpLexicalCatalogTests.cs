using LocalScribe.Core.Mcp;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;

public sealed class McpLexicalCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);
    private static readonly DateTimeOffset T0 = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Builds_entries_from_disk_without_touching_the_cache_file()
    {
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call one", null, T0, "webex",
            "we discussed the settlement figure");
        var catalog = new McpLexicalCatalog(Paths, new Settings { StorageRoot = _root },
            new ManualUtcTimeProvider(T0));
        var entries = await catalog.GetEntriesAsync(default);
        Assert.True(entries.ContainsKey("s1"));
        Assert.False(File.Exists(Paths.SearchIndexJson)); // read-only: never writes the App's cache
    }

    [Fact]
    public async Task Reuses_a_fresh_cache_entry_and_leaves_the_cache_byte_identical()
    {
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call one", null, T0, "webex", "hello");
        // Seed the cache the way the App would: build + save via the real store.
        var entry = await SearchIndexBuilder.BuildEntryAsync(Paths,
            new Settings { StorageRoot = _root }, TimeProvider.System, "s1", ct: default);
        await new SearchIndexStore(Paths).SaveAsync(
            new SearchIndexCache { Sessions = [entry] }, default);
        byte[] before = await File.ReadAllBytesAsync(Paths.SearchIndexJson);

        var catalog = new McpLexicalCatalog(Paths, new Settings { StorageRoot = _root },
            new ManualUtcTimeProvider(T0));
        var entries = await catalog.GetEntriesAsync(default);
        Assert.True(entries.ContainsKey("s1"));
        Assert.Equal(before, await File.ReadAllBytesAsync(Paths.SearchIndexJson));
    }

    [Fact]
    public async Task New_session_appears_after_the_refresh_window()
    {
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call one", null, T0, "webex", "hello");
        var time = new ManualUtcTimeProvider(T0);
        var catalog = new McpLexicalCatalog(Paths, new Settings { StorageRoot = _root }, time,
            refreshInterval: TimeSpan.FromSeconds(10));
        Assert.Single(await catalog.GetEntriesAsync(default));

        TestSessionSeeder.WriteBasicSession(Paths, "s2", "Call two", null, T0.AddMinutes(1), "webex", "world");
        Assert.Single(await catalog.GetEntriesAsync(default)); // inside throttle window: stale snapshot
        time.Set(T0.AddSeconds(11));
        Assert.Equal(2, (await catalog.GetEntriesAsync(default)).Count);
    }

    [Fact]
    public async Task Corrupt_session_is_skipped_and_counted_not_fatal()
    {
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call one", null, T0, "webex", "hello");
        Directory.CreateDirectory(Path.Combine(Paths.SessionsDir, "s2"));
        await File.WriteAllTextAsync(Paths.SessionJson("s2"), "{corrupt");
        var catalog = new McpLexicalCatalog(Paths, new Settings { StorageRoot = _root },
            new ManualUtcTimeProvider(T0));
        var entries = await catalog.GetEntriesAsync(default);
        Assert.Single(entries);
        Assert.Equal(1, catalog.SkippedSessions);
    }

    [Fact]
    public async Task Changed_session_is_re_derived_not_served_from_the_seed()
    {
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call one", null, T0, "webex", "original text");
        var settings = new Settings { StorageRoot = _root };
        var entry = await SearchIndexBuilder.BuildEntryAsync(Paths, settings, TimeProvider.System, "s1", ct: default);
        await new SearchIndexStore(Paths).SaveAsync(new SearchIndexCache { Sessions = [entry] }, default);

        // Mutate the transcript AFTER the cache was seeded: freshness stamps must now disagree
        // with the seeded entry's Stamps, forcing a re-derive rather than a trust-the-cache reuse
        // (a regression that dropped the ComputeStamps(...) == known.Stamps check would still
        // serve the stale "original text" seed and pass every other test in this file).
        await new TranscriptStore(Paths.TranscriptJsonl("s1")).AppendAsync(
            TranscriptLine.Segment(1, TranscriptSource.Local, 1000, 2000, "brand new content", "Me"), default);

        var catalog = new McpLexicalCatalog(Paths, settings, new ManualUtcTimeProvider(T0));
        var entries = await catalog.GetEntriesAsync(default);

        var lines = entries["s1"].Lines;
        Assert.Contains(lines, l => l.Text == "brand new content");
        Assert.Equal(2, lines.Count);                 // both the original line AND the new one
        Assert.NotEqual(entry.Stamps, entries["s1"].Stamps);   // proves a re-derive actually happened
    }
}
