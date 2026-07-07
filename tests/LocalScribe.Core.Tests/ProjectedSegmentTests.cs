using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using Xunit;

public class ProjectedSegmentTests
{
    private static TranscriptLine Seg() =>
        TranscriptLine.Segment(1, TranscriptSource.Local, 1000, 4000, "hi", "Speaker 1");

    [Fact]
    public void Defaults_MatchLine_AndAreNotSplitChildren()
    {
        var p = new ProjectedSegment(Seg(), "hi");
        Assert.False(p.IsSplitChild);
        Assert.Equal(1000, p.StartMs);
        Assert.Equal(4000, p.EndMs);
    }

    [Fact]
    public void Overrides_WinForSplitChild()
    {
        var p = new ProjectedSegment(Seg(), "half", IsSplitChild: true, PartIndex: 1,
            StartMsOverride: 2500, EndMsOverride: 4000, SpeakerParticipantId: "p-2");
        Assert.True(p.IsSplitChild);
        Assert.Equal(2500, p.StartMs);
        Assert.Equal("p-2", p.SpeakerParticipantId);
    }
}
