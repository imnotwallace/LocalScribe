using System.Linq;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ReadViewSegmentTests
{
    private static RowSegment Seg(int seq, long start, long end, string text, bool split = false) =>
        new(seq, TranscriptSource.Local, start, end, text, text, IsCorrected: false, IsPinned: false,
            IsSplitChild: split);

    [Fact]
    public void ReadRow_maps_DisplayRow_segments_to_ReadSegments_in_order()
    {
        var row = new ReadRow(new DisplayRow
        {
            StartMs = 130208, EndMs = 143104, DisplayName = "Christine", Text = "a b c",
            Segments = new[] { Seg(25, 130208, 136320, "a"), Seg(27, 138720, 143104, "b", split: true) },
        });

        Assert.Equal(2, row.Segments.Count);
        Assert.Equal(130208, row.Segments[0].StartMs);
        Assert.Equal("a", row.Segments[0].Text);
        Assert.False(row.Segments[0].IsEstimatedStart);
        Assert.Equal(27, row.Segments[1].Data.Seq);
        Assert.True(row.Segments[1].IsEstimatedStart);   // split child carries an estimated start
    }

    [Fact]
    public void ReadRow_marker_has_no_segments()
    {
        var row = new ReadRow(new DisplayRow { IsMarker = true, StartMs = 0, EndMs = 0, Text = "marker" });
        Assert.Empty(row.Segments);
    }
}
