using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using Xunit;

public class DisplayRowTests
{
    [Fact]
    public void HasSplit_TrueWhenAnyChildIsSplit()
    {
        var row = new DisplayRow
        {
            DisplayName = "Them", Text = "a b",
            Segments =
            [
                new RowSegment(3, TranscriptSource.Remote, 15000, 16000, "a", "a b", false, false,
                    IsSplitChild: true, PartIndex: 0),
                new RowSegment(3, TranscriptSource.Remote, 16000, 17000, "b", "a b", false, false,
                    IsSplitChild: true, PartIndex: 1),
            ],
        };
        Assert.True(row.HasSplit);
    }
}
