using System.Windows.Controls;
using System.Windows.Input;
using LocalScribe.App.ViewModels;

namespace LocalScribe.App.Pages;

/// <summary>Thin code-behind for the Sessions page. Constructed by the composition root with
/// its VM (never via a navigation URI, so the non-default ctor is fine). Loaded fires on every
/// navigation into view, which is exactly the 3.1 "page navigation" refresh trigger; LoadAsync
/// catches all exceptions, so the async-void Loaded lambda cannot throw.</summary>
public partial class SessionsPage : Page
{
    private readonly SessionsPageViewModel _vm;

    public SessionsPage(SessionsPageViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += async (_, _) => await vm.OnNavigatedToAsync();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
        => _vm.OpenReadViewCommand.Execute(_vm.SelectedRow);
}
