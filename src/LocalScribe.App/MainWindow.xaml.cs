using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
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

    public MainWindow(MainWindowViewModel vm, WindowStateStore stateStore, ISettingsService settings)
    {
        InitializeComponent();
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
        Loaded += (_, _) => RootNav.Navigate(typeof(Pages.SessionsPage));
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
        if (sender.SelectedItem is NavigationViewItem { Tag: string tag })
            _vm.SelectedSection = tag;
    }

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
