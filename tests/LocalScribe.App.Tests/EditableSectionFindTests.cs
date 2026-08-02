// tests/LocalScribe.App.Tests/EditableSectionFindTests.cs
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Item 1 (UX round 2026-08-02): the edit-mode find corpus rule and its change
/// notification live on EditableSectionViewModel - section-level, no session fixture needed.</summary>
public sealed class EditableSectionFindTests
{
    /// <summary>Two Local segments; Row.Text is exactly SectionGrouper's single-space join.</summary>
    private static DisplayRow MakeRow() => new()
    {
        StartMs = 0,
        EndMs = 3000,
        DisplayName = "Sam",
        Text = "hello world goodbye",
        Segments = new[]
        {
            new RowSegment(0, TranscriptSource.Local, 0, 1500, "hello world", "hello world",
                IsCorrected: false, IsPinned: false),
            new RowSegment(1, TranscriptSource.Local, 1600, 3000, "goodbye", "goodbye",
                IsCorrected: false, IsPinned: false),
        },
    };

    [Fact]
    public void SearchText_uses_row_text_collapsed_and_live_joined_text_expanded()
    {
        var section = new EditableSectionViewModel(MakeRow());
        Assert.Equal("hello world goodbye", section.SearchText);      // collapsed: loaded Row.Text

        section.BeginEdit("relative", default);
        Assert.Equal("hello world goodbye", section.SearchText);      // expanded, untouched: same join

        section.Segments[0].EditedText = "changed text";
        Assert.Equal("changed text goodbye", section.SearchText);     // expanded: LIVE buffer wins
    }

    [Fact]
    public void Find_flags_are_observable()
    {
        var section = new EditableSectionViewModel(MakeRow());
        var raised = new List<string?>();
        section.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        section.IsFindMatch = true;
        section.IsCurrentFindMatch = true;

        Assert.Contains(nameof(EditableSectionViewModel.IsFindMatch), raised);
        Assert.Contains(nameof(EditableSectionViewModel.IsCurrentFindMatch), raised);
    }

    [Fact]
    public void LiveTextChanged_fires_for_typing_and_survives_split_and_revert()
    {
        var section = new EditableSectionViewModel(MakeRow());
        int fired = 0;
        section.LiveTextChanged += () => fired++;

        section.BeginEdit("relative", default);
        Assert.True(fired > 0);                        // materialization changes the live corpus

        fired = 0;
        section.Segments[0].EditedText = "typed here";
        Assert.Equal(1, fired);                        // plain typing

        fired = 0;
        section.SplitSegment(section.Segments[0], caret: 5);
        Assert.True(fired > 0);                        // split REPLACES segment instances

        fired = 0;
        section.Segments[0].EditedText = "typed again";
        Assert.True(fired > 0);                        // the replacement instance is re-wired

        fired = 0;
        section.RevertSplit(0);
        Assert.True(fired > 0);                        // revert replaces instances too

        fired = 0;
        section.Segments[0].EditedText = "after revert";
        Assert.True(fired > 0);                        // the restored instance is re-wired
    }

    [Fact]
    public void LocateMatch_maps_the_joined_hit_to_segment_and_offset()
    {
        var section = new EditableSectionViewModel(MakeRow());
        Assert.Null(section.LocateMatch("goodbye"));                  // collapsed: not materialized

        section.BeginEdit("relative", default);
        Assert.Equal((0, 6, 5), section.LocateMatch("world"));        // inside segment 0
        Assert.Equal((1, 0, 7), section.LocateMatch("goodbye"));      // segment 1, offset rebased
        Assert.Equal((0, 6, 5), section.LocateMatch("WORLD"));        // case-insensitive
        Assert.Null(section.LocateMatch("absent"));

        // A match spanning the join space is selectable only in the TextBox it starts in.
        Assert.Equal((0, 6, 5), section.LocateMatch("world goodbye"));
    }

    [Fact]
    public void SetFindSelection_orders_length_before_start_and_clear_resets()
    {
        var section = new EditableSectionViewModel(MakeRow());
        section.BeginEdit("relative", default);
        var seg = section.Segments[0];
        int lengthAtStartChange = -1;
        seg.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditableSegmentViewModel.FindSelectionStart))
                lengthAtStartChange = seg.FindSelectionLength;
        };

        seg.SetFindSelection(6, 5);
        Assert.Equal(6, seg.FindSelectionStart);
        Assert.Equal(5, lengthAtStartChange);          // Length was already set when Start fired

        seg.ClearFindSelection();
        Assert.Equal(-1, seg.FindSelectionStart);
    }
}
