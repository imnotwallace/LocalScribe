using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void Defaults_to_sessions_and_raises_change_notifications()
    {
        var vm = new MainWindowViewModel(new InfoBarErrorReporter(a => a()));
        Assert.Equal("Sessions", vm.SelectedSection);      // design section 2: Sessions is default

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SelectedSection = "Matters";
        Assert.Equal("Matters", vm.SelectedSection);
        Assert.Contains(nameof(MainWindowViewModel.SelectedSection), raised);
    }

    [Fact]
    public void Exposes_the_shared_error_queue()
    {
        var errors = new InfoBarErrorReporter(a => a());
        var vm = new MainWindowViewModel(errors);
        Assert.Same(errors, vm.Errors);
    }
}
