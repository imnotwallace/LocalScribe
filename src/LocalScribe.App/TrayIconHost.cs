using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using H.NotifyIcon;
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
    private readonly StoragePaths _paths;
    private LiveViewWindow? _liveView;

    public TrayIconHost(SessionViewModel session, TranscriptLinesViewModel lines, StoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(lines);
        (_session, _lines, _paths) = (session, lines, paths);

        _icon = new TaskbarIcon { ToolTipText = "LocalScribe - idle" };
        _icon.ContextMenu = BuildMenu();
        _icon.TrayMouseDoubleClick += (_, _) => OpenLiveView();
        _session.PropertyChanged += OnSessionChanged;
        _session.NoticeRaised += OnNoticeRaised;
        UpdateIcon(SessionState.Idle);
        _icon.ForceCreate();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(Bound("Start recording", _session.StartCommand));
        menu.Items.Add(Bound("Pause / Resume", _session.PauseResumeCommand));
        menu.Items.Add(Bound("Stop", _session.StopCommand));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Open live view", (_, _) => OpenLiveView()));
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

    private void OpenLiveView()
    {
        _liveView ??= new LiveViewWindow(_session, _lines);
        _liveView.Show();
        _liveView.Activate();
    }

    private void OnSessionChanged(object? _, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionViewModel.State)) UpdateIcon(_session.State);
    }

    // [ObservableProperty] gates PropertyChanged(LastNotice) on equality, so a second identical
    // notice (e.g. the same degraded-system-audio privacy warning on a later session) would
    // never re-fire off that property. NoticeRaised fires unconditionally instead.
    private void OnNoticeRaised(string notice) => _icon.ShowNotification("LocalScribe", notice);

    private void UpdateIcon(SessionState state)
    {
        (Brush brush, string tip) = state switch
        {
            SessionState.Recording => (Brushes.Red, "LocalScribe - RECORDING"),
            SessionState.Paused => (Brushes.Orange, "LocalScribe - paused"),
            SessionState.Finalizing => (Brushes.Gray, "LocalScribe - finalizing..."),
            _ => (Brushes.Gray, "LocalScribe - idle"),
        };
        _icon.ToolTipText = tip;
        // H.NotifyIcon 2.3.0: the generated icon is set via TaskbarIcon.IconSource with a
        // GeneratedIconSource (there is no top-level GeneratedIcon type/property in this
        // version - confirmed by reflecting the installed package) and the Task-1 placeholder
        // already used this exact type for the plain gray dot.
        // ASCII-only source rule: the glyph stays a \u escape, never a literal.
        _icon.IconSource = new GeneratedIconSource
        { Text = "\u25CF", Foreground = brush, FontSize = 46 };
    }

    public void Dispose()
    {
        _session.PropertyChanged -= OnSessionChanged;
        _session.NoticeRaised -= OnNoticeRaised;
        _icon.Dispose();
    }
}
