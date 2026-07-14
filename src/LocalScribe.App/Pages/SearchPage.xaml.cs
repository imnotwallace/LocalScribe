using System.Windows.Controls;
using LocalScribe.App.ViewModels;

namespace LocalScribe.App.Pages;

/// <summary>Thin code-behind for the Search page (design 2026-07-13 section 2.2 surface 1).
/// Constructed by the composition root with its VM (never via a navigation URI), mirroring
/// SessionsPage: Loaded refreshes the matter facet options; OnNavigatedToAsync catches all its
/// own exceptions, so the async-void Loaded lambda cannot throw.</summary>
public partial class SearchPage : Page
{
    private readonly SearchPageViewModel _vm;

    public SearchPage(SearchPageViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Loaded += (_, _) => _ = _vm.OnNavigatedToAsync();
    }
}
