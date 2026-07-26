using LocalScribe.Core.Assistant;
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;

public sealed class McpCorpusSemanticTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FixedEmbeddings(string method) : IMcpEmbeddingProvider, IEmbeddingClient
    {
        public Task<(IEmbeddingClient, string)> GetAsync(CancellationToken ct)
            => Task.FromResult(((IEmbeddingClient)this, method));
        public Task<EmbeddingBatch> EmbedAsync(string kind, IReadOnlyList<string> texts, CancellationToken ct)
            => Task.FromResult(new EmbeddingBatch(texts.Select(_ => new[] { 1f, 0f }).ToList(), method));
        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class UnavailableEmbeddings : IMcpEmbeddingProvider
    {
        public Task<(IEmbeddingClient, string)> GetAsync(CancellationToken ct)
            => throw new McpToolException("semantic unavailable: embedding model not installed", "error");
    }

    private McpCorpus Corpus(IMcpEmbeddingProvider embeddings)
    {
        var settings = new Settings { StorageRoot = _root };
        var time = new ManualUtcTimeProvider(T0.AddDays(1));
        return new McpCorpus(Paths, settings, time, new McpConsentStore(Paths),
            new McpLexicalCatalog(Paths, settings, time), new SemanticIndexStore(Paths),
            new MatterStore(Paths.MattersDir), new SummaryStore(Paths), embeddings);
    }

    private async Task<SearchSessionEntry> SeedSessionWithSidecarAsync(string method,
        bool staleStamps = false)
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Settlement call", "m-001", T0, "webex",
            "we agreed the settlement figure");
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);
        var entry = await SearchIndexBuilder.BuildEntryAsync(Paths,
            new Settings { StorageRoot = _root }, TimeProvider.System, "s1", default);
        var stamps = staleStamps ? new SearchFreshnessStamps { TranscriptTicks = 1 } : entry.Stamps;
        await new SemanticIndexStore(Paths).SaveAsync("s1", new SemanticSidecar(
            method, entry.VersionId, stamps, 2,
            [new SemanticChunk(0, 0, 0, 0, 1000, "we agreed the settlement figure")],
            [new[] { 1f, 0f }]), default);
        return entry;
    }

    [Fact]
    public async Task Semantic_hit_comes_back_with_anchor_score_and_full_coverage()
    {
        await SeedSessionWithSidecarAsync("fake@2");
        var r = await Corpus(new FixedEmbeddings("fake@2"))
            .SearchSemanticAsync("settlement number", null, null, null, null, 10, default);
        var hit = Assert.Single(r.Hits);
        Assert.Equal("s1", hit.SessionId);
        Assert.Equal(0, hit.StartSeq);
        Assert.True(hit.Score > 0.9f); // identical unit vectors
        Assert.Equal(new McpCoverage(1, 1, 0), r.Coverage);
    }

    [Fact]
    public async Task Stale_sidecar_counts_in_coverage_honesty()
    {
        await SeedSessionWithSidecarAsync("fake@2", staleStamps: true);
        var r = await Corpus(new FixedEmbeddings("fake@2"))
            .SearchSemanticAsync("settlement number", null, null, null, null, 10, default);
        Assert.Equal(new McpCoverage(1, 1, 1), r.Coverage); // covered but stale
    }

    [Fact]
    public async Task Method_mismatch_is_stale_not_a_match_source()
    {
        await SeedSessionWithSidecarAsync("other-model@2");
        var r = await Corpus(new FixedEmbeddings("fake@2"))
            .SearchSemanticAsync("settlement number", null, null, null, null, 10, default);
        Assert.Empty(r.Hits); // incomparable vectors are never scanned
        Assert.Equal(new McpCoverage(1, 1, 1), r.Coverage);
    }

    [Fact]
    public async Task Non_allowlisted_sessions_never_reach_the_ranker_or_coverage()
    {
        await SeedSessionWithSidecarAsync("fake@2");
        TestSessionSeeder.EnsureMatter(Paths, "m-002", "Estate of Brown");
        TestSessionSeeder.WriteBasicSession(Paths, "s2", "Estate call", "m-002", T0.AddHours(1),
            "webex", "estate settlement talk");
        var r = await Corpus(new FixedEmbeddings("fake@2"))
            .SearchSemanticAsync("settlement", null, null, null, null, 10, default);
        Assert.All(r.Hits, h => Assert.Equal("s1", h.SessionId));
        Assert.Equal(1, r.Coverage.SessionsEligible); // s2 invisible even to the denominator
    }

    [Fact]
    public async Task Missing_helper_surfaces_as_clear_error()
    {
        await SeedSessionWithSidecarAsync("fake@2");
        var ex = await Assert.ThrowsAsync<McpToolException>(() =>
            Corpus(new UnavailableEmbeddings())
                .SearchSemanticAsync("settlement", null, null, null, null, 10, default));
        Assert.StartsWith("semantic unavailable", ex.Message);
    }
}
