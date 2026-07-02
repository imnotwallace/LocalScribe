using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using H.NotifyIcon;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Storage;
namespace LocalScribe.App;

/// <summary>Placeholder for Task 4 (replaced there). Ships only a gray generated icon and a
/// single Exit item so the app is runnable/killable in this task.</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;

    public TrayIconHost(SessionViewModel session, StoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(paths);

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Application.Current.Shutdown();

        _icon = new TaskbarIcon
        {
            IconSource = new GeneratedIconSource { Background = Brushes.Gray },
            ToolTipText = "LocalScribe",
            ContextMenu = new ContextMenu { Items = { exit } },
        };
        _icon.ForceCreate();
    }

    public void Dispose() => _icon.Dispose();
}
