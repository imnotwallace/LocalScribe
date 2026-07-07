using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using Xunit;

public class EditableSegmentViewModelTests
{
    [Fact]
    public void SplitAt_PartitionsText_AndEstimatesDerivedStartTo10ms()
    {
        var seg = new EditableSegmentViewModel(seq: 3, source: TranscriptSource.Remote, partIndex: 0,
            editedText: "First half. Second half.", startMs: 15000, derivedStart: false,
            rawText: "First half. Second half.", speaker: null, isSplitChild: false);

        // caret right after "First half." (index 11 of a 24-char string), segment ends at 17000.
        var (left, right) = EditableSegmentViewModel.SplitAt(seg, caret: 11, segEndMs: 17000);

        Assert.Equal("First half.", left.Text.TrimEnd());
        Assert.Equal("Second half.", right.Text.TrimStart());
        Assert.False(left.DerivedStart);
        Assert.Equal(15000, left.StartMs);
        Assert.True(right.DerivedStart);
        // proportion 11/24 * (17000-15000) = 916.6 -> +15000 = 15916.6 -> round to 10ms = 15920.
        Assert.Equal(15920, right.StartMs);
        Assert.Equal(0, right.StartMs % 10);       // 10 ms grid
    }

    [Fact]
    public void SplitAt_RejectsDegenerateCaret()
    {
        var seg = new EditableSegmentViewModel(3, TranscriptSource.Remote, 0, "hello", 15000, false,
            "hello", null, false);
        Assert.Throws<InvalidOperationException>(() => EditableSegmentViewModel.SplitAt(seg, 0, 17000));
        Assert.Throws<InvalidOperationException>(() => EditableSegmentViewModel.SplitAt(seg, 5, 17000)); // end
    }
}
