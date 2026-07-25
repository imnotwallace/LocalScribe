using LocalScribe.Core.Assistant;
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;

public sealed class McpCorpusSearchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
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

    private async Task SeedTwoMattersAsync()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones", "2026-14");
        TestSessionSeeder.EnsureMatter(Paths, "m-002", "Estate of Brown");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Settlement call", "m-001", T0, "webex",
            "we agreed the settlement figure is forty thousand");
        TestSessionSeeder.WriteBasicSession(Paths, "s2", "Estate call", "m-002", T0.AddHours(2), "webex",
            "the settlement of the estate remains open");
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"], UpdatedUtc = T0 }, default);
    }

    [Fact]
    public async Task Search_denied_when_consent_absent()
    {
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", null, T0, "webex", "hello");
        var ex = await Assert.ThrowsAsync<McpToolException>(
            () => Corpus().SearchAsync("hello", null, null, null, null, 10, default));
        Assert.Equal("denied", ex.Outcome);
        Assert.Equal(McpConsentFilter.NotEnabledMessage, ex.Message);
    }

    [Fact]
    public async Task Search_only_sees_allowlisted_matters()
    {
        await SeedTwoMattersAsync();
        var r = await Corpus().SearchAsync("settlement", null, null, null, null, 10, default);
        Assert.All(r.Hits, h => Assert.Equal("s1", h.SessionId));
        Assert.True(r.TotalHits >= 1);
        Assert.Contains("settlement", r.Hits[0].Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, r.Hits[0].Seq);
    }

    [Fact]
    public async Task Search_limit_caps_flattened_hits()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", "m-001", T0, "webex",
            "alpha beta", "alpha gamma", "alpha delta");
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);
        var r = await Corpus().SearchAsync("alpha", null, null, null, null, 2, default);
        Assert.Equal(2, r.Hits.Count);
        Assert.Equal(3, r.TotalHits); // honest total even when capped
    }

    [Fact]
    public async Task ListSessions_filters_facets_and_flags_summaries()
    {
        await SeedTwoMattersAsync();
        Directory.CreateDirectory(Paths.AssistantDir("s1"));
        await File.WriteAllTextAsync(Paths.SummariesJson("s1"), "{}");
        var r = await Corpus().ListSessionsAsync(null, null, null, null, 0, 20, default);
        var s = Assert.Single(r.Sessions);
        Assert.Equal("s1", s.SessionId);
        Assert.True(s.HasSummary);
    }

    [Fact]
    public async Task ListMatters_shows_only_allowlisted()
    {
        await SeedTwoMattersAsync();
        var r = await Corpus().ListMattersAsync(default);
        var m = Assert.Single(r.Matters);
        Assert.Equal("m-001", m.Id);
        Assert.Equal("Smith v Jones", m.Name);
    }
}
