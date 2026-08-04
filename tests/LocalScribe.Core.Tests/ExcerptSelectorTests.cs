using LocalScribe.Core.Projection;
using Xunit;

namespace LocalScribe.Core.Tests;

/// <summary>Whole-row overlap selection (design 2026-08-04 section 8). Rows are never truncated,
/// so the exported span snaps OUTWARD to turn boundaries and the document must report the ACTUAL
/// span, not the requested one.</summary>
public sealed class ExcerptSelectorTests
{
    private static DisplayRow Turn(long startMs, long endMs, string text = "x") =>
        new() { StartMs = startMs, EndMs = endMs, DisplayName = "Sam", Text = text };

    [Fact]
    public void A_row_straddling_the_from_boundary_is_included_whole_and_verbatim()
    {
        var rows = new[] { Turn(0, 5000, "straddles the start") };
        var kept = ExcerptSelector.Select(rows, new ExcerptRange(3000, 9000));

        Assert.Single(kept);
        Assert.Equal("straddles the start", kept[0].Text);   // never truncated
        Assert.Equal(0, kept[0].StartMs);                    // original anchors preserved
    }

    [Fact]
    public void Rows_entirely_outside_the_range_are_excluded()
    {
        var rows = new[] { Turn(0, 1000), Turn(4000, 6000), Turn(20000, 22000) };
        var kept = ExcerptSelector.Select(rows, new ExcerptRange(3000, 9000));

        Assert.Single(kept);
        Assert.Equal(4000, kept[0].StartMs);
    }

    [Fact]
    public void A_row_touching_the_boundary_with_zero_overlap_is_excluded()
    {
        var rows = new[] { Turn(0, 3000) };
        Assert.Empty(ExcerptSelector.Select(rows, new ExcerptRange(3000, 9000)));
    }

    [Fact]
    public void A_zero_length_marker_row_inside_the_range_is_included()
    {
        var marker = new DisplayRow { IsMarker = true, StartMs = 4000, EndMs = 4000, Text = "Paused" };
        var kept = ExcerptSelector.Select([marker], new ExcerptRange(3000, 9000));
        Assert.Single(kept);
    }

    [Fact]
    public void A_zero_length_marker_row_outside_the_range_is_excluded()
    {
        var marker = new DisplayRow { IsMarker = true, StartMs = 12000, EndMs = 12000, Text = "Paused" };
        Assert.Empty(ExcerptSelector.Select([marker], new ExcerptRange(3000, 9000)));
    }

    [Fact]
    public void Actual_span_is_the_outward_snapped_boundary_of_the_selected_rows()
    {
        var rows = new[] { Turn(0, 5000), Turn(5500, 12000) };
        var kept = ExcerptSelector.Select(rows, new ExcerptRange(3000, 9000));

        Assert.Equal((0L, 12000L), ExcerptSelector.ActualSpan(kept));   // NOT (3000, 9000)
    }

    [Fact]
    public void Actual_span_of_nothing_is_zero()
        => Assert.Equal((0L, 0L), ExcerptSelector.ActualSpan([]));
}
