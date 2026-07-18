using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Controls;
namespace LocalScribe.App;

/// <summary>Stage 4 manager shell (design section 2): FluentWindow + NavigationView hosting
/// the Sessions/Matters/Settings pages. GENUINELY closable - unlike the live view and overlay
/// (hide-on-close: a recording must never die with a window), nothing depends on this window
/// staying alive, and TrayIconHost re-creates it on demand. Humble object: section state and
/// the error queue live in the tested WPF-free MainWindowViewModel/InfoBarErrorReporter; this
/// class only mirrors them into WPF-UI controls.</summary>
public partial class MainWindow
{
    private readonly MainWindowViewModel _vm;
    private readonly WindowStateStore _stateStore;
    private readonly ISettingsService _settings;
    private bool _hwndReady;
    private Type? _pendingNavigate;

    public MainWindow(MainWindowViewModel vm, WindowStateStore stateStore, ISettingsService settings,
        INavigationViewPageProvider pageProvider)
    {
        InitializeComponent();
        // Tasks 15-21 gave the pages VM-taking ctors, so NavigationView's default
        // parameterless-ctor activator can no longer construct them; the provider (built per
        // window open by App.OnStartup with the real VMs) resolves TargetPageType navigation,
        // including the Loaded-time Navigate(typeof(Pages.SessionsPage)) below.
        RootNav.SetPageProviderService(pageProvider);
        (_vm, _stateStore, _settings) = (vm, stateStore, settings);
        DataContext = vm;

        vm.Errors.Messages.CollectionChanged += OnMessagesChanged;
        _settings.Changed += OnSettingsChanged;
        // WPF-UI 4.0.3's InfoBar exposes no Closed CLR event; the close button just flips
        // IsOpen false. DependencyPropertyDescriptor is the version-safe hook to advance the
        // queue on user dismissal (design 7.5).
        DependencyPropertyDescriptor
            .FromProperty(InfoBar.IsOpenProperty, typeof(InfoBar))
            .AddValueChanged(ErrorBar, OnErrorBarIsOpenChanged);
        SyncInfoBar();                                     // errors queued while closed show now
        // A NavigateToSection issued before Loaded (fresh window from the tray factory) must not
        // be clobbered by this default landing - it is stashed and wins here.
        Loaded += (_, _) =>
        {
            RootNav.Navigate(_pendingNavigate ?? typeof(Pages.SessionsPage));
            _pendingNavigate = null;
        };
    }

    /// <summary>Programmatic section navigation (read-view "Search all sessions" hand-off,
    /// design 2026-07-18 section 3). Navigating by page type moves the nav-rail selection too
    /// (Wpf.Ui matches TargetPageType), same as the Loaded-time landing.</summary>
    public void NavigateToSection(Type pageType)
    {
        if (IsLoaded) RootNav.Navigate(pageType);
        else _pendingNavigate = pageType;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndReady = true;
        CaptureExclusion.Apply(this, _settings.Current.Privacy.ExcludeWindowsFromCapture);
        if (_stateStore.Load("main") is { } p)
        {
            // Restore size before clamping so the clamp sees the real extents; reject
            // degenerate sizes from a hand-edited file (throwaway state, never trusted).
            if (p.Width is { } w && w >= MinWidth) Width = w;
            if (p.Height is { } h && h >= MinHeight) Height = h;
            (Left, Top) = ScreenClamp.Clamp(p.X, p.Y, Width, Height,
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        }
    }

    // Genuinely closable: no e.Cancel, no Hide (contrast LiveViewWindow/OverlayWindow).
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _stateStore.Save("main", new WindowPlacement(Left, Top, Width, Height));
    }

    protected override void OnClosed(EventArgs e)
    {
        // The VM and the settings service outlive this window (a fresh window is created per
        // open): unhook, or every reopen would leak its predecessor via these subscriptions.
        _vm.Errors.Messages.CollectionChanged -= OnMessagesChanged;
        _settings.Changed -= OnSettingsChanged;
        DependencyPropertyDescriptor
            .FromProperty(InfoBar.IsOpenProperty, typeof(InfoBar))
            .RemoveValueChanged(ErrorBar, OnErrorBarIsOpenChanged);
        base.OnClosed(e);
    }

    private void OnNavSelectionChanged(NavigationView sender, RoutedEventArgs args)
    {
        if (sender.SelectedItem is not NavigationViewItem { Tag: string tag }) return;
        if (tag == "Record")
        {
            // Record is a command, not a section: the item's Command (OpenConsoleCommand) opens the
            // console on click. This handler only bounces the selection back to the active section so
            // the indicator never parks on Record. (Wpf.Ui gates SelectionChanged on TargetPageType,
            // so for the no-page Record item this branch may not even fire - kept as harmless defense;
            // the Command is the actual trigger.) Re-navigating to the current page is idempotent.
            RootNav.Navigate(SectionPageType(_vm.SelectedSection));
            return;
        }
        _vm.SelectedSection = tag;
    }

    private static Type SectionPageType(string section) => section switch
    {
        "Search" => typeof(Pages.SearchPage),
        "Matters" => typeof(Pages.MattersPage),
        "Settings" => typeof(Pages.SettingsPage),
        _ => typeof(Pages.SessionsPage),
    };

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => SyncInfoBar();

    // User clicked the InfoBar close button: drop the shown message; SyncInfoBar (via the
    // resulting CollectionChanged) then re-opens the bar if more messages are queued.
    private void OnErrorBarIsOpenChanged(object? sender, EventArgs e)
    {
        if (!ErrorBar.IsOpen && _vm.Errors.Messages.Count > 0) _vm.Errors.DismissOldest();
    }

    private void SyncInfoBar()
    {
        var messages = _vm.Errors.Messages;
        ErrorBar.Message = messages.Count > 0 ? messages[0] : string.Empty;
        ErrorBar.IsOpen = messages.Count > 0;
    }

    private void OnSettingsChanged(Settings oldSettings, Settings newSettings)
    {
        if (!CaptureExclusionPolicy.ShouldReapply(oldSettings, newSettings)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_hwndReady)
                CaptureExclusion.Apply(this, newSettings.Privacy.ExcludeWindowsFromCapture);
        });
    }
}
