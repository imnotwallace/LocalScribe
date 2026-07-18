using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class PagerViewModelTests
{
    [Fact]
    public void Defaults_are_page_1_size_50_empty()
    {
        var p = new PagerViewModel();
        Assert.Equal(1, p.CurrentPage);
        Assert.Equal(50, p.PageSize);
        Assert.Equal(0, p.TotalCount);
        Assert.Equal(1, p.PageCount);          // min 1 even when empty
        Assert.False(p.HasItems);
        Assert.False(p.CanGoPrev);
        Assert.False(p.CanGoNext);
        Assert.Equal(new[] { 25, 50, 100 }, PagerViewModel.PageSizeChoices);
    }

    [Theory]
    [InlineData(0, 50, 1)]
    [InlineData(1, 50, 1)]
    [InlineData(50, 50, 1)]
    [InlineData(51, 50, 2)]
    [InlineData(100, 25, 4)]
    [InlineData(101, 25, 5)]
    public void PageCount_is_ceiling_of_total_over_size(int total, int size, int expected)
    {
        var p = new PagerViewModel { PageSize = size };
        p.SetTotal(total);
        Assert.Equal(expected, p.PageCount);
    }

    [Fact]
    public void Next_and_prev_move_within_bounds_and_raise_Changed()
    {
        var p = new PagerViewModel { PageSize = 25 };
        p.SetTotal(60);                        // 3 pages
        int changed = 0;
        p.Changed += () => changed++;

        Assert.True(p.NextCommand.CanExecute(null));
        p.NextCommand.Execute(null);
        Assert.Equal(2, p.CurrentPage);
        p.NextCommand.Execute(null);
        Assert.Equal(3, p.CurrentPage);
        Assert.False(p.CanGoNext);
        p.PrevCommand.Execute(null);
        Assert.Equal(2, p.CurrentPage);
        Assert.Equal(3, changed);
        Assert.Equal("Page 2 of 3", p.PageText);
    }

    [Fact]
    public void SetTotal_clamps_current_page_silently()
    {
        var p = new PagerViewModel { PageSize = 25 };
        p.SetTotal(100);                       // 4 pages
        p.CurrentPage = 4;
        int changed = 0;
        p.Changed += () => changed++;
        p.SetTotal(30);                        // now 2 pages -> clamp to 2
        Assert.Equal(2, p.CurrentPage);
        Assert.Equal(0, changed);              // silent: host re-slices right after SetTotal
    }

    [Fact]
    public void Reset_returns_to_page_1_silently()
    {
        var p = new PagerViewModel { PageSize = 25 };
        p.SetTotal(100);
        p.CurrentPage = 3;
        int changed = 0;
        p.Changed += () => changed++;
        p.Reset();
        Assert.Equal(1, p.CurrentPage);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void PageSize_change_resets_to_page_1_and_raises_Changed_once()
    {
        var p = new PagerViewModel { PageSize = 25 };
        p.SetTotal(100);
        p.CurrentPage = 3;
        int changed = 0;
        p.Changed += () => changed++;
        p.PageSize = 100;
        Assert.Equal(1, p.CurrentPage);
        Assert.Equal(1, changed);              // exactly one Changed for the whole size flip
    }

    [Fact]
    public void Slice_returns_the_current_page_window()
    {
        var p = new PagerViewModel { PageSize = 25 };
        var items = Enumerable.Range(0, 60).ToList();
        p.SetTotal(items.Count);
        Assert.Equal(Enumerable.Range(0, 25), p.Slice(items));
        p.CurrentPage = 3;
        Assert.Equal(Enumerable.Range(50, 10), p.Slice(items));
    }
}
