using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;

public class ClusterAssignerTests
{
    private static TranscriptLine Seg(int seq, long start, long end) =>
        TranscriptLine.Segment(seq, TranscriptSource.Remote, start, end, "x", "Them");

    [Fact]
    public void Assigns_each_seq_to_max_overlap_cluster()
    {
        var lines = new[] { Seg(1, 0, 1000), Seg(2, 1000, 2000) };
        var segs = new[]
        {
            new DiarisedSegment(0, 1100, 0),     // covers seq 1 fully, small bit of seq 2
            new DiarisedSegment(1100, 2000, 1),  // covers most of seq 2
        };
        var a = ClusterAssigner.Assign(lines, segs, SourceKind.Remote);

        Assert.Equal("Remote:0", a.SeqToClusterKey["1"]);
        Assert.Equal("Remote:1", a.SeqToClusterKey["2"]);
        Assert.Equal(new[] { "Remote:0", "Remote:1" }, a.ClusterKeys);
    }

    [Fact]
    public void Uncovered_seq_is_left_unassigned()
    {
        var lines = new[] { Seg(1, 0, 500), Seg(2, 5000, 5500) };   // seq 2 in a diariser gap
        var segs = new[] { new DiarisedSegment(0, 1000, 0) };
        var a = ClusterAssigner.Assign(lines, segs, SourceKind.Remote);

        Assert.True(a.SeqToClusterKey.ContainsKey("1"));
        Assert.False(a.SeqToClusterKey.ContainsKey("2"));
    }

    [Fact]
    public void Equal_overlap_breaks_to_lower_cluster_id()
    {
        var lines = new[] { Seg(1, 0, 1000) };
        var segs = new[]
        {
            new DiarisedSegment(0, 500, 1),   // 500ms overlap, cluster 1
            new DiarisedSegment(500, 1000, 0) // 500ms overlap, cluster 0
        };
        var a = ClusterAssigner.Assign(lines, segs, SourceKind.Remote);
        Assert.Equal("Remote:0", a.SeqToClusterKey["1"]);
    }

    [Fact]
    public void Negative_cluster_id_is_not_mistaken_for_no_candidate()
    {
        var lines = new[] { Seg(1, 0, 1000) };
        var segs = new[] { new DiarisedSegment(0, 1000, -1) };
        var a = ClusterAssigner.Assign(lines, segs, SourceKind.Remote);

        Assert.True(a.SeqToClusterKey.ContainsKey("1"));
        Assert.Equal("Remote:-1", a.SeqToClusterKey["1"]);
    }

    [Fact]
    public void Ignores_other_source_and_markers()
    {
        var lines = new[]
        {
            TranscriptLine.Segment(1, TranscriptSource.Local, 0, 1000, "x", "Me"),
            TranscriptLine.Marker(2, 0, "paused"),
            Seg(3, 0, 1000),
        };
        var segs = new[] { new DiarisedSegment(0, 1000, 0) };
        var a = ClusterAssigner.Assign(lines, segs, SourceKind.Remote);
        Assert.Equal(new[] { "3" }, a.SeqToClusterKey.Keys.ToArray());
    }
}
