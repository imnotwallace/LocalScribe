using LocalScribe.Core.Projection;

public class SectionGrouperTests
{
    private static PreRow Seg(long start, long end, string name, string text) =>
        new(start, end, 0, 0, name, text, IsMarker: false);
    private static PreRow Marker(long at, string text) =>
        new(at, at, 2, 0, Name: null, text, IsMarker: true);

    [Fact]
    public void Same_speaker_gap_below_threshold_merges_into_one_section()
    {
        var rows = SectionGrouper.Group(new[]
        {
            Seg(0, 1000, "Me", "one"),
            Seg(1500, 2500, "Me", "two"),   // gap 1500-1000 = 500 < 5000 -> merge
        }, gapMs: 5000);
        Assert.Single(rows);
        Assert.Equal("one two", rows[0].Text);
        Assert.Equal(2500, rows[0].EndMs);   // running end extends
    }

    [Fact]
    public void Same_speaker_gap_at_threshold_starts_new_section()
    {
        var rows = SectionGrouper.Group(new[]
        {
            Seg(0, 1000, "Me", "one"),
            Seg(6000, 7000, "Me", "two"),   // gap 6000-1000 = 5000 == threshold -> split
        }, gapMs: 5000);
        Assert.Equal(2, rows.Count);
        Assert.Equal("one", rows[0].Text);
        Assert.Equal("two", rows[1].Text);
    }

    [Fact]
    public void Speaker_change_always_starts_new_section()
    {
        var rows = SectionGrouper.Group(new[]
        {
            Seg(0, 1000, "Me", "hi"),
            Seg(1100, 2000, "Them", "yo"),   // tiny gap but different speaker
        }, gapMs: 5000);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Me", rows[0].DisplayName);
        Assert.Equal("Them", rows[1].DisplayName);
    }

    [Fact]
    public void Marker_passes_through_and_breaks_grouping()
    {
        var rows = SectionGrouper.Group(new[]
        {
            Seg(0, 1000, "Me", "a"),
            Marker(1500, "paused by user"),
            Seg(1600, 2000, "Me", "b"),   // same speaker, tiny gap, but the marker split it
        }, gapMs: 5000);
        Assert.Equal(3, rows.Count);
        Assert.False(rows[0].IsMarker);
        Assert.True(rows[1].IsMarker);
        Assert.Equal("paused by user", rows[1].Text);
        Assert.Equal(1500, rows[1].EndMs);
        Assert.False(rows[2].IsMarker);
        Assert.Equal("b", rows[2].Text);
    }

    [Fact]
    public void Out_of_order_overlapping_same_speaker_stays_merged_and_extends_end()
    {
        // Talk-over / late insert: the second segment starts before the first ends, so the gap
        // is negative (< threshold) -> merge; the running end takes the max. No crash.
        var rows = SectionGrouper.Group(new[]
        {
            Seg(0, 2000, "Me", "one"),
            Seg(1000, 3000, "Me", "two"),
        }, gapMs: 5000);
        Assert.Single(rows);
        Assert.Equal("one two", rows[0].Text);
        Assert.Equal(3000, rows[0].EndMs);
    }
}
