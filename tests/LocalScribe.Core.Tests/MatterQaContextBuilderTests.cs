using LocalScribe.Core.Assistant;

public class MatterQaContextBuilderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);

    private static MatterSummarySource Src(string id, int daysAgo, string? summary, bool stale = false)
        => new(id, "Session " + id, T0.AddDays(-daysAgo), summary, stale);

    [Fact]
    public void Builds_newest_first_with_missing_and_stale_disclosed()
    {
        var sources = new[]
        {
            Src("old", 10, "Older summary: the retainer was signed [00:10:00]"),
            Src("new", 1, "Newest summary: the parties agreed to settle [00:01:05]", stale: true),
            Src("none", 5, null),
        };
        var ctx = MatterQaContextBuilder.Build(sources, s => s.Length / 2);
        Assert.Equal(new[] { "new", "old" }, ctx.IncludedSessionIds);        // newest first
        Assert.Empty(ctx.OmittedSessionIds);
        Assert.Equal(new[] { "none" }, ctx.MissingSummarySessionIds);
        int newAt = ctx.ContextBody.IndexOf("Newest summary", StringComparison.Ordinal);
        int oldAt = ctx.ContextBody.IndexOf("Older summary", StringComparison.Ordinal);
        Assert.True(newAt >= 0 && oldAt > newAt);
        Assert.Contains("## Session new (2026-06-30)", ctx.ContextBody);     // per-session header
        Assert.Contains(MatterQaContextBuilder.StaleNote, ctx.ContextBody);  // stale disclosed in-context
        Assert.Equal(8192, ctx.CtxTokens);                                   // per-job ladder pick
    }

    [Fact]
    public void Budget_cut_is_a_strict_newest_first_prefix()
    {
        // Each summary ~30k estimated tokens (s => s.Length with 30k chars): the budget
        // (int)(32768*0.8)-1024 = 25190 holds only the first. The cut point and everything
        // older is OMITTED - explicit, never silent (design 7.5).
        string big = new string('x', 30_000);
        var sources = new[] { Src("a", 1, big), Src("b", 2, big), Src("c", 3, big) };
        var ctx = MatterQaContextBuilder.Build(sources, s => s.Length);
        Assert.Equal(new[] { "a" }, ctx.IncludedSessionIds);                 // first ALWAYS included
        Assert.Equal(new[] { "b", "c" }, ctx.OmittedSessionIds);
        Assert.Empty(ctx.MissingSummarySessionIds);
    }

    [Fact]
    public void Matter_claim_verifies_and_navigates_when_the_cited_time_is_unique()
    {
        var included = new[]
        {
            Src("a", 1, "The parties agreed to settle for ten thousand dollars [00:01:05]"),
            Src("b", 2, "The retainer was signed on Monday [00:10:00]"),
        };
        var v = MatterCitationValidator.Validate(
            "- The parties agreed to settle for ten thousand dollars [00:01:05]", included);
        var chip = Assert.Single(Assert.Single(v.Lines).Chips);
        Assert.True(chip.Verified);
        Assert.Equal("a", chip.SessionId);
        Assert.Equal(-1, chip.Seq);                        // matter scope: open, no scroll (v1)
        Assert.Equal(0, v.UnverifiableCount);
    }

    [Fact]
    public void Ambiguous_cited_time_verifies_but_does_not_navigate()
    {
        var included = new[]
        {
            Src("a", 1, "The parties agreed to settle for ten thousand dollars [00:01:05]"),
            Src("b", 2, "They agreed to settle for ten thousand dollars promptly [00:01:05]"),
        };
        var v = MatterCitationValidator.Validate(
            "The parties agreed to settle for ten thousand dollars [00:01:05]", included);
        var chip = Assert.Single(v.Lines[0].Chips);
        Assert.True(chip.Verified);
        Assert.Null(chip.SessionId);                       // ambiguous across sessions
    }

    [Fact]
    public void Unknown_cited_time_is_flagged()
    {
        var included = new[] { Src("a", 1, "The parties agreed to settle [00:01:05]") };
        var v = MatterCitationValidator.Validate("The parties agreed to settle [00:59:59]", included);
        Assert.True(v.Lines[0].Unverifiable);
        Assert.Equal("cited time not found in the included summaries", v.Lines[0].Reason);
    }

    // Whole-branch-review test-gap fix (Minor): MatterCitationValidator.cs:40 correctly uses exact
    // `t.Ms == stamp.Ms` (matter-scope resolution = exact ms-EQUALITY, invariant 2), but no prior
    // case here sat 1-2s off a real stamp - so mutating line 40 to a session-style tolerance
    // (`Math.Abs(t.Ms - stamp.Ms) <= 2000`) left the whole suite GREEN. The claim text below is
    // IDENTICAL to the summary's own text (so a tolerant mutation's fuzzy-match would pass) -
    // only the ms-equality check can still flag the 1-second-off (1000ms) cited stamp.
    [Fact]
    public void Matter_citation_one_second_off_a_summary_stamp_is_flagged()
    {
        var included = new[]
            { Src("a", 1, "The parties agreed to settle for ten thousand dollars [00:01:05]") };
        var v = MatterCitationValidator.Validate(
            "The parties agreed to settle for ten thousand dollars [00:01:06]", included);
        Assert.True(v.Lines[0].Unverifiable);
        Assert.Equal("cited time not found in the included summaries", v.Lines[0].Reason);
    }

    [Fact]
    public void Mismatched_claim_text_is_flagged_not_dropped()
    {
        var included = new[] { Src("a", 1, "The parties agreed to settle for ten thousand dollars [00:01:05]") };
        var v = MatterCitationValidator.Validate("The weather was rainy on Tuesday [00:01:05]", included);
        Assert.True(v.Lines[0].Unverifiable);
        Assert.Equal("text does not match the cited summary", v.Lines[0].Reason);
        Assert.Equal("The weather was rainy on Tuesday", v.Lines[0].Text);
    }

    [Fact]
    public void Stamp_bearing_header_line_is_validated_in_matter_scope()
    {
        // Cross-task seam (same fix as CitationValidator, Task 2): SplitAnswer marks a
        // '#'-prefixed line IsClaim=false purely on the markdown-header rule, even when it
        // carries a valid [HH:MM:SS] stamp. Gating validation on IsClaim alone would let a
        // factual claim hidden behind a header prefix bypass matter-scope citation checking
        // entirely - so a stamp-bearing header line must still be validated, not silently
        // skipped. The cited time here is NOT in any included summary.
        var included = new[] { Src("a", 1, "The parties agreed to settle [00:01:05]") };
        var v = MatterCitationValidator.Validate("# Update at [00:59:59]", included);
        var line = Assert.Single(v.Lines);
        Assert.True(line.Unverifiable);
        Assert.Equal("cited time not found in the included summaries", line.Reason);
        Assert.Equal("# Update at", line.Text);             // preserved - went through validation
    }
}
