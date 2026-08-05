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
    private LiveViewWindow? _liveView;
    private MainWindow? _main;

    public TrayIconHost(SessionViewModel session, TranscriptLinesViewModel lines,
        RecordingConsoleViewModel console, StoragePaths paths,
        ISettingsService settingsService, WindowStateStore windowState,
        Action<string, string>? openExport,
        Func<MainWindow> mainWindowFactory,
        IDiagnosticLog? log = null)
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
            try
            {
                if (_session.State is SessionState.Recording or SessionState.Paused)
                {
                    if (MessageBox.Show("A recording is in progress. Stop and exit?",
                            "LocalScribe", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                        != MessageBoxResult.Yes) return;
                    await _session.StopCommand.ExecuteAsync(null);   // never kill a live recording silently
                }
                else if (_session.State == SessionState.Finalizing)
                {
                    // A stop is already in flight (e.g. Exit clicked right after Stop) - do not
                    // re-confirm, but never Shutdown() mid-write and abandon the evidentiary
                    // session.json + projection regen.
                    if (_session.StopCommand.ExecutionTask is { } finalize) await finalize;
                }
            }
            catch (Exception ex)
            {
                // A StopAsync fault here must not become an unhandled async-void exception -
                // surface it and still exit (the user already asked to exit).
                _icon.ShowNotification("LocalScribe", "Error stopping recording: " + ex.Message);
            }
            // Tier 1 plan A (2026-08-05, fix round 1): a BOUNDED await, on the app's only Exit,
            // before the process starts tearing down - App.OnExit is the backstop for the OTHER
            // shutdown routes, but this line has to reach Shutdown() itself for OnExit to ever run
            // at all. REJECTED: an unbounded `await FlushAsync(...)` (round 1's shape) - if the
            // drain is wedged (dead disk, vanished network path, antivirus holding the file) this
            // line never completes, so Shutdown() below never runs, so OnExit never runs either,
            // and the user is left with a tray process only Task Manager can end. Task.WhenAny
            // against a Task.Delay bounds the wait regardless of whether FlushAsync's
            // CancellationToken is ever honoured (it is documented never to throw, so it may not
            // observe the token at all). ShutdownFlush.Timeout is the SAME constant App.OnExit's
            // backstop bounds its own wait with, so the two routes cannot silently drift apart the
            // way this one's hardcoded literal already had.
            //
            // F14 (final whole-branch review, 2026-08-05): ONE SHARED CONSTANT, NOT ONE SHARED
            // CEILING - an earlier version of this comment implied the latter. The two waits are
            // ADDITIVE on this path: this line waits up to ShutdownFlush.Timeout, then
            // Application.Current.Shutdown() below runs App.OnExit, which waits up to
            // ShutdownFlush.Timeout AGAIN on the same still-wedged chain. So with a dead network
            // storage root, tray Exit takes 2 s + 2 s = 4 s, not 2 s. That is ACCEPTED and
            // deliberately not changed: both bounds are needed independently (OnExit is the
            // backstop for every OTHER route into shutdown, which never passes through here), the
            // worst case is bounded and small, and it only occurs when the disk is already gone.
            // Nothing here should be "optimised" by dropping one of them.
            try
            {
                Task flush = _log?.FlushAsync(CancellationToken.None) ?? Task.CompletedTask;
                await Task.WhenAny(flush, Task.Delay(ShutdownFlush.Timeout));
            }
            catch { }
            Application.Current.Shutdown();
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
