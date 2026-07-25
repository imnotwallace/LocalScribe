// tests/LocalScribe.Core.Tests/ReadOnlyProjectionTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

/// <summary>Task 3b: pins the persistMigration:false path so an MCP read of a legacy session never
/// write-migrates corpus files (SessionStore.ReadAsync / MetadataStore.LoadAsync / the shared
/// SessionProjectionLoader / McpLexicalCatalog). Every case asserts on file BYTES (not just the
/// returned model) so a regression that brought the writes back would actually fail these.</summary>
public sealed class ReadOnlyProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private static readonly DateTimeOffset T0 = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private const string LegacyMetaJson = "{\"schemaVersion\":1,\"title\":\"Old intake\"}";

    /// <summary>A v1 session.json (same shape SessionMigratorTests uses): migrates all the way to
    /// v4 through the v1->v2->v3->v4 chain, synthesizing a meta.json along the way if none exists.</summary>
    private static string LegacySessionJson(string id) => $@"{{
        ""schemaVersion"": 1,
        ""id"": ""{id}"",
        ""app"": ""Webex"",
        ""startedAtUtc"": ""2026-07-01T09:00:00Z"",
        ""endedAtUtc"": ""2026-07-01T09:05:00Z"",
        ""durationMs"": 300000,
        ""sources"": [""Local"", ""Remote""],
        ""model"": ""small.en"",
        ""backend"": ""CPU"",
        ""language"": ""en"",
        ""audioRetained"": true,
        ""title"": ""Legacy call"",
        ""segmentCount"": 1,
        ""markerCount"": 0
    }}";

    [Fact]
    public async Task Legacy_meta_is_migrated_in_memory_without_writing()
    {
        string path = Path.Combine(_root, "meta.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(path, LegacyMetaJson);
        byte[] before = await File.ReadAllBytesAsync(path);

        var back = await new MetadataStore(path).LoadAsync(persistMigration: false, default);

        Assert.Equal(MetadataStore.Version, back!.SchemaVersion);
        Assert.Equal("Old intake", back.Title);
        Assert.Equal(before, await File.ReadAllBytesAsync(path));   // NOT rewritten
    }

    [Fact]
    public async Task Legacy_meta_is_still_write_migrated_by_default()
    {
        string path = Path.Combine(_root, "meta.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(path, LegacyMetaJson);
        byte[] before = await File.ReadAllBytesAsync(path);

        var back = await new MetadataStore(path).LoadAsync(default);   // existing single-arg overload

        Assert.Equal(MetadataStore.Version, back!.SchemaVersion);
        byte[] after = await File.ReadAllBytesAsync(path);
        Assert.NotEqual(before, after);                                // rewritten
        Assert.Contains("\"schemaVersion\": 2", System.Text.Encoding.UTF8.GetString(after));
    }

    [Fact]
    public async Task Legacy_session_json_is_migrated_in_memory_without_writing()
    {
        Directory.CreateDirectory(_root);
        string sessionPath = Path.Combine(_root, "session.json");
        string metaPath = Path.Combine(_root, "meta.json");
        await File.WriteAllTextAsync(sessionPath, LegacySessionJson("s1"));
        byte[] before = await File.ReadAllBytesAsync(sessionPath);

        var record = await new SessionStore(sessionPath)
            .ReadAsync(selfForMigration: null, persistMigration: false, default);

        Assert.Equal(SessionStore.Version, record!.SchemaVersion);
        Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, record.RetainedAudioSources);
        Assert.Equal(before, await File.ReadAllBytesAsync(sessionPath));   // NOT rewritten
        Assert.False(File.Exists(metaPath));                              // NOT synthesized either
    }

    [Fact]
    public async Task Legacy_session_json_is_still_write_migrated_by_default()
    {
        Directory.CreateDirectory(_root);
        string sessionPath = Path.Combine(_root, "session.json");
        string metaPath = Path.Combine(_root, "meta.json");
        await File.WriteAllTextAsync(sessionPath, LegacySessionJson("s1"));
        byte[] before = await File.ReadAllBytesAsync(sessionPath);

        var record = await new SessionStore(sessionPath).ReadAsync(default);   // existing overload

        Assert.Equal(SessionStore.Version, record!.SchemaVersion);
        byte[] after = await File.ReadAllBytesAsync(sessionPath);
        Assert.NotEqual(before, after);                                 // rewritten at v4
        Assert.Contains("\"schemaVersion\": 4", System.Text.Encoding.UTF8.GetString(after));
        Assert.True(File.Exists(metaPath));                             // synthesized (no self -> empty participants)
    }

    [Fact]
    public async Task Projection_of_a_legacy_session_leaves_every_file_untouched()
    {
        var paths = new StoragePaths(_root);
        const string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));
        await File.WriteAllTextAsync(paths.SessionJson(id), LegacySessionJson(id));
        await File.WriteAllTextAsync(paths.MetaJson(id), LegacyMetaJson);
        await new TranscriptStore(paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Hello there.", "Me"), default);

        var before = SnapshotFiles(paths.SessionDir(id));

        var loaded = await SessionProjectionLoader.LoadAsync(paths, new Settings(),
            new ManualUtcTimeProvider(T0), id, persistMigration: false, default);

        Assert.NotEmpty(loaded.Rows);
        Assert.NotNull(loaded.Header);
        AssertFilesUnchanged(paths.SessionDir(id), before);
    }

    /// <summary>Task 3b fix pass 1: a genuinely legacy session (pre-v3, no meta.json yet) must
    /// still surface its REAL title through the non-persisting path - only persistence may be
    /// skipped, never the migration content. Before the fix, the v2-&gt;v3 synthesized meta was
    /// discarded and MetadataStore.LoadAsync returned null, so the loader fell back to
    /// SessionMeta.CreateDefault's fabricated "{app} - {date}" title.</summary>
    [Fact]
    public async Task Legacy_session_without_meta_keeps_its_real_title_when_not_persisting()
    {
        var paths = new StoragePaths(_root);
        const string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));
        await File.WriteAllTextAsync(paths.SessionJson(id), LegacySessionJson(id));
        // No meta.json seeded: this session has never been migrated before.
        byte[] sessionBefore = await File.ReadAllBytesAsync(paths.SessionJson(id));

        var loaded = await SessionProjectionLoader.LoadAsync(paths, new Settings(),
            new ManualUtcTimeProvider(T0), id, persistMigration: false, default);

        Assert.Equal("Legacy call", loaded.Meta.Title);
        Assert.False(File.Exists(paths.MetaJson(id)));                              // still not written
        Assert.Equal(sessionBefore, await File.ReadAllBytesAsync(paths.SessionJson(id)));   // still not rewritten
    }

    /// <summary>Pins that the App's (persisting) behavior is unchanged by the fix: the same
    /// synthesized meta is written to meta.json and the returned title still matches.</summary>
    [Fact]
    public async Task Legacy_session_without_meta_still_writes_synthesized_meta_by_default()
    {
        var paths = new StoragePaths(_root);
        const string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));
        await File.WriteAllTextAsync(paths.SessionJson(id), LegacySessionJson(id));

        var loaded = await SessionProjectionLoader.LoadAsync(paths, new Settings(),
            new ManualUtcTimeProvider(T0), id, persistMigration: true, default);

        Assert.True(File.Exists(paths.MetaJson(id)));
        Assert.Equal("Legacy call", loaded.Meta.Title);
    }

    [Fact]
    public async Task Catalog_never_writes_when_a_session_is_legacy()
    {
        var paths = new StoragePaths(_root);
        const string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));
        await File.WriteAllTextAsync(paths.SessionJson(id), LegacySessionJson(id));
        await File.WriteAllTextAsync(paths.MetaJson(id), LegacyMetaJson);
        await new TranscriptStore(paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Hello there.", "Me"), default);

        var before = SnapshotFiles(paths.SessionDir(id));

        var catalog = new McpLexicalCatalog(paths, new Settings { StorageRoot = _root },
            new ManualUtcTimeProvider(T0));
        var entries = await catalog.GetEntriesAsync(default);

        Assert.True(entries.ContainsKey(id));
        AssertFilesUnchanged(paths.SessionDir(id), before);
        Assert.False(File.Exists(paths.SearchIndexJson));
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllBytes);

    private static void AssertFilesUnchanged(string dir, Dictionary<string, byte[]> before)
    {
        var after = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList();
        Assert.Equal(before.Keys.OrderBy(x => x), after.OrderBy(x => x));   // no new/missing files
        foreach (var (path, bytes) in before)
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }
}
