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
}
