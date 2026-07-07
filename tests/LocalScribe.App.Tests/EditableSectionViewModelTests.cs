// tests/LocalScribe.App.Tests/EditableSectionViewModelTests.cs
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using Xunit;

public class EditableSectionViewModelTests
{
    private static DisplayRow OneSegmentRow() => new()
    {
        DisplayName = "Them", StartMs = 15000, EndMs = 17000, Text = "First. Second.",
        Segments =
        [
            new RowSegment(3, TranscriptSource.Remote, 15000, 17000, "First. Second.", "First. Second.",
                false, false),
        ],
    };

    [Fact]
    public void BeginEdit_MaterializesChildSegments()
    {
        var vm = new EditableSectionViewModel(OneSegmentRow());
        Assert.Empty(vm.Segments);                    // nothing until edit
        vm.BeginEdit("relative", DateTimeOffset.UtcNow);
        Assert.Single(vm.Segments);
        Assert.True(vm.IsEditing);
    }

    [Fact]
    public void SplitSegment_ReplacesWithTwoHalves_AndCollectsSplitEdit()
    {
        var vm = new EditableSectionViewModel(OneSegmentRow());
        vm.BeginEdit("relative", DateTimeOffset.UtcNow);
        vm.SplitSegment(vm.Segments[0], caret: 6);   // after "First."
        Assert.Equal(2, vm.Segments.Count);

        var splits = vm.CollectSplits();
        Assert.Single(splits);
        Assert.Equal(3, splits[0].Seq);
        Assert.Equal(2, splits[0].Parts.Count);
        Assert.True(splits[0].Parts[1].DerivedStart);
    }

    private static DisplayRow TwoSegmentRow() => new()
    {
        DisplayName = "Them", StartMs = 15000, EndMs = 19000, Text = "First. Second. Third.",
        Segments =
        [
            new RowSegment(3, TranscriptSource.Remote, 15000, 17000, "First. Second.", "First. Second.",
                false, false),
            new RowSegment(4, TranscriptSource.Remote, 17000, 19000, "Third.", "Third.",
                false, false),
        ],
    };

    [Fact]
    public void SplitOneSegment_LeavesSiblingCollectibleAsCorrection()
    {
        var vm = new EditableSectionViewModel(TwoSegmentRow());
        vm.BeginEdit("relative", DateTimeOffset.UtcNow);
        var seq3 = vm.Segments.Single(s => s.Seq == 3);
        vm.SplitSegment(seq3, caret: 6);   // after "First."

        var seq4 = vm.Segments.Single(s => s.Seq == 4);
        seq4.EditedText = "Third revised.";

        Assert.False(seq4.IsSplitChild);
        Assert.Equal(0, seq4.PartIndex);

        var corrections = vm.CollectCorrections();
        Assert.True(corrections.ContainsKey(4));
        Assert.Equal("Third revised.", corrections[4]);
    }

    [Fact]
    public void SplitThenRevert_LeavesSegmentCollectibleAsCorrection()
    {
        var vm = new EditableSectionViewModel(OneSegmentRow());
        vm.BeginEdit("relative", DateTimeOffset.UtcNow);
        vm.SplitSegment(vm.Segments[0], caret: 6);   // after "First."
        vm.RevertSplit(3);

        Assert.Single(vm.Segments);
        var survivor = vm.Segments[0];
        Assert.False(survivor.IsSplitChild);

        survivor.EditedText = "First. Second. Revised.";

        var corrections = vm.CollectCorrections();
        Assert.True(corrections.ContainsKey(3));
        Assert.Equal("First. Second. Revised.", corrections[3]);
    }
}
