using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Search;

public class ExcerptContextBuilderTests
{
    private static DisplayRow Row(int seq, long startMs, string text) => new()
    {
        StartMs = startMs, EndMs = startMs + 1000, DisplayName = "Alice", Text = text,
        Segments = [new RowSegment(seq, TranscriptSource.Local, startMs, startMs + 1000, text, text, false, false)]
    };

    private static List<DisplayRow> Rows(int count, params (int Index, string Text)[] specials)
    {
        var rows = new List<DisplayRow>();
        for (int i = 0; i < count; i++)
        {
            string text = $"ordinary line number {i}";
            foreach (var s in specials) if (s.Index == i) text = s.Text;
            rows.Add(Row(i, i * 10_000, text));
        }
        return rows;
    }

    /// <summary>Substring-matching stand-in for SearchIndexService.Query, canned to one session.</summary>
    private static Func<SearchQuery, IReadOnlyList<SearchResult>> QueryOver(
        string sessionId, IReadOnlyList<DisplayRow> rows, List<string>? queried = null)
        => q =>
        {
            queried?.Add(q.Text);
            var hits = new List<SearchHit>();
            foreach (var row in rows)
            {
                if (row.IsMarker || !row.Text.Contains(q.Text, StringComparison.OrdinalIgnoreCase)) continue;
                var seg = row.Segments[0];
                hits.Add(new SearchHit(seg.Seq, seg.PartIndex, row.StartMs, "Alice", row.Text, q.Text, false, false));
            }
            return hits.Count == 0 ? []
                : [new SearchResult(new SearchSessionEntry { SessionId = sessionId }, hits, hits.Count)];
        };

    [Fact]
    public void Single_hit_includes_two_neighbors_each_side()
    {
        var rows = Rows(10, (4, "they discussed the settlement amount at length"));
        var ex = ExcerptContextBuilder.Build("what was the settlement figure", rows, "s1",
            QueryOver("s1", rows), s => s.Length / 2);
        Assert.False(ex.NoMatches);
        Assert.Equal(5, ex.IncludedRows.Count);                                  // rows 2..6
        Assert.Equal(new long[] { 20_000, 30_000, 40_000, 50_000, 60_000 },
            ex.IncludedRows.Select(r => r.StartMs));
        Assert.DoesNotContain(ExcerptContextBuilder.GapMarker, ex.ContextBody);
        Assert.Contains("[00:00:40] Alice: they discussed the settlement amount at length", ex.ContextBody);
        Assert.Equal(ExcerptContextBuilder.DisclosureText, ex.Disclosure);
        Assert.Equal(32768, ex.CtxTokens);
    }

    [Fact]
    public void Distant_hits_render_chronologically_with_a_gap_marker()
    {
        var rows = Rows(20, (3, "the alpha clause was disputed"), (15, "the beta clause was accepted"));
        var ex = ExcerptContextBuilder.Build("alpha beta clause", rows, "s1",
            QueryOver("s1", rows), s => s.Length / 2);
        Assert.Equal(10, ex.IncludedRows.Count);                                 // 1..5 and 13..17
        Assert.Contains(ExcerptContextBuilder.GapMarker, ex.ContextBody);
        Assert.True(ex.ContextBody.IndexOf("alpha clause", StringComparison.Ordinal)
            < ex.ContextBody.IndexOf("beta clause", StringComparison.Ordinal));  // chronological
    }

    [Fact]
    public void Hits_from_other_sessions_are_ignored()
    {
        var rows = Rows(10, (4, "the settlement amount"));
        var ex = ExcerptContextBuilder.Build("settlement", rows, "s1",
            QueryOver("OTHER-session", rows), s => s.Length / 2);
        Assert.True(ex.NoMatches);
        Assert.Equal("", ex.ContextBody);
        Assert.Empty(ex.IncludedRows);
    }

    [Fact]
    public void Budget_keeps_the_best_ranked_window_when_nothing_more_fits()
    {
        // Row 4 matches BOTH terms (rank 1); row 15 matches one. A huge token estimate rejects
        // every expansion, but the first-ranked window is ALWAYS kept.
        var rows = Rows(20, (4, "alpha beta together here"), (15, "only beta here"));
        var ex = ExcerptContextBuilder.Build("alpha beta", rows, "s1",
            QueryOver("s1", rows), s => 1_000_000);
        Assert.False(ex.NoMatches);
        Assert.Equal(5, ex.IncludedRows.Count);                                  // rows 2..6 only
        Assert.Equal(20_000, ex.IncludedRows[0].StartMs);
    }

    [Fact]
    public void Short_question_words_are_never_queried()
    {
        var rows = Rows(10, (4, "the settlement amount"));
        var queried = new List<string>();
        ExcerptContextBuilder.Build("is at of settlement", rows, "s1",
            QueryOver("s1", rows, queried), s => s.Length / 2);
        Assert.Equal(new[] { "settlement" }, queried);                           // 2-char terms dropped
    }

    [Fact]
    public void Zero_hits_is_an_honest_no_matches_result()
    {
        var rows = Rows(10);
        var ex = ExcerptContextBuilder.Build("zeppelin", rows, "s1",
            QueryOver("s1", rows), s => s.Length / 2);
        Assert.True(ex.NoMatches);
    }
}
