using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
namespace LocalScribe.App;

/// <summary>Thin shell over the shared VMs. Bottom-sticky auto-scroll: follows new lines only
/// while the user is at the bottom. Closing HIDES - a recording must never die with a window;
/// only tray Exit shuts the app down. Capture-excluded per settings.Privacy (design section 2);
/// this is a hide-on-close singleton that lives for the app lifetime, so the Changed
/// subscription is intentionally never removed.</summary>
public partial class LiveViewWindow
{
    public sealed record LiveViewContext(SessionViewModel Session, TranscriptLinesViewModel Lines,
        RecordingConsoleViewModel Console, CompactConsoleViewModel Compact);

    private readonly TranscriptLinesViewModel _lines;
    private readonly ISettingsService _settings;
    private readonly RecordingConsoleViewModel _console;
    // Compact mode (design 2026-07-18 section 6): VM + placement store. The VM (like this
    // hide-on-close singleton and its Changed subscription) intentionally lives for the app
    // lifetime, so it is never disposed here.
    private readonly CompactConsoleViewModel _compact;
    private readonly WindowStateStore _stateStore;
    private Rect? _normalBounds;
    private WindowState _normalWindowState = WindowState.Normal;
    private double _normalCaptionHeight = -1;
    private bool _stickToBottom = true;
    private bool _hwndReady;
    private readonly DispatcherTimer _remoteTargetPoll = new() { Interval = TimeSpan.FromSeconds(2) };

    public LiveViewWindow(SessionViewModel session, TranscriptLinesViewModel lines,
        RecordingConsoleViewModel console, ISettingsService settings, WindowStateStore stateStore)
    {
        InitializeComponent();
        (_lines, _settings, _console, _stateStore) = (lines, settings, console, stateStore);
        _compact = new CompactConsoleViewModel(session, lines, settings);
        DataContext = new LiveViewContext(session, lines, console, _compact);
        // Geometry rides the VM's IsCompact flips (auto-compact-on-start included): the XAML
        // triggers swap the templates, this swaps the window shell around them.
        _compact.PropertyChanged += OnCompactChanged;
        if (_compact.IsCompact) EnterCompactLayout();   // constructed mid-recording with the option on
        lines.Lines.CollectionChanged += OnLinesChanged;
        settings.Changed += OnSettingsChanged;
        // Stage 6.2 Task 7 (+ review fix): refresh the matter picker's catalog every time this
        // hide-on-close singleton window becomes VISIBLE, not just once on first construction -
        // a matter created via the Matters page while the console was hidden must appear the
        // next time it is shown, not only after an app restart. IsVisibleChanged fires on the
        // very first Show() too, so it subsumes the old Loaded-only trigger; using it alone
        // avoids a double initial load.
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) LoadMattersSafely(); };
        // Capture Scope Control (design 2026-07-12 section 4): keep the live app list fresh while
        // the console is visible - refresh immediately on show and every 2 s thereafter; stop the
        // poll while hidden so a background hide-on-close singleton does not churn CPU forever.
        _remoteTargetPoll.Tick += (_, _) => RefreshRemoteTargetsSafely();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) { RefreshRemoteTargetsSafely(); _remoteTargetPoll.Start(); }
            else _remoteTargetPoll.Stop();
        };
    }

    // Fire-and-forget wrapper: LoadMattersAsync is already best-effort internally, but an async
    // void handler must never let an exception escape regardless, so a catalog read failure can
    // never crash this hide-on-close singleton window - the picker just stays as it was until
    // the next successful visible-refresh. Logs so a broken matters index is visible in
    // diagnostics instead of silently leaving the picker stale.
    private async void LoadMattersSafely()
    {
        try { await _console.LoadMattersAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"LoadMattersAsync failed: {ex}"); }
    }

    // Async-void safe wrapper: RefreshRemoteTargetsAsync is best-effort internally, but a handler
    // must never let an exception escape and crash this hide-on-close singleton.
    private async void RefreshRemoteTargetsSafely()
    {
        try { await _console.RefreshRemoteTargetsAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"RefreshRemoteTargetsAsync failed: {ex}"); }
    }

    private void RemoteTargetCombo_DropDownOpened(object? sender, EventArgs e)
        => RefreshRemoteTargetsSafely();   // immediate refresh on open (design section 4)

    // Both the idle and live pickers route here. SelectedItem is OneWay, so a user pick is NOT yet
    // committed to the VM - fire the command (idle -> override only; recording -> confirm-gated
    // hot-swap). The command's idempotency guard absorbs the echo when it re-points the selection.
    private void RemoteTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var option = e.AddedItems[0] as ViewModels.RemoteTargetOption;
        if (option is not null) _console.ChangeRemoteTargetCommand.Execute(option);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndReady = true;
        CaptureExclusion.Apply(this, _settings.Current.Privacy.ExcludeWindowsFromCapture);
    }

    // ISettingsService.Changed carries no thread contract; marshal to the UI thread before
    // touching the HWND. _hwndReady guards a save landing before the window was first shown.
    private void OnSettingsChanged(Settings oldSettings, Settings newSettings)
    {
        if (!CaptureExclusionPolicy.ShouldReapply(oldSettings, newSettings)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_hwndReady)
                CaptureExclusion.Apply(this, newSettings.Privacy.ExcludeWindowsFromCapture);
        });
    }

    private void OnLinesChanged(object? _, NotifyCollectionChangedEventArgs e)
    {
        if (_stickToBottom && _lines.Lines.Count > 0)
            LineList.ScrollIntoView(_lines.Lines[^1]);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (FindScrollViewer(LineList) is { } sv)
            sv.ScrollChanged += (_, args) =>
                _stickToBottom = args.VerticalOffset >= args.ExtentHeight - args.ViewportHeight - 2;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;                       // hide, never close
        if (_compact.IsCompact)                // remember the pill spot across hide/show
            _stateStore.Save("consoleCompact", new WindowPlacement(Left, Top));
        Hide();
    }

    // ---- Compact mode geometry (design 2026-07-18 section 6). UI-only: nothing in here touches
    // capture or session state; the VM owns WHEN, this owns only the window shell. ----

    private void OnCompactChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CompactConsoleViewModel.IsCompact)) return;
        if (_compact.IsCompact) EnterCompactLayout(); else ExitCompactLayout();
    }

    private void EnterCompactLayout()
    {
        // Remember the full-console geometry. Normalize a maximized window first: Left/Top/
        // Width/Height of a maximized window report its RESTORE bounds, not what is on screen.
        _normalWindowState = WindowState;
        if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        _normalBounds = new Rect(Left, Top, Width, Height);
        // FluentWindow keeps a WindowChrome whose caption strip would swallow clicks on the top
        // half of a 64px pill - zero it while compact (restored on exit). Null-guarded: if the
        // library ever stops attaching one, compact still works, just with a caption-drag strip.
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            _normalCaptionHeight = chrome.CaptionHeight;
            chrome.CaptionHeight = 0;
        }
        ResizeMode = ResizeMode.NoResize;
        (MinWidth, MinHeight) = (CompactConsoleViewModel.PillWidth, CompactConsoleViewModel.PillHeight);
        (Width, Height) = (CompactConsoleViewModel.PillWidth, CompactConsoleViewModel.PillHeight);
        // Remembered pill position, clamped to the visible virtual screen (a monitor may be gone
        // since last run - the overlay pill's exact restore pattern); NaN falls back to top-right.
        var saved = _stateStore.Load("consoleCompact");
        var (x, y) = ScreenClamp.Clamp(saved?.X ?? double.NaN, saved?.Y ?? double.NaN,
            CompactConsoleViewModel.PillWidth, CompactConsoleViewModel.PillHeight,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        (Left, Top) = (x, y);
    }

    private void ExitCompactLayout()
    {
        _stateStore.Save("consoleCompact", new WindowPlacement(Left, Top));
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome && _normalCaptionHeight >= 0)
            chrome.CaptionHeight = _normalCaptionHeight;
        ResizeMode = ResizeMode.CanResize;
        (MinWidth, MinHeight) = (420, 300);    // the XAML-authored minimums (window element line 5)
        if (_normalBounds is { } b)
            (Left, Top, Width, Height) = (b.X, b.Y, b.Width, b.Height);
        WindowState = _normalWindowState;
    }

    // Drag-to-move on the pill background (buttons handle their own mouse-down, so they never
    // reach this). DragMove returns when the drag ends - persist the spot right there.
    private void CompactPill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        DragMove();
        _stateStore.Save("consoleCompact", new WindowPlacement(Left, Top));
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } deep) return deep;
        }
        return null;
    }
}
