using System.Windows;
using Whisper.net.LibraryLoader;
namespace LocalScribe.App;

public partial class App : Application
{
    private TrayIconHost? _tray;
    private OverlayWindow? _overlay;
    private ViewModels.OverlayViewModel? _overlayVm;
    private System.Windows.Threading.DispatcherTimer? _timer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Safety net: CommunityToolkit's AsyncRelayCommand (AwaitAndThrowIfFailed) rethrows a
        // faulted Stop/Pause command's exception back on the dispatcher. Without this handler
        // that becomes an unhandled exception that crashes the whole tray app. Stage 7 can add
        // real logging here; for now, swallow it - the per-command try/catch (see TrayIconHost
        // Exit handler) is the primary path for surfacing errors to the user.
        DispatcherUnhandledException += (_, ex) => { ex.Handled = true; };

        // Host responsibility (see LiveRunner): native backend order, once per process.
        RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu];

        var (controller, settingsService, paths) = CompositionRoot.Build();
        // SessionViewModel still takes a plain Settings snapshot; live propagation of saves
        // into the VMs is Task 24's wiring - Stage 4 policy is next-Start effect anyway (6.2).
        var session = new ViewModels.SessionViewModel(controller, settingsService.Current,
            dispatch: a => Dispatcher.BeginInvoke(a));
        var lines = new ViewModels.TranscriptLinesViewModel(controller, a => Dispatcher.BeginInvoke(a));
        _tray = new TrayIconHost(session, lines, paths);

        // Overlay singleton (design decision 12): shown/hidden - never closed - as
        // OverlayViewModel.IsVisible flips with State. Position is throwaway window-state.json,
        // NOT settings (spec 7); it lives next to settings.json under %APPDATA%/LocalScribe.
        _overlayVm = new ViewModels.OverlayViewModel(session, settingsService.Current);
        string stateStorePath = System.IO.Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "LocalScribe", "window-state.json");
        _overlay = new OverlayWindow(_overlayVm, new ViewModels.WindowStateStore(stateStorePath));
        _overlayVm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ViewModels.OverlayViewModel.IsVisible)) return;
            if (_overlayVm.IsVisible) _overlay.Show(); else _overlay.Hide();
        };

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
