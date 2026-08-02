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
}
