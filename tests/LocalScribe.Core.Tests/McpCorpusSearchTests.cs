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
            "the settlement of the estate remains open",
            "another settlement matter to review",
            "final settlement discussion for the estate");
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
        // s2 (non-visible, matter m-002) is seeded with strictly MORE matches (3 lines) than
        // s1 (visible, matter m-001, 1 line) so a filter-after-ranking implementation - which
        // would run the engine over both sessions and only filter the RESULT list afterwards -
        // is caught: its TotalHits would reflect s2's hit count (or rank s2 above s1), diverging
        // from the exact total asserted below. A consent-before-ranking implementation never
        // lets s2 reach the engine at all, so the total can only ever be s1's single hit.
        await SeedTwoMattersAsync();
        var r = await Corpus().SearchAsync("settlement", null, null, null, null, 10, default);
        Assert.All(r.Hits, h => Assert.Equal("s1", h.SessionId));
        Assert.Equal(1, r.TotalHits);
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
    public async Task Session_listing_order_is_stable_for_identical_timestamps()
    {
        // Three sessions sharing one StartedAtUtc: the catalog rebuilds its dictionary from
        // Directory.EnumerateDirectories on every refresh, so without a tie-breaker an MCP client
        // paging with offset/limit across a refresh could see a session twice or miss one.
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s3", "Call three", "m-001", T0, "webex", "hello");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call one", "m-001", T0, "webex", "hello");
        TestSessionSeeder.WriteBasicSession(Paths, "s2", "Call two", "m-001", T0, "webex", "hello");
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);

        // Separate McpCorpus/catalog instances per call simulate a catalog refresh between pages.
        var first = await Corpus().ListSessionsAsync(null, null, null, null, 0, 20, default);
        var second = await Corpus().ListSessionsAsync(null, null, null, null, 0, 20, default);

        var firstIds = first.Sessions.Select(s => s.SessionId).ToList();
        var secondIds = second.Sessions.Select(s => s.SessionId).ToList();
        Assert.Equal(firstIds, secondIds);
        Assert.Equal(new[] { "s1", "s2", "s3" }, firstIds);
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

    /// <summary>ListMatters' session_count must agree with what list_sessions actually shows for
    /// that matter, not the corpus-wide index count: session B is tagged with BOTH m-001 (allowed)
    /// and m-002 (not allowed), so the multi-matter consent rule (McpConsentFilter.SessionVisible)
    /// hides it entirely - it must not be counted by list_matters either, or its existence leaks
    /// through the count discrepancy (the client would see session_count=2 vs list_sessions total=1
    /// and infer a hidden session exists).</summary>
    [Fact]
    public async Task ListMatters_session_count_matches_list_sessions_and_excludes_multi_matter_hidden()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.EnsureMatter(Paths, "m-002", "Estate of Brown");
        TestSessionSeeder.WriteBasicSession(Paths, "sA", "Call A", "m-001", T0, "webex", "hello");
        TestSessionSeeder.WriteBasicSession(Paths, "sB", "Call B", "m-001", T0.AddHours(1), "webex", "hello");
        // sB is ALSO tagged m-002 - WriteBasicSession only accepts one matter id, so re-save meta
        // directly to tag both (this is the multi-matter fixture the consent rule exists for).
        await new MetadataStore(Paths.MetaJson("sB")).SaveAsync(
            new SessionMeta { Title = "Call B", MatterIds = ["m-001", "m-002"] }, default);
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);

        var matters = await Corpus().ListMattersAsync(default);
        var sessions = await Corpus().ListSessionsAsync("m-001", null, null, null, 0, 20, default);
        var m001 = Assert.Single(matters.Matters, m => m.Id == "m-001");

        Assert.Equal(1, sessions.Total);
        Assert.Equal(sessions.Total, m001.SessionCount);
        Assert.NotEqual(2, m001.SessionCount);   // corpus-wide (index) count would be 2 - must not leak
    }

    /// <summary>A speaker-name-only hit's snippet is the speaker's first line (unrelated to the
    /// query) and its Seq can be -1 - a client must be able to tell it apart from a real text match
    /// so it never quotes the snippet as if it matched the query, or feeds a -1 seq to
    /// read_transcript. IsSpeakerNameMatch must surface on the DTO for exactly this hit and stay
    /// false for an ordinary text hit.</summary>
    [Fact]
    public async Task Speaker_name_only_hit_is_flagged_and_a_text_hit_is_not()
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", "m-001", T0, "webex",
            "we discussed the contract terms");
        var meta = await new MetadataStore(Paths.MetaJson("s1")).LoadAsync(default);
        await new MetadataStore(Paths.MetaJson("s1")).SaveAsync(
            meta! with { Participants = [new SessionParticipant { Name = "Zephyrine" }] }, default);
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);

        var nameHit = Assert.Single(
            (await Corpus().SearchAsync("Zephyrine", null, null, null, null, 10, default)).Hits);
        Assert.True(nameHit.IsSpeakerNameMatch);

        var textHit = Assert.Single(
            (await Corpus().SearchAsync("contract", null, null, null, null, 10, default)).Hits);
        Assert.False(textHit.IsSpeakerNameMatch);
    }
}
