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

    /// <summary>A machine row reporting a specific (seq, partIndex) - task-5b's RowWith, for the
    /// Seq-0-collision fixtures where the default Rows() helper's implicit seq isn't enough.</summary>
    private static DisplayRow RowWith(int seq, int partIndex, string text, long startMs) => new()
    {
        StartMs = startMs, EndMs = startMs + 1000, DisplayName = "Alice", Text = text,
        Segments = [new RowSegment(seq, TranscriptSource.Local, startMs, startMs + 1000, text, text,
            false, false, PartIndex: partIndex)]
    };

    /// <summary>A human-authored row (design 2026-08-11 section 2.3) - task-5b's InsertedRowWith.
    /// Mirrors TranscriptProjection's real shape: placeholder Seq 0/PartIndex 0, EndMs == StartMs,
    /// TranscriptSource.System, identity carried on InsertionId not Seq.</summary>
    private static DisplayRow InsertedRowWith(string insertionId, string text, long startMs) => new()
    {
        StartMs = startMs, EndMs = startMs, DisplayName = "Jane", Text = text,
        Segments = [new RowSegment(0, TranscriptSource.System, startMs, startMs, text, text,
            false, false, InsertionId: insertionId)]
    };

    /// <summary>Drives ExcerptContextBuilder.Build off a single canned SearchHit for the given term,
    /// rather than deriving hits from row text - task-5b's BuildExcerpt. Needed because the fixture
    /// deliberately puts a colliding (Seq, PartIndex) pair on TWO rows; QueryOver's row-derived hits
    /// can't express "this hit means the inserted row, not the machine one" on their own.</summary>
    private static ExcerptQaContext BuildExcerpt(IReadOnlyList<DisplayRow> rows, SearchHit hit, string term)
    {
        IReadOnlyList<SearchResult> Query(SearchQuery q) => q.Text == term
            ? [new SearchResult(new SearchSessionEntry { SessionId = "s1" }, [hit], 1)]
            : [];
        return ExcerptContextBuilder.Build(term, rows, "s1", Query, s => s.Length / 2);
    }

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

    [Fact]
    public void An_inserted_line_hit_excerpts_the_inserted_row_not_machine_segment_zero()
    {
        // Both rows report Seq 0 / PartIndex 0 - the inserted one because that is its placeholder,
        // the machine one because machine seq genuinely starts at 0. Matching on (Seq, PartIndex)
        // alone returns the machine row and grounds the assistant's citation at the wrong moment.
        //
        // Fixture note (widened past the brief's 2-row sketch): NeighborRadius is 2, so with only
        // two rows total the +-2 window around EITHER index always covers both of them - the
        // Contains/DoesNotContain pair would pass no matter which row FindIndex actually picked,
        // i.e. the test could not fail for the thing it is named after. Three filler rows of
        // unrelated, distinctly-seq'd machine speech separate the two candidate rows by more than
        // 2*NeighborRadius so the window genuinely differs depending on which row was resolved.
        var rows = new[]
        {
            RowWith(0, 0, "Machine words at the start.", startMs: 0),
            RowWith(101, 0, "Ordinary filler line one.", startMs: 10_000),
            RowWith(102, 0, "Ordinary filler line two.", startMs: 20_000),
            RowWith(103, 0, "Ordinary filler line three.", startMs: 30_000),
            InsertedRowWith("i000001", "Authored words much later.", startMs: 45_000),
        };
        var hit = new SearchHit(0, 0, 45_000, "Jane", "Authored words much later.", "authored",
            false, false, InsertionId: "i000001");

        var ctx = BuildExcerpt(rows, hit, term: "authored");

        Assert.Contains("Authored words much later.", ctx.ContextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Machine words at the start.", ctx.ContextBody, StringComparison.Ordinal);
    }

    [Fact]
    public void A_machine_hit_on_seq_zero_excerpts_the_machine_row_not_an_inserted_one()
    {
        // The collision runs both ways: without the InsertionId-is-null half of the guard, a machine
        // hit can match the inserted segment, which also reports Seq 0 / PartIndex 0.
        //
        // Fixture note (same widening as above, and for the same reason): a 2-row array can't
        // discriminate a wrong resolution from a right one once NeighborRadius pulls both rows into
        // every window regardless of the resolved centre. Filler rows push the inserted row and the
        // real machine hit far enough apart that only the correctly-resolved row's window reaches it.
        var rows = new[]
        {
            InsertedRowWith("i000001", "Authored words.", startMs: 0),
            RowWith(201, 0, "Ordinary filler line one.", startMs: 250),
            RowWith(202, 0, "Ordinary filler line two.", startMs: 500),
            RowWith(203, 0, "Ordinary filler line three.", startMs: 750),
            RowWith(0, 0, "Machine words.", startMs: 1_000),
        };
        var hit = new SearchHit(0, 0, 1_000, "Me", "Machine words.", "machine", false, false);

        var ctx = BuildExcerpt(rows, hit, term: "machine");

        Assert.Contains("Machine words.", ctx.ContextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Authored words.", ctx.ContextBody, StringComparison.Ordinal);
    }
}
