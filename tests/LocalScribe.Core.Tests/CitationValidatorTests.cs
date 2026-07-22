using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

public class CitationValidatorTests
{
    private static DisplayRow Row(int seq, long startMs, long endMs, string name, string text) => new()
    {
        StartMs = startMs, EndMs = endMs, DisplayName = name, Text = text,
        Segments = [new RowSegment(seq, TranscriptSource.Remote, startMs, endMs, text, text, false, false)]
    };

    private static DisplayRow Marker(long startMs, string text)
        => new() { IsMarker = true, StartMs = startMs, EndMs = startMs, Text = text };

    [Fact]
    public void Verified_claim_resolves_and_carries_the_seq_for_click_through()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var v = CitationValidator.Validate(
            "- The parties agreed to settle for ten thousand dollars [00:01:05]", rows, "s1");
        var line = Assert.Single(v.Lines);
        Assert.True(line.IsClaim);
        Assert.False(line.Unverifiable);
        Assert.Equal(0, v.UnverifiableCount);
        var chip = Assert.Single(line.Chips);
        Assert.True(chip.Verified);
        Assert.Equal("s1", chip.SessionId);
        Assert.Equal(3, chip.Seq);
        Assert.Equal("thousand", chip.NavTerm);   // longest claim word (>= 4 chars) found in the row text
    }

    [Fact]
    public void Tolerance_is_exactly_two_seconds_around_the_row_start()
    {
        var rows = new[] { Row(1, 65_000, 66_500, "Alice", "We agreed to settle for ten thousand dollars") };
        var ok = CitationValidator.Validate(
            "The parties agreed to settle for ten thousand dollars [00:01:07]", rows, "s1");
        Assert.False(ok.Lines[0].Unverifiable);                       // 67s vs 65s start = +2000 ms, inside
        var bad = CitationValidator.Validate(
            "The parties agreed to settle for ten thousand dollars [00:01:08]", rows, "s1");
        Assert.True(bad.Lines[0].Unverifiable);                       // +3000 ms and past EndMs
        Assert.Equal("cited time not found in the record", bad.Lines[0].Reason);
        Assert.Equal(1, bad.UnverifiableCount);
    }

    [Fact]
    public void Stamp_inside_a_long_turn_resolves()
    {
        var rows = new[] { Row(5, 10_000, 30_000, "Bob", "We agreed to settle for ten thousand dollars and then talked about the schedule") };
        var v = CitationValidator.Validate("They agreed to settle for ten thousand dollars [00:00:20]", rows, "s1");
        Assert.False(v.Lines[0].Unverifiable);                        // 20s is inside [10s, 30s]
        Assert.Equal(5, v.Lines[0].Chips[0].Seq);
    }

    [Fact]
    public void Resolved_but_mismatched_text_is_flagged_not_dropped()
    {
        var rows = new[] { Row(1, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var v = CitationValidator.Validate("The weather was rainy on Tuesday [00:01:05]", rows, "s1");
        var line = Assert.Single(v.Lines);
        Assert.True(line.Unverifiable);
        Assert.Equal("text does not match the cited segment", line.Reason);
        Assert.Equal("The weather was rainy on Tuesday", line.Text);  // NEVER dropped (locked rule)
        Assert.False(line.Chips[0].Verified);
        Assert.Null(line.Chips[0].SessionId);
        Assert.Equal(1, v.UnverifiableCount);
    }

    [Fact]
    public void Claim_without_any_citation_is_flagged()
    {
        var rows = new[] { Row(1, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var v = CitationValidator.Validate("The parties reached an agreement", rows, "s1");
        Assert.True(v.Lines[0].Unverifiable);
        Assert.Equal("no citation", v.Lines[0].Reason);
    }

    [Fact]
    public void Markers_are_never_citation_targets()
    {
        var rows = new[] { Marker(65_000, "microphone muted") };
        var v = CitationValidator.Validate("The microphone was muted [00:01:05]", rows, "s1");
        Assert.True(v.Lines[0].Unverifiable);
        Assert.Equal("cited time not found in the record", v.Lines[0].Reason);
    }

    [Fact]
    public void Short_reply_verifies_via_whole_string_similarity()
    {
        // ContainmentSimilarity floors out below 12 chars / 3 tokens (by design); the
        // NormalizedSimilarity path must cover a genuine short extract.
        var rows = new[] { Row(7, 5_000, 5_400, "Bob", "Yes.") };
        var v = CitationValidator.Validate("Yes [00:00:05]", rows, "s1");
        Assert.False(v.Lines[0].Unverifiable);
        Assert.Equal(7, v.Lines[0].Chips[0].Seq);
    }

    [Fact]
    public void Headers_and_blank_lines_are_never_flagged()
    {
        var rows = new[] { Row(1, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var v = CitationValidator.Validate("# Answer\n\nKey points:", rows, "s1");
        Assert.Equal(0, v.UnverifiableCount);
        Assert.All(v.Lines, l => Assert.False(l.Unverifiable));
    }

    [Fact]
    public void One_good_stamp_verifies_the_claim_and_the_bad_chip_stays_visible()
    {
        var rows = new[] { Row(3, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var v = CitationValidator.Validate(
            "The parties agreed to settle for ten thousand dollars [00:59:59] [00:01:05]", rows, "s1");
        var line = Assert.Single(v.Lines);
        Assert.False(line.Unverifiable);
        Assert.Equal(2, line.Chips.Count);
        Assert.False(line.Chips[0].Verified);                         // the phantom stamp stays, marked
        Assert.True(line.Chips[1].Verified);
    }

    [Fact]
    public void Stamp_bearing_header_line_is_validated_not_silently_unflagged()
    {
        // Cross-task seam (Task 1 review): SplitAnswer classifies a '#'-prefixed line as
        // IsClaim=false (markdown-header rule) EVEN IF it carries a valid [HH:MM:SS] stamp
        // (SplitAnswer still populates that line's Stamps). If Validate only checked IsClaim,
        // a factual claim hidden behind a '#' prefix would silently bypass citation checking -
        // violating "unverifiable claims are FLAGGED, never dropped". Validate must subject any
        // STAMP-BEARING line to validation (parts.IsClaim || parts.Stamps.Count > 0), while still
        // reporting SplitAnswer's real IsClaim (false here) rather than faking it true.
        var rows = new[] { Row(9, 500_000, 503_000, "Alice", "Completely unrelated content about scheduling") };
        var v = CitationValidator.Validate(
            "#1 The parties agreed to settle for ten thousand dollars [00:01:05]", rows, "s1");
        var line = Assert.Single(v.Lines);
        Assert.False(line.IsClaim);                  // SplitAnswer's classification is NOT faked
        Assert.True(line.Unverifiable);               // but it WAS validated, not silently skipped
        Assert.Equal("cited time not found in the record", line.Reason);
        Assert.Contains("ten thousand dollars", line.Text);   // claim text preserved, not dropped
        var chip = Assert.Single(line.Chips);
        Assert.False(chip.Verified);
        Assert.Equal(1, v.UnverifiableCount);
    }

    [Fact]
    public void Genuine_header_with_no_stamp_stays_non_claim_and_unflagged()
    {
        // Discriminator for the fix above: a header line with NO stamp must still be skipped
        // entirely (the plan's Headers_and_blank_lines_are_never_flagged test covers this too,
        // but this pins the boundary explicitly - Stamps.Count == 0 is what keeps a real
        // header/lead-in out of validation, not IsClaim alone).
        var rows = new[] { Row(1, 65_000, 68_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var v = CitationValidator.Validate("# The parties agreed to settle for ten thousand dollars", rows, "s1");
        var line = Assert.Single(v.Lines);
        Assert.False(line.IsClaim);
        Assert.False(line.Unverifiable);
        Assert.Empty(line.Chips);
        Assert.Equal(0, v.UnverifiableCount);
    }

    [Fact]
    public void Row_without_segments_is_not_a_verified_target()
    {
        // Chip-invariant guard: "Verified=false => SessionId=null, Seq=-1" is the locked chip
        // contract. Seq is read from best.Segments[0].Seq, falling back to -1 when Segments is
        // empty - without a guard, a non-marker row with EMPTY Segments that time-and-text
        // matches would emit Verified=true, SessionId=sessionId, Seq=-1: a "verified but
        // un-navigable" chip that breaks click-through. The loader always populates Segments in
        // practice (defense-in-depth), but the evidentiary-safe behavior is: a citation that
        // cannot be made clickable must be FLAGGED, never falsely verified.
        var rows = new[]
        {
            new DisplayRow
            {
                StartMs = 65_000, EndMs = 68_000, DisplayName = "Alice",
                Text = "We agreed to settle for ten thousand dollars",
                Segments = []
            }
        };
        var v = CitationValidator.Validate(
            "The parties agreed to settle for ten thousand dollars [00:01:05]", rows, "s1");
        var line = Assert.Single(v.Lines);
        Assert.True(line.Unverifiable);
        Assert.Equal("cited time not found in the record", line.Reason);
        Assert.Equal("The parties agreed to settle for ten thousand dollars", line.Text);  // preserved
        var chip = Assert.Single(line.Chips);
        Assert.False(chip.Verified);
        Assert.Null(chip.SessionId);
        Assert.Equal(-1, chip.Seq);
        Assert.Equal(1, v.UnverifiableCount);
    }

    [Fact]
    public void Best_scoring_row_wins_when_multiple_rows_resolve_the_same_stamp()
    {
        // Both rows resolve the same stamp (both within +/-2s of it); the STRONGER text match
        // must win the selection - pins the `if (score > bestScore)` branch, previously
        // unexercised because every other test has only one resolving row per stamp.
        var rows = new[]
        {
            Row(1, 64_000, 66_000, "Bob", "completely unrelated chatter about lunch plans"),
            Row(2, 65_500, 67_000, "Alice", "We agreed to settle for ten thousand dollars"),
        };
        var v = CitationValidator.Validate(
            "The parties agreed to settle for ten thousand dollars [00:01:05]", rows, "s1");
        var line = Assert.Single(v.Lines);
        Assert.False(line.Unverifiable);
        var chip = Assert.Single(line.Chips);
        Assert.True(chip.Verified);
        Assert.Equal(2, chip.Seq);
    }

    [Fact]
    public void Tolerance_holds_on_the_end_ms_side()
    {
        // Pins the inside-span branch on the high side: a stamp far from StartMs (> 2s) but
        // exactly AT EndMs still resolves (inside [StartMs, EndMs]); one second past EndMs
        // (and still > StartMs+2000) does not.
        var rows = new[] { Row(4, 10_000, 30_000, "Alice", "We agreed to settle for ten thousand dollars") };
        var atEnd = CitationValidator.Validate(
            "The parties agreed to settle for ten thousand dollars [00:00:30]", rows, "s1");
        Assert.False(atEnd.Lines[0].Unverifiable);              // 30s == EndMs, inside the span
        Assert.Equal(4, atEnd.Lines[0].Chips[0].Seq);

        var pastEnd = CitationValidator.Validate(
            "The parties agreed to settle for ten thousand dollars [00:00:31]", rows, "s1");
        Assert.True(pastEnd.Lines[0].Unverifiable);             // 31s: past EndMs, > StartMs+2000
        Assert.Equal("cited time not found in the record", pastEnd.Lines[0].Reason);
    }
}
