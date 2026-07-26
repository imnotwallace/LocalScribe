using LocalScribe.Core.Assistant;
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;

public sealed class McpCorpusReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class NoEmbeddings : IMcpEmbeddingProvider
    {
        public Task<(LocalScribe.Core.Search.Semantic.IEmbeddingClient, string)> GetAsync(CancellationToken ct)
            => throw new McpToolException("semantic unavailable", "error");
    }

    private McpCorpus Corpus()
    {
        var settings = new Settings { StorageRoot = _root };
        var time = new ManualUtcTimeProvider(T0.AddDays(1));
        return new McpCorpus(Paths, settings, time, new McpConsentStore(Paths),
            new McpLexicalCatalog(Paths, settings, time), new SemanticIndexStore(Paths),
            new MatterStore(Paths.MattersDir), new SummaryStore(Paths), new NoEmbeddings());
    }

    private async Task SeedAsync(int lineCount = 6)
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", "m-001", T0, "webex",
            Enumerable.Range(0, lineCount).Select(i => $"line number {i}").ToArray());
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);
    }

    [Fact]
    public async Task Reads_a_seq_range_with_speaker_names()
    {
        await SeedAsync();
        var r = await Corpus().ReadTranscriptAsync("s1", 1, 3, null, 0, null, default);
        Assert.All(r.Rows.Where(x => x.Kind == "speech"),
            x => Assert.InRange(x.Seq!.Value, 1, 3));
        Assert.Contains(r.Rows, x => x.Text.Contains("line number 2"));
        Assert.All(r.Rows.Where(x => x.Kind == "speech"), x => Assert.False(string.IsNullOrEmpty(x.Speaker)));
        Assert.Null(r.NextCursor);
    }

    [Fact]
    public async Task Around_seq_returns_the_context_window()
    {
        await SeedAsync();
        var r = await Corpus().ReadTranscriptAsync("s1", null, null, 3, 1, null, default);
        var seqs = r.Rows.Where(x => x.Seq is not null).Select(x => x.Seq!.Value).ToList();
        Assert.Equal([2, 3, 4], seqs);
    }

    [Fact]
    public async Task Char_cap_pages_with_a_version_pinned_cursor()
    {
        await SeedAsync(lineCount: 40);
        // Tiny cap via the test seam so we don't need 15k chars of fixture text.
        var r = await Corpus().ReadTranscriptAsync("s1", null, null, null, 0, null, default,
            maxChars: 60);
        Assert.True(r.Rows.Count < 40);
        Assert.NotNull(r.NextCursor);
        Assert.StartsWith(r.VersionId + ":", r.NextCursor);
        var r2 = await Corpus().ReadTranscriptAsync("s1", null, null, null, 0, r.NextCursor, default,
            maxChars: 60);
        Assert.NotEqual(r.Rows[0].Seq, r2.Rows[0].Seq);
    }

    [Fact]
    public async Task Cursor_from_a_different_version_is_rejected()
    {
        await SeedAsync();
        var ex = await Assert.ThrowsAsync<McpToolException>(() =>
            Corpus().ReadTranscriptAsync("s1", null, null, null, 0, "vOLD:2", default));
        Assert.Contains("version changed", ex.Message);
    }

    [Fact]
    public async Task Hidden_session_read_is_indistinguishable_from_missing()
    {
        await SeedAsync();
        TestSessionSeeder.WriteBasicSession(Paths, "s2", "Hidden", null, T0, "webex", "secret");
        var exHidden = await Assert.ThrowsAsync<McpToolException>(() =>
            Corpus().ReadTranscriptAsync("s2", null, null, null, 0, null, default));
        var exMissing = await Assert.ThrowsAsync<McpToolException>(() =>
            Corpus().ReadTranscriptAsync("nope", null, null, null, 0, null, default));
        Assert.Equal(exMissing.Message, exHidden.Message);
        Assert.Equal(McpConsentFilter.NotFoundMessage, exHidden.Message);
    }

    [Fact]
    public async Task GetSummary_returns_newest_version_with_provenance()
    {
        await SeedAsync();
        var store = new SummaryStore(Paths);
        await store.AppendAsync("s1", new SummaryVersion("v-a", T0, "v1",
            new AssistantModelRef("qwen.gguf", "aa", "cuda"), 2, "OLD", Stale: true), default);
        await store.AppendAsync("s1", new SummaryVersion("v-b", T0.AddHours(1), "v1",
            new AssistantModelRef("qwen.gguf", "aa", "cpu"), 2, "NEW summary", Stale: false,
            CudaFellToCpu: true), default);
        var r = await Corpus().GetSummaryAsync("s1", default);
        Assert.Equal("NEW summary", r.ContentMarkdown);
        Assert.True(r.CudaFellToCpu);
        Assert.Equal("cpu", r.Backend);
    }

    [Fact]
    public async Task GetSummary_on_summaryless_session_says_so()
    {
        await SeedAsync();
        var ex = await Assert.ThrowsAsync<McpToolException>(() => Corpus().GetSummaryAsync("s1", default));
        Assert.Contains("no summary", ex.Message);
    }

    // A genuinely legacy (pre-v3) session.json + meta.json - matches ReadOnlyProjectionTests'
    // fixture shape. A CURRENT-schema fixture (like SeedAsync's) would leave every file
    // untouched no matter what persistMigration value ReadTranscriptAsync passes through,
    // because SessionStore/MetadataStore only write when the on-disk schema version is stale
    // (see SessionStore.ReadWithSynthesizedMetaAsync / MetadataStore.LoadAsync) - so only a
    // legacy fixture actually exercises the persistMigration:false plumbing this test exists to pin.
    private const string LegacyMetaJson = "{\"schemaVersion\":1,\"title\":\"Old intake\"}";
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
    public async Task Reading_a_transcript_never_writes_to_the_session_folder()
    {
        string sessionDir = Paths.SessionDir("s1");
        Directory.CreateDirectory(sessionDir);
        await File.WriteAllTextAsync(Paths.SessionJson("s1"), LegacySessionJson("s1"));
        await File.WriteAllTextAsync(Paths.MetaJson("s1"), LegacyMetaJson);
        await new TranscriptStore(Paths.TranscriptJsonl("s1")).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Hello there.", "Me"), default);
        // Unassigned (no matter) session: legacy meta.json predates matterIds, so AllowUnassigned
        // is what makes it visible - proves the read-only guarantee holds on the consent path too.
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowUnassigned = true }, default);

        var before = Directory.EnumerateFiles(sessionDir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllBytes);

        await Corpus().ReadTranscriptAsync("s1", null, null, null, 0, null, default);

        var after = Directory.EnumerateFiles(sessionDir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => f, File.ReadAllBytes);
        Assert.Equal(before.Keys.OrderBy(x => x), after.Keys.OrderBy(x => x));
        foreach (var (file, bytes) in before)
            Assert.Equal(bytes, after[file]);
    }
}
