using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class AddSessionsPickerViewModelTests
{
    private static AddSessionsPickerViewModel Make() => new(
    [
        new PickerSessionItem("a", "Webex call with Smith", "2026-07-01 10:00", "Webex"),
        new PickerSessionItem("b", "Manual note", "2026-07-02 11:00", "Manual"),
        new PickerSessionItem("c", "Webex follow-up", "2026-07-03 12:00", "Webex"),
    ]);

    [Fact]
    public void All_candidates_visible_initially_and_none_selected()
    {
        var vm = Make();
        Assert.Equal(3, vm.Visible.Count);
        Assert.Empty(vm.SelectedIds);
    }

    [Fact]
    public void Filter_narrows_by_title_case_insensitive()
    {
        var vm = Make();
        vm.FilterText = "webex";
        Assert.Equal(new[] { "a", "c" }, vm.Visible.Select(i => i.Id));
        vm.FilterText = "";
        Assert.Equal(3, vm.Visible.Count);
    }

    [Fact]
    public void Selection_survives_filtering()
    {
        var vm = Make();
        vm.Visible.First(i => i.Id == "b").IsSelected = true;
        vm.FilterText = "webex";                       // b hidden but stays selected
        vm.Visible.First(i => i.Id == "a").IsSelected = true;
        Assert.Equal(new[] { "a", "b" }, vm.SelectedIds.OrderBy(x => x));
    }
}
