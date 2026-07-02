using System.Windows;
using Whisper.net.LibraryLoader;
namespace LocalScribe.App;

public partial class App : Application
{
    private TrayIconHost? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Host responsibility (see LiveRunner): native backend order, once per process.
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];

        var (controller, settings, paths) = CompositionRoot.Build();
        var session = new ViewModels.SessionViewModel(controller, settings,
            dispatch: a => Dispatcher.BeginInvoke(a));
        _tray = new TrayIconHost(session, paths);        // Task 4 (this task: icon + Exit only)
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
