using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
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
    public sealed record LiveViewContext(SessionViewModel Session, TranscriptLinesViewModel Lines, RecordingConsoleViewModel Console);

    private readonly TranscriptLinesViewModel _lines;
    private readonly ISettingsService _settings;
    private readonly RecordingConsoleViewModel _console;
    private bool _stickToBottom = true;
    private bool _hwndReady;
    private readonly DispatcherTimer _remoteTargetPoll = new() { Interval = TimeSpan.FromSeconds(2) };

    public LiveViewWindow(SessionViewModel session, TranscriptLinesViewModel lines,
        RecordingConsoleViewModel console, ISettingsService settings)
    {
        InitializeComponent();
        (_lines, _settings, _console) = (lines, settings, console);
        DataContext = new LiveViewContext(session, lines, console);
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
        Hide();
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
