using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;

public class RowSegmentGroupingTests
{
    private static PreRow Seg(int seq, long startMs, long endMs, string name, string text,
        TranscriptSource source = TranscriptSource.Local,
        bool corrected = false, bool pinned = false)
        => new(startMs, endMs, source == TranscriptSource.Local ? 0 : 1, seq, name, text,
            IsMarker: false,
            Segment: new RowSegment(seq, source, startMs, endMs, text, "raw " + text, corrected, pinned));

    [Fact]
    public void Merged_turn_accumulates_constituent_segments_in_order()
    {
        var rows = SectionGrouper.Group(new[]
        {
            Seg(0, 0, 1000, "Me", "a"),
            Seg(1, 1500, 2500, "Me", "b"),
            Seg(2, 3000, 4000, "Me", "c"),
        }, gapMs: 5000);

        Assert.Single(rows);
        Assert.Equal("a b c", rows[0].Text);
        Assert.Equal(new[] { 0, 1, 2 }, rows[0].Segments.Select(s => s.Seq));
        Assert.All(rows[0].Segments, s => Assert.Equal(TranscriptSource.Local, s.Source));
        Assert.Equal("raw b", rows[0].Segments[1].RawText);
    }

    [Fact]
    public void Single_segment_row_carries_exactly_its_one_segment()
    {
        var rows = SectionGrouper.Group(new[] { Seg(7, 0, 1000, "Them", "x",
            TranscriptSource.Remote, corrected: true, pinned: true) }, 5000);

        var seg = Assert.Single(Assert.Single(rows).Segments);
        Assert.Equal(7, seg.Seq);
        Assert.True(seg.IsCorrected);
        Assert.True(seg.IsPinned);
    }

    [Fact]
    public void Marker_rows_have_empty_segments()
    {
        var rows = SectionGrouper.Group(new[]
        {
            new PreRow(1000, 1000, 2, 5, Name: null, "audio device changed", IsMarker: true),
        }, 5000);

        Assert.True(rows[0].IsMarker);
        Assert.Empty(rows[0].Segments);
    }

    [Fact]
    public void Null_segment_payload_keeps_grouping_working_with_empty_lists()
    {
        // The live view builds PreRows WITHOUT payloads - grouping must not require them.
        var rows = SectionGrouper.Group(new[]
        {
            new PreRow(0, 1000, 0, 0, "Me", "a", IsMarker: false),
            new PreRow(1200, 2000, 0, 1, "Me", "b", IsMarker: false),
        }, 5000);

        Assert.Single(rows);
        Assert.Equal("a b", rows[0].Text);
        Assert.Empty(rows[0].Segments);
    }

    [Fact]
    public void HasCorrection_and_HasPin_reflect_any_constituent_flag()
    {
        var rows = SectionGrouper.Group(new[]
        {
            Seg(0, 0, 1000, "Me", "a"),
            Seg(1, 1500, 2500, "Me", "b", corrected: true),
        }, 5000);

        Assert.True(rows[0].HasCorrection);
        Assert.False(rows[0].HasPin);
    }
}
