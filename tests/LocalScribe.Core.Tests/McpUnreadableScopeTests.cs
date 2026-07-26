using LocalScribe.Core.Assistant;
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;

/// <summary>Consent-scoped "unreadable sessions" honesty signal (brief 2026-07-26): a session whose
/// build fails AFTER meta.json is read has a known, checkable matter tag, so it can be counted when
/// the consent filter says the client is entitled to see it. A session whose meta.json is itself
/// unreadable stays unattributable and must never be counted client-side (that ambiguity is the
/// exact leak this design closes). See McpDtos.cs / McpCorpus.cs for the scoped field itself.</summary>
public sealed class McpUnreadableScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);
    private static readonly DateTimeOffset T0 = new(2026, 7, 26, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class NoEmbeddings : IMcpEmbeddingProvider
    {
        public Task<(IEmbeddingClient, string)> GetAsync(CancellationToken ct)
            => throw new McpToolException("semantic unavailable: no embed helper in test", "error");
    }

    private McpCorpus Corpus()
    {
        var settings = new Settings { StorageRoot = _root };
        var time = new ManualUtcTimeProvider(T0.AddDays(1));
        return new McpCorpus(Paths, settings, time,
            new McpConsentStore(Paths),
            new McpLexicalCatalog(Paths, settings, time),
            new SemanticIndexStore(Paths),
            new MatterStore(Paths.MattersDir),
            new SummaryStore(Paths),
            new NoEmbeddings());
    }

    /// <summary>Corrupts speakers.json with bytes that fail JSON parsing entirely
    /// (SchemaGuard.ReadObjectAsync -&gt; JsonNode.Parse throws JsonException, uncaught by
    /// SpeakersStore.LoadAsync). speakers.json is read by SessionProjectionLoader AFTER meta.json
    /// (and after the tolerant transcript.jsonl read, which skips malformed lines rather than
    /// throwing - it does NOT fail the build), so this lands the session in
    /// McpLexicalCatalog's skip path while meta.json stays perfectly readable.</summary>
    private void CorruptBuildAfterMeta(string sessionId)
    {
        Directory.CreateDirectory(Paths.SessionDir(sessionId));
        File.WriteAllText(Paths.SpeakersJson(sessionId), "not valid json {{{");
    }

    private void CorruptMeta(string sessionId)
        => File.WriteAllText(Paths.MetaJson(sessionId), "not valid json {{{");

    private async Task AssertGenuinelySkippedButMetaReadableAsync(string sessionId)
    {
        var catalog = new McpLexicalCatalog(Paths, new Settings { StorageRoot = _root },
            new ManualUtcTimeProvider(T0.AddDays(1)));
        await catalog.GetEntriesAsync(default);
        Assert.Contains(sessionId, catalog.SkippedSessionIds);
        var meta = await new MetadataStore(Paths.MetaJson(sessionId)).LoadAsync(persistMigration: false, default);
        Assert.NotNull(meta);
    }

    [Fact]
    public async Task Unreadable_session_in_an_allowlisted_matter_is_counted()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", "m-001", T0, "webex", "hello");
        CorruptBuildAfterMeta("s1");
        await AssertGenuinelySkippedButMetaReadableAsync("s1");
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);

        var r = await Corpus().SearchAsync("hello", null, null, null, null, 10, default);
        Assert.Equal(1, r.UnreadableSessions);
    }

    /// <summary>The leak test: the corrupted session's matter is NOT allowlisted, so the client is
    /// not entitled to know it exists at all - the count must stay 0. This must fail if attribution
    /// is dropped and the corpus-wide skipped count is returned instead (that would report 1).</summary>
    [Fact]
    public async Task Unreadable_session_in_a_non_allowlisted_matter_is_not_counted()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.EnsureMatter(Paths, "m-002", "Estate of Brown");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", "m-002", T0, "webex", "hello");
        CorruptBuildAfterMeta("s1");
        await AssertGenuinelySkippedButMetaReadableAsync("s1");
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);

        var r = await Corpus().SearchAsync("hello", null, null, null, null, 10, default);
        Assert.Equal(0, r.UnreadableSessions);
    }

    [Fact]
    public async Task Unreadable_session_with_an_unreadable_meta_is_not_counted()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", "m-001", T0, "webex", "hello");
        CorruptBuildAfterMeta("s1");
        CorruptMeta("s1"); // meta.json itself is now unreadable too
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);

        var catalog = new McpLexicalCatalog(Paths, new Settings { StorageRoot = _root },
            new ManualUtcTimeProvider(T0.AddDays(1)));
        await catalog.GetEntriesAsync(default);
        Assert.Contains("s1", catalog.SkippedSessionIds);

        var r = await Corpus().SearchAsync("hello", null, null, null, null, 10, default);
        Assert.Equal(0, r.UnreadableSessions);
    }

    [Fact]
    public async Task Multi_matter_unreadable_session_follows_the_all_matters_rule()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.EnsureMatter(Paths, "m-002", "Estate of Brown");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", "m-001", T0, "webex", "hello");
        await new MetadataStore(Paths.MetaJson("s1")).SaveAsync(
            new SessionMeta { Title = "Call", MatterIds = ["m-001", "m-002"] }, default);
        CorruptBuildAfterMeta("s1");
        await AssertGenuinelySkippedButMetaReadableAsync("s1");
        // Only m-001 is allowlisted; the privilege-safe "every matter must be allowlisted" rule
        // applies to attribution exactly as it does to visibility - so this must stay 0.
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);

        var r = await Corpus().SearchAsync("hello", null, null, null, null, 10, default);
        Assert.Equal(0, r.UnreadableSessions);
    }

    /// <summary>Uses a SINGLE shared McpCorpus instance across both halves - a fresh Corpus() per
    /// call would build a fresh cache each time and never exercise the stale-cache path. This is
    /// the shape that catches the cached-consent-verdict bug: the same process must be asked
    /// twice, with consent changing in between, exactly as a live MCP server would be.</summary>
    [Fact]
    public async Task Unassigned_unreadable_session_rides_the_unassigned_toggle()
    {
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", null, T0, "webex", "hello");
        CorruptBuildAfterMeta("s1");
        await AssertGenuinelySkippedButMetaReadableAsync("s1");
        var corpus = Corpus();

        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowUnassigned = false }, default);
        var denied = await corpus.SearchAsync("hello", null, null, null, null, 10, default);
        Assert.Equal(0, denied.UnreadableSessions);

        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowUnassigned = true }, default);
        var allowed = await corpus.SearchAsync("hello", null, null, null, null, 10, default);
        Assert.Equal(1, allowed.UnreadableSessions);
    }

    /// <summary>The revocation-leak regression test: the attribution cache must never survive a
    /// consent change on the SAME McpCorpus process. If the cache stored the consent VERDICT
    /// (rather than just the meta.json read), this would keep reporting 1 after the matter is
    /// unticked - telling the client a session exists that consent no longer allows it to see.</summary>
    [Fact]
    public async Task Revoking_a_matter_stops_counting_its_unreadable_session()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", "m-001", T0, "webex", "hello");
        CorruptBuildAfterMeta("s1");
        await AssertGenuinelySkippedButMetaReadableAsync("s1");
        var corpus = Corpus();

        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);
        var before = await corpus.SearchAsync("hello", null, null, null, null, 10, default);
        Assert.Equal(1, before.UnreadableSessions);

        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = [] }, default);
        var after = await corpus.SearchAsync("hello", null, null, null, null, 10, default);
        Assert.Equal(0, after.UnreadableSessions);
    }

    /// <summary>The inverse honesty-failure regression test: ticking a NEW matter on the same
    /// process must start counting its unreadable session immediately, not report a stale 0
    /// ("your results are complete") while a now-visible session is uncounted.</summary>
    [Fact]
    public async Task Ticking_a_matter_starts_counting_its_unreadable_session()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", "m-001", T0, "webex", "hello");
        CorruptBuildAfterMeta("s1");
        await AssertGenuinelySkippedButMetaReadableAsync("s1");
        var corpus = Corpus();

        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = [] }, default);
        var before = await corpus.SearchAsync("hello", null, null, null, null, 10, default);
        Assert.Equal(0, before.UnreadableSessions);

        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);
        var after = await corpus.SearchAsync("hello", null, null, null, null, 10, default);
        Assert.Equal(1, after.UnreadableSessions);
    }

    /// <summary>Pins persistMigration:false on the attribution path: a LEGACY (schemaVersion 1)
    /// meta.json must be read (and migrated in memory) WITHOUT being rewritten. Snapshots the
    /// WHOLE storage root except mcp\ (consent.json/audit log are legitimately writable), same
    /// pattern as ReadOnlyProjectionTests.</summary>
    [Fact]
    public async Task Attribution_never_writes()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", "m-001", T0, "webex", "hello");
        // Downgrade meta.json to a legacy (v1) shape in place.
        await File.WriteAllTextAsync(Paths.MetaJson("s1"),
            "{\"schemaVersion\":1,\"title\":\"Call\",\"matterIds\":[\"m-001\"]}");
        CorruptBuildAfterMeta("s1");
        await AssertGenuinelySkippedButMetaReadableAsync("s1");
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);

        var before = SnapshotFiles(Paths.Root);
        var r = await Corpus().SearchAsync("hello", null, null, null, null, 10, default);
        Assert.Equal(1, r.UnreadableSessions); // sanity: attribution actually ran and counted it
        AssertFilesUnchanged(Paths.Root, before);
    }

    [Fact]
    public async Task Unreadable_count_surfaces_on_list_sessions_and_semantic_coverage()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", "m-001", T0, "webex", "hello");
        CorruptBuildAfterMeta("s1");
        await AssertGenuinelySkippedButMetaReadableAsync("s1");
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);

        var listed = await Corpus().ListSessionsAsync(null, null, null, null, 0, 20, default);
        Assert.Equal(1, listed.UnreadableSessions);

        var settings = new Settings { StorageRoot = _root };
        var time = new ManualUtcTimeProvider(T0.AddDays(1));
        var semanticCorpus = new McpCorpus(Paths, settings, time,
            new McpConsentStore(Paths), new McpLexicalCatalog(Paths, settings, time),
            new SemanticIndexStore(Paths), new MatterStore(Paths.MattersDir),
            new SummaryStore(Paths), new FixedEmbeddings("fake@2"));
        var semantic = await semanticCorpus.SearchSemanticAsync("hello", null, null, null, null, 10, default);
        Assert.Equal(1, semantic.Coverage.UnreadableSessions);
    }

    private sealed class FixedEmbeddings(string method) : IMcpEmbeddingProvider, IEmbeddingClient
    {
        public Task<(IEmbeddingClient, string)> GetAsync(CancellationToken ct)
            => Task.FromResult(((IEmbeddingClient)this, method));
        public Task<EmbeddingBatch> EmbedAsync(string kind, IReadOnlyList<string> texts, CancellationToken ct)
            => Task.FromResult(new EmbeddingBatch(texts.Select(_ => new[] { 1f, 0f }).ToList(), method));
        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string root)
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !IsUnderMcpDir(root, f))
                .ToDictionary(f => f, File.ReadAllBytes)
            : new Dictionary<string, byte[]>();

    private static void AssertFilesUnchanged(string root, Dictionary<string, byte[]> before)
    {
        var after = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !IsUnderMcpDir(root, f)).ToList()
            : new List<string>();
        Assert.Equal(before.Keys.OrderBy(x => x), after.OrderBy(x => x));
        foreach (var (path, bytes) in before)
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static bool IsUnderMcpDir(string root, string filePath)
    {
        string mcpDir = new StoragePaths(root).McpDir;
        string rel = Path.GetRelativePath(mcpDir, filePath);
        return !rel.StartsWith("..", StringComparison.Ordinal) && rel != ".";
    }
}
