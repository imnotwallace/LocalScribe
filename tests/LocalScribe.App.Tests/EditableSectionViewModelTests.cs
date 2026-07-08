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

    // Bug fix (final review): same speaker, two machine segments in one section with a 500 ms
    // silence gap between A's own EndMs (10800) and B's StartMs (11300) - SectionGrouper merges
    // same-speaker turns within sectionGapMs (5000 ms), so this shape is normal, not contrived.
    private static DisplayRow GappedTwoSegmentRow() => new()
    {
        DisplayName = "Them", StartMs = 10000, EndMs = 12000, Text = "First is quite long here. Second.",
        Segments =
        [
            new RowSegment(5, TranscriptSource.Remote, 10000, 10800,
                "First is quite long here.", "First is quite long here.", false, false),
            new RowSegment(6, TranscriptSource.Remote, 11300, 12000, "Second.", "Second.", false, false),
        ],
    };

    [Fact]
    public void SplitSegment_ClampsDerivedStart_ToOwnSegmentEndMs_NotNextSegmentStart()
    {
        var vm = new EditableSectionViewModel(GappedTwoSegmentRow());
        vm.BeginEdit("relative", DateTimeOffset.UtcNow);
        var seqA = vm.Segments.Single(s => s.Seq == 5);

        // "First is quite long here." is 25 chars; caret=20 is proportion 0.8. Clamped (correctly)
        // to A's own EndMs (10800) that lands at 10640 - well inside (10000, 10800]. Clamped
        // (wrongly, pre-fix) to B's StartMs (11300) that lands at 11040 - past A's own EndMs, which
        // is exactly the value EditStore.ApplySplitAsync would reject as outside (line.StartMs, line.EndMs].
        vm.SplitSegment(seqA, caret: 20);
        Assert.Equal(2, vm.Segments.Count(s => s.Seq == 5));

        // Part 0 (machine floor) stays read-only-estimate-free; part 1 is the derived estimate.
        var partsInOrder = vm.Segments.Where(s => s.Seq == 5).OrderBy(s => s.PartIndex).ToList();
        Assert.False(partsInOrder[0].DerivedStart);
        Assert.True(partsInOrder[1].DerivedStart);

        var splits = vm.CollectSplits();
        var splitA = splits.Single(s => s.Seq == 5);
        Assert.Equal(2, splitA.Parts.Count);
        Assert.All(splitA.Parts, p => Assert.InRange(p.StartMs, 10000, 10800));
        Assert.True(splitA.Parts[1].StartMs <= 10800,
            $"derived start {splitA.Parts[1].StartMs} must be clamped to seq 5's own EndMs (10800), " +
            "not seq 6's StartMs (11300).");
    }

    [Fact]
    public void RefreshSpeakerChoices_keeps_a_selection_across_a_rename_matched_by_id()
    {
        var vm = new EditableSectionViewModel(OneSegmentRow());   // seq 3, Remote
        var jane = new SpeakerChoice("Jane", "p-jane", null);
        vm.BeginEdit("relative", DateTimeOffset.UtcNow,
            remoteChoices: new List<SpeakerChoice> { new("Automatic (Me / Them)", null, null, true), jane },
            localChoices: []);
        vm.Segments[0].Speaker = jane;

        // Session Details renamed p-jane "Jane" -> "Janet": same id, new display.
        vm.RefreshSpeakerChoices(
            new List<SpeakerChoice> { new("Automatic (Me / Them)", null, null, true),
                new("Janet", "p-jane", null) }, []);

        var sel = vm.Segments[0].Speaker!;
        Assert.Equal("p-jane", sel.ParticipantId);               // preserved by id, not blanked
        Assert.Equal("Janet", sel.Display);                      // now shows the new name
    }

    [Fact]
    public void RefreshSpeakerChoices_falls_back_to_automatic_when_the_participant_is_removed()
    {
        var vm = new EditableSectionViewModel(OneSegmentRow());
        var jane = new SpeakerChoice("Jane", "p-jane", null);
        vm.BeginEdit("relative", DateTimeOffset.UtcNow,
            remoteChoices: new List<SpeakerChoice> { new("Automatic (Me / Them)", null, null, true), jane },
            localChoices: []);
        vm.Segments[0].Speaker = jane;

        vm.RefreshSpeakerChoices(
            new List<SpeakerChoice> { new("Automatic (Me / Them)", null, null, true) }, []);   // p-jane removed

        Assert.True(vm.Segments[0].Speaker!.IsUnassign);         // visible Automatic, not a blank/null
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

    // Regression (GUI smoke): reverting a split must RESTORE the single machine segment - full
    // original text and machine start/end - not leave the surviving part-0 fragment ("First.")
    // with the derived boundary as its end. The old code deleted PartIndex>0 parts only, so the
    // row showed the truncated fragment and the other part's text vanished.
    [Fact]
    public void RevertSplit_RestoresOriginalMachineSegment_TextAndBounds()
    {
        var vm = new EditableSectionViewModel(OneSegmentRow());   // seq 3 "First. Second." 15000..17000
        vm.BeginEdit("relative", DateTimeOffset.UtcNow);
        vm.SplitSegment(vm.Segments[0], caret: 6);                // "First." | " Second."
        Assert.Equal(2, vm.Segments.Count);

        vm.RevertSplit(3);

        Assert.Single(vm.Segments);
        var survivor = vm.Segments[0];
        Assert.Equal("First. Second.", survivor.EditedText);      // restored, NOT the "First." fragment
        Assert.Equal("First. Second.", survivor.ProjectedText);
        Assert.False(survivor.IsSplitChild);
        Assert.Equal(0, survivor.PartIndex);
        Assert.Equal(15000, survivor.StartMs);                    // machine start
        Assert.Equal(17000, survivor.EndMs);                      // machine end, not the derived boundary
        Assert.Empty(vm.CollectSplits());                         // nothing to persist as a split
        Assert.Contains(3, vm.CollectSplitReverts());             // revert is queued
        Assert.False(vm.CollectCorrections().ContainsKey(3));     // restored text == projected: no phantom correction
    }

    [Fact]
    public void RevertSplit_InMultiSegmentSection_LeavesSiblingUntouched()
    {
        var vm = new EditableSectionViewModel(TwoSegmentRow());   // seq3 15000..17000, seq4 "Third." 17000..19000
        vm.BeginEdit("relative", DateTimeOffset.UtcNow);
        vm.SplitSegment(vm.Segments.Single(s => s.Seq == 3), caret: 6);
        Assert.Equal(3, vm.Segments.Count);                       // seq3 x2 + seq4

        vm.RevertSplit(3);

        Assert.Equal(2, vm.Segments.Count);                       // seq3 restored + seq4
        var s3 = vm.Segments.Single(s => s.Seq == 3);
        Assert.Equal("First. Second.", s3.EditedText);
        Assert.Equal(17000, s3.EndMs);
        var s4 = vm.Segments.Single(s => s.Seq == 4);
        Assert.Equal("Third.", s4.EditedText);                    // sibling untouched
        Assert.False(s4.IsSplitChild);
        Assert.Equal(0, s4.PartIndex);
    }
}
