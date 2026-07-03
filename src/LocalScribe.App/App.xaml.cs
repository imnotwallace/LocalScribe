using System.Windows;
using Whisper.net.LibraryLoader;
namespace LocalScribe.App;

public partial class App : Application
{
    private TrayIconHost? _tray;
    private OverlayWindow? _overlay;
    private ViewModels.OverlayViewModel? _overlayVm;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private Services.SingleInstance? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance guard (design 7.2): Stage 4 makes matters.json read-modify-write
        // load-bearing and two instances could double-record. The second instance pings the
        // holder (activate -> open the manager) and exits before building anything. The
        // callback is dispatch-wrapped here, as SingleInstance requires: it fires on the
        // guard's background wait thread.
        _singleInstance = Services.SingleInstance.TryAcquire("LocalScribe",
            onActivateRequested: () => Dispatcher.BeginInvoke(() => _tray?.OpenMainWindow()));
        if (_singleInstance is null)
        {
            Services.SingleInstance.SignalExisting("LocalScribe");
            Shutdown();
            return;
        }

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
        // One WindowStateStore serves overlay + main (keyed entries in window-state.json;
        // spec 7: throwaway UI state, NOT settings). Lives next to settings.json.
        string stateStorePath = System.IO.Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "LocalScribe", "window-state.json");
        var windowState = new ViewModels.WindowStateStore(stateStorePath);

        // Manager shell (design section 2): the VM is a singleton (the error queue survives
        // close/reopen) but the WINDOW is re-created per open - MainWindow genuinely closes,
        // and TrayIconHost owns the lazily re-creating field.
        var errors = new Services.InfoBarErrorReporter(a => Dispatcher.BeginInvoke(a));
        var mainVm = new ViewModels.MainWindowViewModel(errors);
        _tray = new TrayIconHost(session, lines, paths, settingsService,
            mainWindowFactory: () => new MainWindow(mainVm, windowState, settingsService));

        // Overlay singleton (design decision 12): shown/hidden - never closed - as
        // OverlayViewModel.IsVisible flips with State.
        _overlayVm = new ViewModels.OverlayViewModel(session, settingsService.Current);
        _overlay = new OverlayWindow(_overlayVm, windowState);
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
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
