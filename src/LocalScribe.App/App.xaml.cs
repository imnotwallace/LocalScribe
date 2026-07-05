using System.Windows;
using LocalScribe.App.Services;
using LocalScribe.Core.Storage;
using Whisper.net.LibraryLoader;
using Wpf.Ui.Appearance;
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

        // Stage 5.1: make WPF-UI theming authoritative and OS-following. Apply BEFORE any window
        // is constructed so brushes resolve correctly on first render. Watch is deferred to the
        // pump (Step below) to stay clear of the pre-pump invisible-Mica gotcha.
        ApplicationThemeManager.ApplySystemTheme();

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
        var lines = new ViewModels.TranscriptLinesViewModel(comp.Controller, comp.Settings, dispatch);

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

        // Split-speakers dialog factory (Task 9): a fresh VM + window per request - unlike the
        // read view, there is no dedup/reuse map here (the dialog is a short-lived run-then-
        // confirm flow, not something a user re-opens repeatedly for the same session while one
        // is already up). Declared BEFORE openReadView, which passes it through to
        // ReadViewWindow's ctor so the read view's own "Split speakers..." button can invoke it
        // (a lambda cannot reference a local variable declared later in the same method).
        Action<string> openSplitSpeakers = sessionId =>
        {
            var splitVm = new ViewModels.SplitSpeakersViewModel(comp.Diarisation, comp.Maintenance,
                comp.Paths, comp.Settings, errors, dispatch, TimeProvider.System,
                LocalScribe.Core.Transcription.ModelPaths.Resolve);
            new SplitSpeakersWindow(splitVm, sessionId, comp.Windows, comp.Settings).Show();
        };

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
                comp.Settings, openSplitSpeakers);
            readViews[sessionId] = window;
            window.Closed += (_, _) => { readViews.Remove(sessionId); readVm.Dispose(); };
            window.Show();
        };
        sessionsVm.OpenReadViewRequested += openReadView;

        // Session Details windows (Stage 5.2 Task 4): one window per session id, same
        // dedup/activate pattern as readViews - a FRESH MetadataEditorViewModel per window; this
        // is the only editor path now that Task 8 removed the interim Sessions-page drawer and
        // its app-lifetime singleton editor. MetadataEditorViewModel.Dispose() detaches its
        // _session.PropertyChanged subscription (Task 4's leak fix) so a closed details window's
        // editor doesn't stay rooted by the shared SessionViewModel.
        var sessionDetailsWindows = new Dictionary<string, SessionDetailsWindow>(StringComparer.Ordinal);
        Action<string> openSessionDetails = sessionId =>
        {
            if (sessionDetailsWindows.TryGetValue(sessionId, out var existing))
            {
                existing.Activate();
                return;
            }
            var detailEditor = new ViewModels.MetadataEditorViewModel(comp.Maintenance, session,
                errors, dispatch, TimeProvider.System);
            // Stage 5.3 Task 7: Split speakers relocated into this window (the Sessions-list
            // context menu path was retired) - the editor's own DiariseCommand raises this.
            detailEditor.DiariseRequested += openSplitSpeakers;
            // Stage 5.4 4.4: a settled Session Details save refreshes just that grid row in place
            // (mirrors the DiariseRequested wiring). RefreshRowAsync catches its own faults, so
            // fire-and-forget is safe. Covers both the Sessions-page open and the Matters jump -
            // both routes construct the window through this one factory.
            detailEditor.Saved += id => _ = sessionsVm.RefreshRowAsync(id);
            var window = new SessionDetailsWindow(detailEditor, sessionId, comp.Windows, windowState,
                comp.Settings);
            sessionDetailsWindows[sessionId] = window;
            window.Closed += (_, _) =>
            {
                sessionDetailsWindows.Remove(sessionId);
                detailEditor.Dispose();
                _ = sessionsVm.RefreshRowAsync(sessionId);   // Stage 5.4 4.4: backstop if a save landed late / X was used
            };
            window.Show();
        };
        sessionsVm.OpenSessionDetailsRequested += openSessionDetails;

        // Matters-page "Open" jump (Stage 5.2 design 4.1/line 124): reuses the same Session
        // Details window as the Sessions page, not the read view. The read view stays reachable
        // from the Sessions page only.
        mattersVm.OpenSessionDetailsRequested += openSessionDetails;

        // Tray with the re-creating MainWindow factory (Task 14's 5-arg ctor; MainWindow
        // widened by this task). Pages are humble shells built fresh per window open - a WPF
        // element cannot be re-hosted across windows - around the singleton VMs above.
        _tray = new TrayIconHost(session, lines, comp.Paths, comp.Settings,
            mainWindowFactory: () => new MainWindow(mainVm, windowState, comp.Settings,
                new StaticPageProvider(new Dictionary<Type, object>
                {
                    [typeof(Pages.SessionsPage)] = new Pages.SessionsPage(sessionsVm),
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
        // Deferred to ApplicationIdle (i.e. after OnStartup returns and Application.Run's message
        // loop is pumping): a WPF-UI FluentWindow shown SYNCHRONOUSLY here - before the pump is
        // running - failed to composite its Mica backdrop on Win11 and came up invisible, so a
        // normal launch surfaced only a tray icon. The first-run consent dialog masked this because
        // its ShowDialog runs a nested pump that warms composition first.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            // Watch a persistent HWND so light/dark tracks the OS for the whole session,
            // regardless of which transient windows are open. The overlay lives the whole
            // session (shown/hidden, never closed); ensure its handle exists before watching.
            new System.Windows.Interop.WindowInteropHelper(_overlay!).EnsureHandle();
            SystemThemeWatcher.Watch(_overlay!);
            _tray?.OpenMainWindow();
        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

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
