using LocalScribe.Core.Search;

public class SearchQueryEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);

    private static SearchSessionEntry Session(string id, DateTimeOffset started, SearchLine[] lines,
        string app = "Webex", string[]? matterIds = null, string[]? participants = null)
        => new()
        {
            SessionId = id, Title = "T-" + id, MatterIds = matterIds ?? [],
            StartedAtUtc = started, App = app, Participants = participants ?? [],
            VersionId = "v1", Lines = lines,
        };

    [Fact]
    public void Empty_or_whitespace_query_returns_nothing()
    {
        var s = Session("s-1", T0, [new SearchLine(0, 0, 0, "anything", null, "Sam")]);
        Assert.Empty(SearchQueryEngine.Run([s], new SearchQuery("")));
        Assert.Empty(SearchQueryEngine.Run([s], new SearchQuery("   ")));
    }

    [Fact]
    public void Single_term_matches_case_insensitively_over_corrected_text()
    {
        var s = Session("s-1", T0,
            [new SearchLine(4, 0, 61_000, "We discussed the RETAINER agreement.", null, "Sam")]);
        var r = Assert.Single(SearchQueryEngine.Run([s], new SearchQuery("retainer")));
        Assert.Equal("s-1", r.Session.SessionId);
        Assert.Equal(1, r.HitCount);
        var hit = Assert.Single(r.Hits);
        Assert.Equal(4, hit.Seq);
        Assert.Equal(61_000L, hit.StartMs);
        Assert.Equal("Sam", hit.Speaker);
        Assert.False(hit.MatchesOriginalOnly);
        Assert.False(hit.IsSpeakerNameMatch);
        Assert.Contains("RETAINER", hit.Snippet);
        Assert.Equal("retainer", hit.MatchedTerm);
    }

    [Fact]
    public void Multi_word_query_is_AND_across_the_session()
    {
        var both = Session("s-both", T0, [
            new SearchLine(0, 0, 0, "the retainer was signed", null, "Sam"),
            new SearchLine(1, 0, 5_000, "the hearing is on Monday", null, "Jane"),
        ]);
        var onlyOne = Session("s-one", T0.AddHours(1),
            [new SearchLine(0, 0, 0, "the retainer was signed", null, "Sam")]);
        var r = Assert.Single(SearchQueryEngine.Run([both, onlyOne], new SearchQuery("retainer hearing")));
        Assert.Equal("s-both", r.Session.SessionId);
        Assert.Equal(2, r.Hits.Count);                      // one hit per matched line, document order
        Assert.Equal(0, r.Hits[0].Seq);
        Assert.Equal(1, r.Hits[1].Seq);
    }

    [Fact]
    public void Results_rank_by_hit_count_then_recency()
    {
        var twoHitsOld = Session("s-old2", T0, [
            new SearchLine(0, 0, 0, "acme called", null, "Sam"),
            new SearchLine(1, 0, 9_000, "acme called back", null, "Sam"),
        ]);
        var oneHitNew = Session("s-new1", T0.AddDays(2),
            [new SearchLine(0, 0, 0, "acme wrote", null, "Sam")]);
        var oneHitOld = Session("s-old1", T0.AddDays(1),
            [new SearchLine(0, 0, 0, "acme again", null, "Sam")]);
        var results = SearchQueryEngine.Run([oneHitOld, oneHitNew, twoHitsOld], new SearchQuery("acme"));
        Assert.Equal(new[] { "s-old2", "s-new1", "s-old1" },
            results.Select(r => r.Session.SessionId).ToArray());
    }

    [Fact]
    public void Original_text_only_matches_are_labelled_and_snippet_comes_from_the_original()
    {
        var s = Session("s-corr", T0,
            [new SearchLine(1, 0, 2_000, "the corrected words", "the orignal words", "Sam")]);

        var original = Assert.Single(
            Assert.Single(SearchQueryEngine.Run([s], new SearchQuery("orignal"))).Hits);
        Assert.True(original.MatchesOriginalOnly);
        Assert.Contains("orignal", original.Snippet);

        var corrected = Assert.Single(
            Assert.Single(SearchQueryEngine.Run([s], new SearchQuery("corrected"))).Hits);
        Assert.False(corrected.MatchesOriginalOnly);
        Assert.Contains("corrected", corrected.Snippet);
    }

    [Fact]
    public void Speaker_name_only_matches_snippet_the_speakers_first_line()
    {
        var s = Session("s-spk", T0, [
            new SearchLine(0, 0, 0, "good morning", null, "Jane Doe"),
            new SearchLine(2, 0, 7_000, "the first jane line is above", null, "Sam"),
        ], participants: ["Sam", "Jane Doe", "Silent Bob"]);

        // "jane" is in line 2's TEXT and in a speaker name: the text hit wins; no speaker hit added.
        var textHit = Assert.Single(Assert.Single(
            SearchQueryEngine.Run([s], new SearchQuery("jane"))).Hits);
        Assert.False(textHit.IsSpeakerNameMatch);
        Assert.Equal(2, textHit.Seq);

        // "doe" appears ONLY as a speaker name: one speaker hit, snippeted with Jane's first line.
        var spkHit = Assert.Single(Assert.Single(
            SearchQueryEngine.Run([s], new SearchQuery("doe"))).Hits);
        Assert.True(spkHit.IsSpeakerNameMatch);
        Assert.Equal("Jane Doe", spkHit.Speaker);
        Assert.Equal(0, spkHit.Seq);
        Assert.Contains("good morning", spkHit.Snippet);

        // A named participant who never spoke still satisfies the term; Seq -1 = nothing to open to.
        var silent = Assert.Single(Assert.Single(
            SearchQueryEngine.Run([s], new SearchQuery("silent"))).Hits);
        Assert.True(silent.IsSpeakerNameMatch);
        Assert.Equal(-1, silent.Seq);
        Assert.Equal("", silent.Snippet);
    }

    [Fact]
    public void Facets_filter_by_matter_date_range_and_app()
    {
        var a = Session("s-a", T0, [new SearchLine(0, 0, 0, "acme", null, "Sam")],
            app: "Webex", matterIds: ["M-1"]);
        var b = Session("s-b", T0.AddDays(5), [new SearchLine(0, 0, 0, "acme", null, "Sam")],
            app: "Teams", matterIds: ["M-2"]);

        Assert.Equal(new[] { "s-a" }, SearchQueryEngine.Run([a, b],
            new SearchQuery("acme", MatterId: "M-1")).Select(r => r.Session.SessionId).ToArray());
        Assert.Equal(new[] { "s-b" }, SearchQueryEngine.Run([a, b],
            new SearchQuery("acme", App: "teams")).Select(r => r.Session.SessionId).ToArray());
        Assert.Equal(new[] { "s-b" }, SearchQueryEngine.Run([a, b],
            new SearchQuery("acme", FromUtc: T0.AddDays(1), ToUtc: T0.AddDays(6)))
            .Select(r => r.Session.SessionId).ToArray());
        Assert.Empty(SearchQueryEngine.Run([a, b], new SearchQuery("acme", ToUtc: T0)));   // exclusive upper
    }

    [Fact]
    public void Snippet_is_60_chars_around_the_match_with_ellipses()
    {
        string text = new string('x', 70) + "needle" + new string('y', 70);
        string s = SearchQueryEngine.Snippet(text, 70, 6);
        Assert.StartsWith("…", s);
        Assert.EndsWith("…", s);
        Assert.Contains("needle", s);
        Assert.Equal(1 + 60 + 6 + 60 + 1, s.Length);        // ellipsis + radius + match + radius + ellipsis

        Assert.Equal("hello needle world", SearchQueryEngine.Snippet("hello needle world", 6, 6));
    }
}
