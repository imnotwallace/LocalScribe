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
            editedText: "First half. Second half.", startMs: 15000, endMs: 17000, derivedStart: false,
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
        var seg = new EditableSegmentViewModel(3, TranscriptSource.Remote, 0, "hello", 15000, 17000, false,
            "hello", null, false);
        Assert.Throws<InvalidOperationException>(() => EditableSegmentViewModel.SplitAt(seg, 0, 17000));
        Assert.Throws<InvalidOperationException>(() => EditableSegmentViewModel.SplitAt(seg, 5, 17000)); // end
    }

    [Fact]
    public void SplitChild_WithoutOverride_ShowsTheInheritPlaceholder_UntilASpeakerIsPicked()
    {
        // UX round 2026-08-02 item 3.11: a split child with no persisted override deliberately
        // carries Speaker = null ("inherits the parent seq's name") - which painted a blank
        // ComboBox that looked broken. Display-only fix: the null-means-inherit persistence
        // semantics are untouched.
        var choices = new List<SpeakerChoice>
        { new("Automatic (Me / Them)", null, null, IsUnassign: true) };
        var child = new EditableSegmentViewModel(3, TranscriptSource.Remote, 1, "tail",
            15000, 17000, derivedStart: true, rawText: "head tail", speaker: null,
            isSplitChild: true, choices);
        Assert.Equal("(inherits parent's speaker)", child.SpeakerPlaceholder);

        var raised = new List<string?>();
        child.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        child.Speaker = choices[0];
        Assert.Equal("", child.SpeakerPlaceholder);          // picked -> placeholder clears
        Assert.Contains(nameof(EditableSegmentViewModel.SpeakerPlaceholder), raised);

        child.Speaker = null;                                // back to inherit
        Assert.Equal("(inherits parent's speaker)", child.SpeakerPlaceholder);
    }

    [Fact]
    public void WholeSegment_NeverShowsTheInheritPlaceholder()
    {
        var whole = new EditableSegmentViewModel(4, TranscriptSource.Remote, 0, "line",
            0, 1000, derivedStart: false, rawText: "line", speaker: null,
            isSplitChild: false, null);
        Assert.Equal("", whole.SpeakerPlaceholder);          // whole segments fall back to
                                                             // "Automatic (Me / Them)" elsewhere
    }
}
