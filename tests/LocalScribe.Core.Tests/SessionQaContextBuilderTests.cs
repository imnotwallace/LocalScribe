using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

public class SessionQaContextBuilderTests
{
    private static DisplayRow Row(int seq, long startMs, string? name, string text) => new()
    {
        StartMs = startMs, EndMs = startMs + 1000, DisplayName = name, Text = text,
        Segments = [new RowSegment(seq, TranscriptSource.Local, startMs, startMs + 1000, text, text, false, false)]
    };

    [Fact]
    public void Ladder_picks_the_smallest_step_that_fits_the_80_percent_gate()
    {
        // (int)(8192*0.8)=6553; (int)(65536*0.8)=52428; reserve 1024 (design 7.2/7.4 numbers).
        Assert.Equal(8192, QaContextLadder.Pick(5000));     // 5000+1024 <= 6553
        Assert.Equal(16384, QaContextLadder.Pick(6000));    // 7024 > 6553 -> next step
        Assert.Equal(65536, QaContextLadder.Pick(51000));   // 52024 <= 52428
        Assert.Null(QaContextLadder.Pick(52000));           // 53024 > 52428 -> excerpt mode
    }

    [Fact]
    public void Body_carries_canonical_anchors_speakers_and_skips_markers()
    {
        var rows = new List<DisplayRow>
        {
            Row(0, 5_000, "Alice", "Hello there"),
            new() { IsMarker = true, StartMs = 6_000, EndMs = 6_000, Text = "microphone muted" },
            Row(1, 65_000, "Bob", "We agreed to settle"),
            Row(2, 70_000, null, "Something else"),
        };
        var ctx = SessionQaContextBuilder.Build(rows, s => s.Length / 2);
        Assert.Equal(
            "[00:00:05] Alice: Hello there\n" +
            "[00:01:05] Bob: We agreed to settle\n" +
            "[00:01:10] Unknown speaker: Something else\n",
            ctx.ContextBody.Replace("\r\n", "\n"));
        Assert.Equal(new[] { "Alice", "Bob", "Unknown speaker" }, ctx.SpeakerNames);
        Assert.Same(rows, ctx.Rows);                         // the validator's ground truth, unchanged
        Assert.Equal(8192, ctx.CtxTokens);
        Assert.False(ctx.NeedsExcerpts);
    }

    [Fact]
    public void StripLine_seam_is_applied_to_each_row_text()
    {
        var rows = new List<DisplayRow> { Row(0, 5_000, "Alice", "12:00 Hello there") };
        var ctx = SessionQaContextBuilder.Build(rows, s => s.Length / 2,
            stripLine: s => s.StartsWith("12:00 ") ? s[6..] : s);
        Assert.Contains("[00:00:05] Alice: Hello there", ctx.ContextBody);
        Assert.DoesNotContain("12:00 Hello", ctx.ContextBody);
    }

    [Fact]
    public void Oversized_transcript_needs_excerpts()
    {
        // 200k chars -> ~100k estimated tokens at the worst-case 2 chars/token: over every step.
        var rows = new List<DisplayRow> { Row(0, 0, "Alice", new string('x', 200_000)) };
        var ctx = SessionQaContextBuilder.Build(rows, s => s.Length / 2);
        Assert.Null(ctx.CtxTokens);
        Assert.True(ctx.NeedsExcerpts);
    }
}
