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
    private readonly Func<MainWindow> _mainWindowFactory;
    private LiveViewWindow? _liveView;
    private MainWindow? _main;

    public TrayIconHost(SessionViewModel session, TranscriptLinesViewModel lines,
        RecordingConsoleViewModel console, StoragePaths paths,
        ISettingsService settingsService, Func<MainWindow> mainWindowFactory)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(console);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(mainWindowFactory);
        (_session, _lines, _console, _paths, _settingsService, _mainWindowFactory) =
            (session, lines, console, paths, settingsService, mainWindowFactory);

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
        _liveView ??= new LiveViewWindow(_session, _lines, _console, _settingsService);
        _liveView.Show();
        _liveView.Activate();
    }

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
