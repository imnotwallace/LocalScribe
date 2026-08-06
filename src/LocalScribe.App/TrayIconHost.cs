using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using H.NotifyIcon;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Live;
using LocalScribe.Core.Storage;
namespace LocalScribe.App;

/// <summary>The load-bearing consent surface (design decision 6): recording state always
/// visible, quick controls, the app's only Exit. Pure widget assembly - every behavior lives
/// in the tested SessionViewModel; handlers here are one line into the VM.</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly SessionViewModel _session;
    private readonly TranscriptLinesViewModel _lines;
    private readonly RecordingConsoleViewModel _console;
    private readonly StoragePaths _paths;
    private readonly ISettingsService _settingsService;
    private readonly WindowStateStore _windowState;
    private readonly Func<MainWindow> _mainWindowFactory;
    // Export button in the Record console (design 2026-08-03 section 10): a thin pass-through to
    // LiveViewWindow, which declares the same nullable shape and no-ops its Export button when this
    // is null - so a caller that never wires an export seam still builds without a dummy delegate.
    private readonly Action<string, string>? _openExport;
    // Tier 1 plan A (2026-08-05): the tray is the app's ONLY Exit and its handler is genuinely
    // async, so the diagnostic flush can be awaited here rather than blocked on in App.OnExit.
    // Optional so the existing construction site and any future test double stay valid.
    private readonly IDiagnosticLog? _log;
    // Tier 1B (2026-08-05, T1-2): re-reads SessionController.PendingFinalize on every call - it is
    // a property over a REASSIGNED field, so a captured Task would be permanently stale. Nullable
    // with a no-op default, following this file's own _openExport precedent, so the existing
    // construction site and any future caller that wires no controller still builds.
    private readonly Func<Task>? _drainFinalize;
    private LiveViewWindow? _liveView;
    private MainWindow? _main;

    public TrayIconHost(SessionViewModel session, TranscriptLinesViewModel lines,
        RecordingConsoleViewModel console, StoragePaths paths,
        ISettingsService settingsService, WindowStateStore windowState,
        Action<string, string>? openExport,
        Func<MainWindow> mainWindowFactory,
        IDiagnosticLog? log = null,
        Func<Task>? drainFinalize = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(windowState);
        ArgumentNullException.ThrowIfNull(mainWindowFactory);
        (_session, _lines, _console, _paths, _settingsService, _windowState, _openExport, _mainWindowFactory, _log) =
            (session, lines, console, paths, settingsService, windowState, openExport, mainWindowFactory, log);
        _drainFinalize = drainFinalize;

        _icon = new TaskbarIcon { ToolTipText = "LocalScribe - idle" };
        _icon.IconSource = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/Assets/LocalScribe.ico"));
        _icon.ContextMenu = BuildMenu();
        _icon.TrayMouseDoubleClick += (_, _) => OpenMainWindow();   // retargeted to the manager (design section 2)
        _session.PropertyChanged += OnSessionChanged;
        _session.NoticeRaised += OnNoticeRaised;
        UpdateIcon(SessionState.Idle);
        _icon.ForceCreate();
    }

    /// <summary>The exit sequence this host's Exit menu item runs. PUBLIC so
    /// Application.SessionEnding (App.xaml.cs) runs the IDENTICAL sequence - two hand-written
    /// copies of an evidentiary shutdown path would drift, and only one of them would ever be
    /// exercised by hand. SessionEnding calls RunUnattendedAsync on the object this returns, so the
    /// MessageBox below is reached only when a human is actually there to answer it.
    ///
    /// DEVIATION from the Tier 1B plan text, deliberate (2026-08-06). The plan gave this class a
    /// SECOND new constructor parameter, `Func&lt;Task&gt;? flushDiagnostics`, and passed no `log:`
    /// to ExitSequence at all. Both were wrong against the merged tree: Plan A had already wired
    /// `log: comp.Log` into this constructor (App.xaml.cs), so a separate flush delegate would make
    /// the single call site pass the SAME object twice - once as the log, once wrapped in a lambda -
    /// and ExitSequence's own two log?.Write calls would have been permanently dead code, which is
    /// exactly the unthreaded-seam defect Task 1 Step 10 exists to prevent. One parameter is added
    /// instead of two, and the flush delegate is derived from the log this class already holds.</summary>
    public ExitSequence BuildExitSequence() => new(
        state: () => _session.State,
        stopRecording: () => _session.StopCommand.ExecuteAsync(null),
        inFlightStop: () => _session.StopCommand.ExecutionTask,
        drainFinalize: _drainFinalize ?? (() => Task.CompletedTask),
        confirmStopWhileRecording: () => MessageBox.Show(
            "A recording is in progress. Stop and exit?", "LocalScribe",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes,
        notify: m => _icon.ShowNotification("LocalScribe", m),
        // CancellationToken.None deliberately: a flush that gave up early would discard exactly the
        // lines describing the shutdown being diagnosed. The BOUND lives inside ExitSequence
        // (Task.WhenAny against ShutdownFlush.Timeout), which is where Plan A's bounded await moved
        // to - the bound was carried across with the await, not dropped.
        flushDiagnostics: () => _log?.FlushAsync(CancellationToken.None) ?? Task.CompletedTask,
        log: _log);

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Open LocalScribe", (_, _) => OpenMainWindow()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Bound("Start recording", _session.StartCommand));
        menu.Items.Add(Bound("Pause / Resume", _session.PauseResumeCommand));
        menu.Items.Add(Bound("Stop", _session.StopCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open record console", (_, _) => OpenLiveView()));
        menu.Items.Add(Item("Open sessions folder", (_, _) =>
        {
            Directory.CreateDirectory(_paths.SessionsDir);
            Process.Start("explorer.exe", _paths.SessionsDir);
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Exit", async (_, _) =>
        {
            // Tier 1B (2026-08-05, T1-2): the decision logic moved into the tested ExitSequence.
            // This handler is now confirm-free glue - the sequence owns the confirm, the stop, the
            // fault surfacing, the bounded diagnostic flush AND the PendingFinalize drain this path
            // never had. The comment block that used to sit here (Plan A's rationale for a BOUNDED
            // rather than unbounded flush, and F14's "one shared constant, not one shared ceiling"
            // note that this path's wait and OnExit's are ADDITIVE) moved with the code it explains,
            // onto ExitSequence.RunCoreAsync's flush leg. Nothing there should be "optimised" by
            // dropping one of the two bounds.
            if (await BuildExitSequence().RunAsync()) Application.Current.Shutdown();
        }));
        return menu;
    }

    private static MenuItem Bound(string header, ICommand command)
        => new() { Header = header, Command = command };   // IsEnabled follows CanExecute via WPF

    private static MenuItem Item(string header, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += onClick;
        return item;
    }

    public void OpenLiveView()
    {
        _liveView ??= new LiveViewWindow(_session, _lines, _console, _settingsService, _windowState,
            _openExport);
        _liveView.Show();
        _liveView.Activate();
    }

    /// <summary>True while the Record console is on screen (the live view is a hide-on-close
    /// singleton, so IsVisible is authoritative). The call-detect policy's console-armed
    /// suppression input (design 2026-07-18 section 5.2): with the console already open the user
    /// is mid-flow toward Start - an offer toast would only duplicate it.</summary>
    public bool IsLiveViewVisible => _liveView?.IsVisible == true;

    /// <summary>Unlike the live view (hide-on-close singleton), the main window GENUINELY
    /// closes - so the field RE-CREATES after a close. The Closed hook is the closed-flag:
    /// it nulls the field on the UI thread before another click can observe it, so a stale
    /// (closed, un-Show()-able) instance is never reused.</summary>
    public void OpenMainWindow()
    {
        if (_main is null)
        {
            _main = _mainWindowFactory();
            _main.Closed += (_, _) => _main = null;
        }
        _main.Show();
        _main.Activate();
    }

    /// <summary>Open/activate the main window, then land it on the given page (read-view
    /// "Search all sessions" hand-off).</summary>
    public void OpenMainWindowAt(Type pageType)
    {
        OpenMainWindow();
        _main!.NavigateToSection(pageType);
    }

    private void OnSessionChanged(object? _, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.State)) UpdateIcon(_session.State);
    }

    // [ObservableProperty] gates PropertyChanged(LastNotice) on equality, so a second identical
    // notice (e.g. the same degraded-system-audio privacy warning on a later session) would
    // never re-fire off that property. NoticeRaised fires unconditionally instead.
    private void OnNoticeRaised(string notice) => _icon.ShowNotification("LocalScribe", notice);

    /// <summary>Thin app-level hook into the same balloon surface OnNoticeRaised uses - lets
    /// startup/background work (recovery scan, index rebuild failures) surface tray notices
    /// without faking a controller Notice through SessionViewModel.</summary>
    public void ShowNotice(string notice) => _icon.ShowNotification("LocalScribe", notice);

    private void UpdateIcon(SessionState state)
    {
        (Brush? brush, string tip) = state switch
        {
            SessionState.Recording => (Brushes.Red, "LocalScribe - RECORDING"),
            SessionState.Paused => (Brushes.Orange, "LocalScribe - paused"),
            SessionState.Finalizing => (Brushes.Gray, "LocalScribe - finalizing..."),
            _ => (null, "LocalScribe - idle"),
        };
        _icon.ToolTipText = tip;
        if (brush is null)
        {
            // Idle: show the branded logo.
            _icon.IconSource = new System.Windows.Media.Imaging.BitmapImage(
                new Uri("pack://application:,,,/Assets/LocalScribe.ico"));
        }
        else
        {
            // Active: a state-tinted mic glyph (Fluent icon font) - visible status at a glance.
            // ASCII-only source rule: the glyph stays a \u escape.
            _icon.IconSource = new GeneratedIconSource
            { Text = "\uE720", Foreground = brush, FontSize = 40 };
        }
    }

    public void Dispose()
    {
        _session.PropertyChanged -= OnSessionChanged;
        _session.NoticeRaised -= OnNoticeRaised;
        _icon.Dispose();
    }
}
