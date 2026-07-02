using System.Windows;
using Whisper.net.LibraryLoader;
namespace LocalScribe.App;

public partial class App : Application
{
    private TrayIconHost? _tray;
    private System.Windows.Threading.DispatcherTimer? _timer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Host responsibility (see LiveRunner): native backend order, once per process.
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];

        var (controller, settings, paths) = CompositionRoot.Build();
        var session = new ViewModels.SessionViewModel(controller, settings,
            dispatch: a => Dispatcher.BeginInvoke(a));
        var lines = new ViewModels.TranscriptLinesViewModel(controller, a => Dispatcher.BeginInvoke(a));
        _tray = new TrayIconHost(session, lines, paths);
        _timer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) => session.TimerTick();
        _timer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        _tray?.Dispose();
        base.OnExit(e);
    }
}
