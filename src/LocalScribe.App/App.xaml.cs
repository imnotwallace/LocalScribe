using System.Windows;
using LocalScribe.App.Services;
using LocalScribe.Core.Storage;
using Whisper.net.LibraryLoader;
namespace LocalScribe.App;

public partial class App : Application
{
    private const string InstanceName = "LocalScribe";

    private SingleInstance? _singleInstance;
    private TrayIconHost? _tray;
    private OverlayWindow? _overlay;
    private ViewModels.OverlayViewModel? _overlayVm;
    private System.Windows.Threading.DispatcherTimer? _timer;
    private readonly CancellationTokenSource _shutdownCts = new();

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

        // (1) Single-instance guard (design 7.2, Task 12's exact API): the second instance
        // pings the holder and exits before building anything. The activate callback fires on
        // the guard's background wait thread, so it is dispatch-wrapped as SingleInstance
        // requires.
        _singleInstance = SingleInstance.TryAcquire(InstanceName,
            onActivateRequested: () => Dispatcher.BeginInvoke(() => _tray?.OpenMainWindow()));
        if (_singleInstance is null)
        {
            // Return value intentionally discarded: reachable holder or not, this instance
            // exits either way (SignalExisting never throws, by Task 12's contract).
            _ = SingleInstance.SignalExisting(InstanceName);
            Shutdown();
            return;
        }

        // (2) Composition root (Task 10 seam inside): the controller and capture provider
        // resolve settings via Func<Settings> at StartAsync, so a save applies at the NEXT
        // Start. Held in a local so every closure below captures a non-null graph.
        var comp = CompositionRoot.Build();

        // (3) First-run consent (design 6.3, Task 22): modal, BEFORE any tray/overlay/window
        // exists. Detection is field-absence, not file-absence; Decline (or dismissing the
        // dialog) shuts the app down without persisting anything.
        if (comp.Settings.Current.ConsentNotice is null)
        {
            var consentVm = new ViewModels.ConsentViewModel(
                comp.Settings, TimeProvider.System, comp.AppVersion);
            if (new ConsentDialog(consentVm).ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        // (4) Live-session VMs (3b) + Stage 4 page VMs, all sharing one dispatch seam.
        // SessionViewModel still takes a plain Settings snapshot; Stage 4 policy is
        // next-Start effect anyway (design 6.2).
        Action<Action> dispatch = a => Dispatcher.BeginInvoke(a);
        var session = new ViewModels.SessionViewModel(comp.Controller, comp.Settings.Current,
            dispatch);
        var lines = new ViewModels.TranscriptLinesViewModel(comp.Controller, dispatch);

        // One WindowStateStore serves overlay + main + read views (keyed entries in
        // window-state.json; spec 7: throwaway UI state, NOT settings).
        string stateStorePath = System.IO.Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "LocalScribe", "window-state.json");
        var windowState = new ViewModels.WindowStateStore(stateStorePath);

        // Singleton VMs: the error queue and every page's state survive MainWindow
        // close/reopen (the WINDOW is re-created per open; these are not).
        var errors = new InfoBarErrorReporter(dispatch);
        var mainVm = new ViewModels.MainWindowViewModel(errors);
        var sessionsVm = new ViewModels.SessionsPageViewModel(comp.Maintenance, session,
            comp.Windows, errors, dispatch, TimeProvider.System,
            revealInExplorer: id =>
            {
                // Same shell-out TrayIconHost's "Open sessions folder" uses; the path is
                // built via StoragePaths (spec 3.2), never assembled by the VM.
                string dir = comp.Paths.SessionDir(id);
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", dir);
            });
        var editorVm = new ViewModels.MetadataEditorViewModel(comp.Maintenance, session,
            errors, dispatch, TimeProvider.System);
        var mattersVm = new ViewModels.MattersPageViewModel(comp.Maintenance,
            new MatterDeleter(comp.Paths, comp.RecycleBin), errors, dispatch);
        var settingsVm = new ViewModels.SettingsPageViewModel(comp.Settings, comp.Maintenance,
            new RegistryLaunchAtLogin(),
            pickFolder: () =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                { Title = "Choose the LocalScribe storage folder" };
                return dialog.ShowDialog() == true ? dialog.FolderName : null;
            },
            openFolder: p => System.Diagnostics.Process.Start("explorer.exe", p),
            errors, dispatch);

        // Read views (Tasks 19/20): one window per session id; a second request activates the
        // existing window instead of duplicating. WindowRegistry keeps the close hooks for the
        // delete flow (Task 17); this map adds the activate half the registry does not carry.
        var readViews = new Dictionary<string, ReadViewWindow>(StringComparer.Ordinal);
        Action<string> openReadView = sessionId =>
        {
            if (readViews.TryGetValue(sessionId, out var existing))
            {
                existing.Activate();
                return;
            }
            var readVm = new ViewModels.ReadViewViewModel(comp.Maintenance, comp.Paths,
                comp.Settings, errors, new MediaPlayerDualAudioPlayer(), dispatch,
                TimeProvider.System);
            var window = new ReadViewWindow(readVm, sessionId, comp.Windows, windowState,
                comp.Settings);
            readViews[sessionId] = window;
            window.Closed += (_, _) => { readViews.Remove(sessionId); readVm.Dispose(); };
            window.Show();
        };
        sessionsVm.OpenReadViewRequested += openReadView;
        // Matters-page "Open" jump: concretely, the session's read view. In-list selection is
        // a Sessions-page navigation concern MainWindow does not expose; the read view IS the
        // session, which is what the organizer jump is for (design 4.1).
        mattersVm.JumpToSessionRequested += openReadView;

        // Tray with the re-creating MainWindow factory (Task 14's 5-arg ctor; MainWindow
        // widened by this task). Pages are humble shells built fresh per window open - a WPF
        // element cannot be re-hosted across windows - around the singleton VMs above.
        _tray = new TrayIconHost(session, lines, comp.Paths, comp.Settings,
            mainWindowFactory: () => new MainWindow(mainVm, windowState, comp.Settings,
                new StaticPageProvider(new Dictionary<Type, object>
                {
                    [typeof(Pages.SessionsPage)] = new Pages.SessionsPage(sessionsVm, editorVm),
                    [typeof(Pages.MattersPage)] = new Pages.MattersPage(mattersVm),
                    [typeof(Pages.SettingsPage)] = new Pages.SettingsPage(settingsVm),
                })));

        // (5) Overlay singleton (design decision 12): shown/hidden - never closed - as
        // OverlayViewModel.IsVisible flips with State. Timer wiring as in 3b.
        _overlayVm = new ViewModels.OverlayViewModel(session, comp.Settings.Current);
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

        // (6) Stage 4: the manager window is the launch surface (the tray remains the consent
        // surface and the only Exit; MainWindow genuinely closes and reopens from the tray).
        _tray.OpenMainWindow();

        // (7) Startup scan (Task 23): recovery scan, then index rebuild, AFTER the tray is up
        // so balloons have somewhere to land; never blocks Start or the UI. The Sessions page
        // shows its "checking for interrupted sessions..." banner until ScanCompleted, which
        // completes even on fault/cancel - the banner always clears.
        Action<string> notify = m => Dispatcher.BeginInvoke(() => _tray?.ShowNotice(m));
        var orchestrator = new StartupOrchestrator(
            recoverAll: ct => comp.Maintenance.RecoverAllAsync(ct),
            rebuildIndex: ct => comp.Maintenance.RebuildIndexAsync(ct),
            new TrayNoticeReporter(notify),
            notify);
        sessionsVm.IsScanning = true;
        comp.Maintenance.StartupScanTask = orchestrator.RunAsync(_shutdownCts.Token);
        _ = orchestrator.ScanCompleted.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            sessionsVm.IsScanning = false;
            sessionsVm.RefreshCommand.Execute(null);   // recovered rows re-list finalized (3.1)
        }), TaskScheduler.Default);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdownCts.Cancel();                   // stop an in-flight startup scan politely
        _timer?.Stop();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
