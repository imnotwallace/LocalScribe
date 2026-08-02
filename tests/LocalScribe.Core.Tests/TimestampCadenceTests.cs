using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

public class TimestampCadenceTests
{
    private static RowSegment Seg(int seq, long start, long end, string text) =>
        new(seq, TranscriptSource.Local, start, end, text, text, false, false);

    private static DisplayRow Row(params RowSegment[] segs) => new()
    {
        StartMs = segs[0].StartMs, EndMs = segs[^1].EndMs, DisplayName = "Me",
        Text = string.Join(" ", segs.Select(s => s.ProjectedText)), Segments = segs,
    };

    [Fact]
    public void Non_positive_interval_returns_one_whole_row_chunk()
    {
        var row = Row(Seg(0, 0, 4000, "one"), Seg(1, 20000, 24000, "two"));
        foreach (int interval in new[] { 0, -1 })
        {
            var only = Assert.Single(TimestampCadence.Chunk(row, interval));
            Assert.Equal(row.StartMs, only.StampMs);
            Assert.Equal(row.Text, only.Text);
            Assert.Same(row.Segments, only.Segments);
        }
    }

    [Fact]
    public void Marker_rows_pass_through_as_one_chunk()
    {
        var row = new DisplayRow
        { IsMarker = true, StartMs = 30000, EndMs = 30000, Text = "audio device changed" };
        var only = Assert.Single(TimestampCadence.Chunk(row, 15000));
        Assert.Equal("audio device changed", only.Text);
        Assert.Equal(30000L, only.StampMs);
    }

    [Fact]
    public void Rows_without_segments_pass_through_as_one_chunk()
    {
        // Live rows and the legacy renderer fixtures carry Text only (Segments empty).
        var row = new DisplayRow { StartMs = 1000, EndMs = 90000, DisplayName = "Me", Text = "long text" };
        var only = Assert.Single(TimestampCadence.Chunk(row, 15000));
        Assert.Equal("long text", only.Text);
        Assert.Equal(1000L, only.StampMs);
    }

    [Fact]
    public void No_boundary_crossing_the_interval_returns_row_text_verbatim()
    {
        // The whole-row chunk must carry row.Text VERBATIM, not the Segments re-join - proven by
        // a row whose Text deliberately differs from the join (SectionGrouper's null-payload
        // merge can contribute text without a Segment, SectionGrouper.cs:36).
        var row = new DisplayRow
        {
            StartMs = 0, EndMs = 9000, DisplayName = "Me", Text = "one lost two",
            Segments = new[] { Seg(0, 0, 4000, "one"), Seg(1, 4400, 9000, "two") },
        };
        var only = Assert.Single(TimestampCadence.Chunk(row, 15000));
        Assert.Equal("one lost two", only.Text);
    }

    [Fact]
    public void Boundary_at_exactly_the_interval_starts_a_new_chunk()
    {
        var row = Row(Seg(0, 0, 7000, "one"), Seg(1, 15000, 20000, "two"));   // 15000 - 0 == interval
        var chunks = TimestampCadence.Chunk(row, 15000);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(0L, chunks[0].StampMs);
        Assert.Equal("one", chunks[0].Text);
        Assert.Equal(15000L, chunks[1].StampMs);
        Assert.Equal("two", chunks[1].Text);
    }

    [Fact]
    public void Elapsed_time_measures_from_the_last_shown_stamp_not_the_previous_segment()
    {
        // Breaks at 15100 (>= 15000 since stamp 0) and at 30200 (>= 15000 since stamp 15100);
        // 18400 does NOT break (only 3300 since the 15100 stamp).
        var row = Row(
            Seg(0, 0, 4000, "a"), Seg(1, 4400, 9000, "b"),
            Seg(2, 15100, 18000, "c"), Seg(3, 18400, 22000, "d"),
            Seg(4, 30200, 31000, "e"));
        var chunks = TimestampCadence.Chunk(row, 15000);
        Assert.Equal(3, chunks.Count);
        Assert.Equal(new[] { 0L, 15100L, 30200L }, chunks.Select(c => c.StampMs));
        Assert.Equal(new[] { "a b", "c d", "e" }, chunks.Select(c => c.Text));
    }

    [Fact]
    public void Chunk_texts_rejoin_byte_identically_to_a_section_grouper_row()
    {
        // Join fidelity: the chunker's single-space join must be byte-identical to
        // SectionGrouper's prev.Text + " " + p.Text merge (SectionGrouper.cs:34).
        var pre = new[]
        {
            new PreRow(0, 4000, 0, 0, "Me", "one", false, Seg(0, 0, 4000, "one")),
            new PreRow(4400, 9000, 0, 1, "Me", "two", false, Seg(1, 4400, 9000, "two")),
            new PreRow(19400, 24000, 0, 2, "Me", "three", false, Seg(2, 19400, 24000, "three")),
        };
        var row = Assert.Single(SectionGrouper.Group(pre, gapMs: 30000));
        var chunks = TimestampCadence.Chunk(row, 15000);
        Assert.Equal(2, chunks.Count);
        Assert.Equal(row.Text, string.Join(" ", chunks.Select(c => c.Text)));
    }
}
