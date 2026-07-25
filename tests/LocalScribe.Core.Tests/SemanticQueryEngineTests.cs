using LocalScribe.Core.Search;
using LocalScribe.Core.Search.Semantic;

public sealed class SemanticQueryEngineTests
{
    private static SearchSessionEntry Meta(string id, string matterId = "M1",
        string app = "Webex", int day = 1) => new()
    {
        SessionId = id, Title = "T-" + id, MatterIds = [matterId], App = app,
        StartedAtUtc = new DateTimeOffset(2026, 7, day, 9, 0, 0, TimeSpan.Zero),
    };

    private static SemanticSidecar Sidecar(params (SemanticChunk Chunk, float[] Vec)[] entries)
        => new("m@2", "v1", new SearchFreshnessStamps(), 2,
            entries.Select(e => e.Chunk).ToList(), entries.Select(e => e.Vec).ToList());

    private static SemanticChunk Chunk(int startSeq, int endSeq, string text = "some words")
        => new(startSeq, 0, startSeq * 1000L, endSeq, endSeq * 1000L + 900, text);

    // query vector [1,0]: score of a chunk = its vector's first component
    private static readonly float[] Query = [1f, 0f];

    [Fact]
    public void Scores_floor_and_orders_by_best_chunk()
    {
        var results = SemanticQueryEngine.Run(Query,
            new Dictionary<string, SearchSessionEntry>
            { ["a"] = Meta("a"), ["b"] = Meta("b") },
            new Dictionary<string, SemanticSidecar>
            {
                ["a"] = Sidecar((Chunk(0, 1), [0.7f, 0.71f]), (Chunk(2, 3), [0.2f, 0.98f])),
                ["b"] = Sidecar((Chunk(0, 1), [0.9f, 0.44f])),
            },
            new SearchQuery("anything"), lexicalResults: []);

        Assert.Equal(2, results.Count);
        Assert.Equal("b", results[0].Session.SessionId);          // 0.9 beats 0.7
        Assert.Equal(0.9f, results[0].BestScore, 2);
        var aHits = results[1].Hits;
        Assert.Single(aHits);                                     // 0.2 chunk is under the 0.55 floor
        Assert.Equal(0, aHits[0].StartSeq);
    }

    [Fact]
    public void Facets_filter_before_scoring()
    {
        var meta = new Dictionary<string, SearchSessionEntry>
        { ["a"] = Meta("a", matterId: "M1"), ["b"] = Meta("b", matterId: "M2") };
        var sidecars = new Dictionary<string, SemanticSidecar>
        { ["a"] = Sidecar((Chunk(0, 1), [1f, 0f])), ["b"] = Sidecar((Chunk(0, 1), [1f, 0f])) };

        var results = SemanticQueryEngine.Run(Query, meta, sidecars,
            new SearchQuery("x", MatterId: "M2"), []);

        Assert.Equal("b", Assert.Single(results).Session.SessionId);
    }

    [Fact]
    public void Session_missing_from_metadata_is_not_searchable()
    {
        var results = SemanticQueryEngine.Run(Query,
            new Dictionary<string, SearchSessionEntry>(),
            new Dictionary<string, SemanticSidecar> { ["ghost"] = Sidecar((Chunk(0, 1), [1f, 0f])) },
            new SearchQuery("x"), []);
        Assert.Empty(results);
    }

    [Fact]
    public void Chunk_covering_a_lexical_hit_seq_is_deduped_but_other_chunks_survive()
    {
        var meta = new Dictionary<string, SearchSessionEntry> { ["a"] = Meta("a") };
        var sidecars = new Dictionary<string, SemanticSidecar>
        { ["a"] = Sidecar((Chunk(0, 5), [1f, 0f]), (Chunk(10, 15), [0.8f, 0.6f])) };
        var lexical = new List<SearchResult>
        {
            new(Meta("a"), [new SearchHit(3, 0, 0, "S", "snip", "term", false, false)], 1),
        };

        var results = SemanticQueryEngine.Run(Query, meta, sidecars, new SearchQuery("x"), lexical);

        var hit = Assert.Single(Assert.Single(results).Hits);     // chunk 0-5 covers seq 3 -> dropped
        Assert.Equal(10, hit.StartSeq);
    }

    [Fact]
    public void Caps_at_MaxChunks_across_all_sessions()
    {
        var entries = Enumerable.Range(0, 60)
            .Select(i => (Chunk(i * 10, i * 10 + 1), new[] { 0.9f, 0.44f })).ToArray();
        var results = SemanticQueryEngine.Run(Query,
            new Dictionary<string, SearchSessionEntry> { ["a"] = Meta("a") },
            new Dictionary<string, SemanticSidecar> { ["a"] = Sidecar(entries) },
            new SearchQuery("x"), []);
        Assert.Equal(SemanticQueryEngine.MaxChunks, results.Sum(r => r.Hits.Count));
    }

    [Fact]
    public void Snippet_truncates_long_chunk_text_and_flattens_newlines()
    {
        var text = "Alice: " + new string('z', 400) + "\nBob: more";
        var results = SemanticQueryEngine.Run(Query,
            new Dictionary<string, SearchSessionEntry> { ["a"] = Meta("a") },
            new Dictionary<string, SemanticSidecar>
            { ["a"] = Sidecar((Chunk(0, 1, text), [1f, 0f])) },
            new SearchQuery("x"), []);
        string snippet = results[0].Hits[0].Snippet;
        Assert.True(snippet.Length <= SemanticQueryEngine.SnippetChars + 1);   // +1 for ellipsis char
        Assert.DoesNotContain('\n', snippet);
    }

    [Fact]
    public void Deterministic_tie_order_by_session_id()
    {
        var meta = new Dictionary<string, SearchSessionEntry> { ["b"] = Meta("b"), ["a"] = Meta("a") };
        var sidecars = new Dictionary<string, SemanticSidecar>
        {
            ["b"] = Sidecar((Chunk(0, 1), [0.8f, 0.6f])),
            ["a"] = Sidecar((Chunk(0, 1), [0.8f, 0.6f])),
        };
        var results = SemanticQueryEngine.Run(Query, meta, sidecars, new SearchQuery("x"), []);
        Assert.Equal(["a", "b"], results.Select(r => r.Session.SessionId));
    }
}
