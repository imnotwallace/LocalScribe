using Wpf.Ui.Abstractions;

namespace LocalScribe.App.Services;

/// <summary>INavigationViewPageProvider over a fixed Type-to-instance map. MainWindow hands it
/// to NavigationView (SetPageProviderService), so TargetPageType navigation resolves the pages
/// App.OnStartup built WITH their ViewModels instead of reflecting over parameterless ctors
/// (Tasks 15-21 gave every page a VM-taking ctor). One provider per MainWindow open: pages are
/// re-created per window - a WPF element cannot be re-hosted across windows - while the VMs
/// inside them are singletons, so page state survives close/reopen.</summary>
public sealed class StaticPageProvider : INavigationViewPageProvider
{
    private readonly IReadOnlyDictionary<Type, object> _pages;

    public StaticPageProvider(IReadOnlyDictionary<Type, object> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        _pages = pages;
    }

    /// <summary>Null for unknown types: NavigationView then surfaces its own failure instead
    /// of this class masking a wiring mistake with a bogus page.</summary>
    public object? GetPage(Type pageType) => _pages.TryGetValue(pageType, out var page) ? page : null;
}
