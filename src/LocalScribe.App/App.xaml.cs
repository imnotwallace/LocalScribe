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
    // Task 8: separate 2 s timer driving the advisory app-mute tray poll (design 2026-07-11 2.2).
    // Its Poll() is inert until Recording and fail-open, so it may start alongside _timer.
    private System.Windows.Threading.DispatcherTimer? _appMuteTimer;
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

        // Cross-session search (design 2026-07-13 section 2): ONE in-memory index over the same
        // storage root, fed by the persisted self-healing cache. Built in the background after the
        // startup scan (step 7 below); queries before that see IsReady=false ("indexing...").
        // Construction does no IO. A skipped (unreadable) session is logged, never surfaced as an
        // error - it re-indexes on its next content change or the next launch.
        var searchIndex = new LocalScribe.Core.Search.SearchIndexService(
            comp.Paths, () => comp.Settings.Current, TimeProvider.System);
        searchIndex.SessionSkipped += (id, ex) => System.Diagnostics.Trace.WriteLine(
            $"search index skipped session {id}: {ex.Message}");

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
        // Task 8: advisory app-mute watcher over the Win11 tray call-mute signal. Reads UIA only
        // while Recording (isRecording gate); fail-open; the 2 s poll timer that drives it is wired
        // beside the elapsed timer below. Passed to the shared session VM so its debounced banner +
        // one-click MuteLocalCommand action light up (design 2026-07-11, Phase 2).
        var appMuteWatcher = new Services.AppMuteWatcher(new Services.TrayMuteSignalSource(),
            () => comp.Controller.State == LocalScribe.Core.Live.SessionState.Recording);
        var session = new ViewModels.SessionViewModel(comp.Controller, comp.Settings.Current,
            dispatch, matterIdsProvider: () => comp.MatterSelection.MatterIds,
            appMuteWatcher: appMuteWatcher);
        var lines = new ViewModels.TranscriptLinesViewModel(comp.Controller, comp.Settings, dispatch);

        // Stage 5.4 Phase 3: idle-console state for the Record console. Composes the shared
        // session VM; the override seam reaches capture via CompositionRoot's wrapped settings func.
        var console = new ViewModels.RecordingConsoleViewModel(comp.Settings, session,
            comp.RemoteOverride, comp.Maintenance, comp.MatterSelection,
            comp.DeviceEnumerator, comp.MicOverride, comp.Scanner,
            confirmSystemMix: () => MessageBox.Show(
                "Capturing full system mix records ALL machine audio - other apps, notifications, " +
                "both sides through your speakers. A marker will be added to the transcript. Continue?",
                "Switch to system mix", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                == MessageBoxResult.OK,
            dispatch);

        // One WindowStateStore serves overlay + main + read views (keyed entries in
        // window-state.json; spec 7: throwaway UI state, NOT settings).
        string stateStorePath = System.IO.Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "LocalScribe", "window-state.json");
        var windowState = new ViewModels.WindowStateStore(stateStorePath);

        // Save-As seam (design 3.4): remembers the last-used dir in throwaway window-state.json.
        // Declared here (before sessionsVm/mattersVm) so it is in scope for both the Task 9
        // openExport factory below and the Task 10 matter-zip export wiring on mattersVm.
        Func<Services.SavePathRequest, string?> pickSavePath = req =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = req.DefaultFileName, Filter = req.Filter };
            string? last = windowState.LoadLastExportDir();
            if (!string.IsNullOrEmpty(last) && System.IO.Directory.Exists(last)) dialog.InitialDirectory = last;
            if (dialog.ShowDialog() != true) return null;
            string? dir = System.IO.Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrEmpty(dir)) windowState.SaveLastExportDir(dir);
            return dialog.FileName;
        };
        // Open-file seam for the audio-import dialog (design 2026-07-13 section 4.4): the
        // SavePathRequest twin. No last-dir memory - received recordings come from anywhere.
        Func<Services.OpenPathRequest, string?> pickOpenPath = req =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = req.Filter, CheckFileExists = true };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        };
        // Reveal-and-highlight the produced file (design 3.4): the /select, variant of the existing
        // explorer.exe shell-outs. The path is quoted because it may contain spaces.
        Action<string> revealFile = p =>
            System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + p + "\"");

        // Singleton VMs: the error queue and every page's state survive MainWindow
        // close/reopen (the WINDOW is re-created per open; these are not).
        var errors = new InfoBarErrorReporter(dispatch);
        // Stage 5.4 section 6 (D1): the shell hosts the nav-rail Record command and the status
        // strip, so the shell VM carries the ONE shared session VM created above.
        // Nav-rail "Record" OPENS the console (idle) rather than starting capture. _tray is assigned
        // further below; the lambda captures the field and runs only on a post-startup click, so it
        // is non-null by then.
        var mainVm = new ViewModels.MainWindowViewModel(errors, session,
            openConsole: () => _tray?.OpenLiveView());
        // Audio import availability is resolved ONCE at startup (the diarizer-exe precedent):
        // FfmpegLocator checks LOCALSCRIBE_FFMPEG, then ffmpeg\ beside the app, then the repo's
        // tools\ffmpeg. Absent -> the Import button is disabled with the fetch-script tooltip.
        string? ffmpegDir = LocalScribe.Core.Import.FfmpegLocator.FindToolsDir();
        var sessionsVm = new ViewModels.SessionsPageViewModel(comp.Maintenance, session,
            comp.Windows, errors, dispatch, TimeProvider.System,
            revealInExplorer: id =>
            {
                // Same shell-out TrayIconHost's "Open sessions folder" uses; the path is
                // built via StoragePaths (spec 3.2), never assembled by the VM.
                string dir = comp.Paths.SessionDir(id);
                System.IO.Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", dir);
            },
            importAvailable: ffmpegDir is not null,
            retranscribingSessionId: () => comp.Retranscription.RunningSessionId,
            searchIndex: searchIndex);
        // Sessions-list live auto-update (design 2026-07-12 section 3): a completed background
        // finalize (success OR failure) upserts just that row in place - the row flips from
        // "Finalizing..." to its final status without a manual Refresh and with no scroll jump.
        // Marshaled through dispatch like every other controller-event handler; UpsertRowAsync
        // catches its own faults, so fire-and-forget is safe.
        comp.Controller.SessionFinalizeCompleted += id => dispatch(() => _ = sessionsVm.UpsertRowAsync(id));
        // Search-index live updates (design 2026-07-13 section 2.1): a finalized recording and any
        // gated content mutation (edit save, pins, diarisation, recovery, re-render, version
        // switch, delete) re-index just that session. ReindexSessionAsync catches everything and
        // needs no dispatcher (the index is lock-guarded), so bare fire-and-forget is safe.
        comp.Controller.SessionFinalizeCompleted += id => _ = searchIndex.ReindexSessionAsync(id, _shutdownCts.Token);
        comp.Maintenance.SessionContentChanged += id => _ = searchIndex.ReindexSessionAsync(id, _shutdownCts.Token);
        var mattersVm = new ViewModels.MattersPageViewModel(comp.Maintenance,
            new MatterDeleter(comp.Paths, comp.RecycleBin), comp.Windows, errors,
            pickSavePath, revealFile, dispatch);
        var searchVm = new ViewModels.SearchPageViewModel(searchIndex, comp.Maintenance, errors,
            dispatch, TimeProvider.System);
        var settingsVm = new ViewModels.SettingsPageViewModel(comp.Settings, comp.Maintenance,
            new RegistryLaunchAtLogin(),
            pickFolder: () =>
            {
                var dialog = new Microsoft.Win32.OpenFolderDialog
                { Title = "Choose the LocalScribe storage folder" };
                return dialog.ShowDialog() == true ? dialog.FolderName : null;
            },
            openFolder: p => System.Diagnostics.Process.Start("explorer.exe", p),
            errors, dispatch, comp.DeviceEnumerator);

        // Session Details maps hoisted ABOVE openSplitSpeakers (a lambda cannot reference a local
        // declared later in the same method - same reason openSplitSpeakers precedes openReadView).
        // The editors map mirrors the windows map so the split dialog's DiarisationSaved handler
        // can reload the OPEN editor for its session; both are registered in openSessionDetails.
        var sessionDetailsWindows = new Dictionary<string, SessionDetailsWindow>(StringComparer.Ordinal);
        var sessionDetailsEditors = new Dictionary<string, ViewModels.MetadataEditorViewModel>(StringComparer.Ordinal);

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
            // Stage 5.4 C2 Task 3 (LOCKED design): after a successful Split confirm the launching
            // editor RELOADS from disk - it is guaranteed clean (DiariseCommand gates on !IsDirty),
            // so a plain re-load can never clobber unsaved edits. Keyed by id, so BOTH launch
            // paths are covered (the Session Details button and the read view's own Split button
            // refresh an open editor alike). The grid row refreshes too (Diarised flag), mirroring
            // the detailEditor.Saved wiring below; RefreshRowAsync catches its own faults.
            splitVm.DiarisationSaved += id =>
            {
                if (sessionDetailsEditors.TryGetValue(id, out var editor))
                    _ = editor.LoadAsync(id, CancellationToken.None);
                _ = sessionsVm.RefreshRowAsync(id);
            };
            new SplitSpeakersWindow(splitVm, sessionId, comp.Windows, comp.Settings).Show();
        };

        // Versioned re-transcription (design 2026-07-13 section 3.4): a fresh VM + plain Window
        // per request (short-lived, same pattern as openExport). Hoisted above openSessionDetails
        // because the details editor's RetranscribeRequested must reference it (a lambda cannot
        // reference a local declared later - same ordering rule as openSplitSpeakers). The dialog
        // may close while the run continues; a re-opened dialog shows the in-flight state.
        Action<string> openRetranscribe = sessionId =>
        {
            var retransVm = new ViewModels.RetranscribeDialogViewModel(sessionId, comp.Maintenance,
                comp.Retranscription, LocalScribe.Core.Transcription.ModelPaths.AvailableModels,
                errors, dispatch);
            new RetranscribeDialog(retransVm) { Owner = MainWindow }.ShowDialog();
        };
        sessionsVm.RetranscribeRequested += openRetranscribe;
        // Row chip + read-view refresh: Started flips the "Re-transcribing..." chip on through
        // the same in-place upsert seam the finalize path uses; Completed (success, refusal,
        // fault, or cancel) flips it off and re-reads disk truth (ActiveVersion may have
        // changed). NotifyRosterChanged reuses the existing per-session read-view refresh
        // channel (RosterChanged -> RefreshRosterAsync -> gated reload) so an open read view
        // picks up the new active version's rows + badge without a reopen; its Edit mode
        // deliberately survives untouched (RefreshRosterAsync's documented contract).
        comp.Retranscription.RetranscriptionStarted += id => dispatch(() => _ = sessionsVm.UpsertRowAsync(id));
        comp.Retranscription.RetranscriptionCompleted += id => dispatch(() =>
        {
            _ = sessionsVm.UpsertRowAsync(id);
            comp.Windows.NotifyRosterChanged(id);
        });
        // Re-transcription completion re-indexes the session (its new version is now active).
        comp.Retranscription.RetranscriptionCompleted += id => _ = searchIndex.ReindexSessionAsync(id, _shutdownCts.Token);
        comp.Retranscription.Notice += m => dispatch(() => errors.Info(m));

        // Session Details windows (Stage 5.2 Task 4): one window per session id, same
        // dedup/activate pattern as readViews - a FRESH MetadataEditorViewModel per window; this
        // is the only editor path now that Task 8 removed the interim Sessions-page drawer and
        // its app-lifetime singleton editor. MetadataEditorViewModel.Dispose() detaches its
        // _session.PropertyChanged subscription (Task 4's leak fix) so a closed details window's
        // editor doesn't stay rooted by the shared SessionViewModel.
        // Stage 6.1: hoisted ABOVE openReadView because the read view's Reassign-speaker dialog
        // hands off to Session Details in its no-candidates state, so openReadView must be able to
        // pass this lambda through to ReadViewWindow's ctor (a lambda cannot reference a local
        // declared later in the same method - same ordering rule as openSplitSpeakers above).
        Action<string> openSessionDetails = sessionId =>
        {
            if (sessionDetailsWindows.TryGetValue(sessionId, out var existing))
            {
                existing.Activate();
                return;
            }
            var detailEditor = new ViewModels.MetadataEditorViewModel(comp.Maintenance, session,
                errors, dispatch, TimeProvider.System,
                // Stage 5.4 5.1 attribution-warning seam (mirrors MattersPage.OnDeleteMatter's
                // MessageBox confirm): the VM composes the one-line message; the view side is a
                // bare Yes/No defaulting to No, so declining keeps the edits buffered and dirty.
                // Invoked synchronously on the UI thread from SaveCommand, never off-thread.
                confirm: message => MessageBox.Show(message, "Session details",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
                    == MessageBoxResult.Yes);
            // Stage 5.3 Task 7: Split speakers relocated into this window (the Sessions-list
            // context menu path was retired) - the editor's own DiariseCommand raises this.
            detailEditor.DiariseRequested += openSplitSpeakers;
            detailEditor.RetranscribeRequested += openRetranscribe;
            // Stage 5.4 4.4: a settled Session Details save refreshes just that grid row in place
            // (mirrors the DiariseRequested wiring). RefreshRowAsync catches its own faults, so
            // fire-and-forget is safe. Covers both the Sessions-page open and the Matters jump -
            // both routes construct the window through this one factory.
            detailEditor.Saved += id => _ = sessionsVm.RefreshRowAsync(id);
            // Task 17 live roster sync (design section 4): Saved only fires after a SUCCESSFUL
            // SaveMetaAsync (MetadataEditorViewModel.SaveAsync), so this can never notify over a
            // failed or declined save. comp.Windows is the same WindowRegistry instance passed to
            // every ReadViewWindow, so a read view open for this session id refreshes its speaker
            // choices live without a reopen.
            detailEditor.Saved += comp.Windows.NotifyRosterChanged;
            var window = new SessionDetailsWindow(detailEditor, sessionId, comp.Windows, windowState,
                comp.Settings);
            sessionDetailsWindows[sessionId] = window;
            sessionDetailsEditors[sessionId] = detailEditor;
            window.Closed += (_, _) =>
            {
                sessionDetailsWindows.Remove(sessionId);
                sessionDetailsEditors.Remove(sessionId);
                detailEditor.Dispose();
                _ = sessionsVm.RefreshRowAsync(sessionId);   // Stage 5.4 4.4: backstop if a save landed late / X was used
            };
            window.Show();
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
            // Find-bar escalation (design 2026-07-18 section 3): pre-fill the Search page with
            // the current find term and RESET the facets to their defaults ("" = All sentinel,
            // Task 5) - "Search all sessions" means exactly that, never this session's facets.
            // _tray is assigned later in OnStartup but strictly before any read view can open.
            readVm.SearchAllSessionsRequested += term => dispatch(() =>
            {
                searchVm.MatterFilterId = "";
                searchVm.AppFilterId = "";
                searchVm.FromDate = null;
                searchVm.ToDate = null;
                searchVm.QueryText = term;
                _tray?.OpenMainWindowAt(typeof(Pages.SearchPage));
            });
            var window = new ReadViewWindow(readVm, sessionId, comp.Windows, windowState,
                comp.Settings, openSplitSpeakers, openSessionDetails);
            readViews[sessionId] = window;
            window.Closed += (_, _) => { readViews.Remove(sessionId); readVm.Dispose(); };
            window.Show();
        };
        sessionsVm.OpenReadViewRequested += openReadView;

        // Search-page click-through (design 2026-07-13 section 2.2): open or re-activate the read
        // view, then target the clicked hit's segment with its matched term so the window scrolls
        // there with the find bar showing the match. Seq < 0 = a speaker-name hit with no spoken
        // line - nothing to scroll to, so just open. Raised from OpenSnippetCommand on the UI
        // thread, so the readViews map read is safe here.
        searchVm.OpenSnippetRequested += (sessionId, seq, term) =>
        {
            openReadView(sessionId);
            if (seq >= 0 && readViews.TryGetValue(sessionId, out var window))
                window.ShowFindAt(seq, term);
        };

        // Audio import (design 2026-07-13 section 4): fresh decoder/importer/VM per request (the
        // openExport run-then-close pattern). The importer snapshots CURRENT settings at open,
        // like SessionViewModel snapshots at Start. The duration-mismatch gate is a modal OKCancel
        // (the confirmSystemMix house idiom) marshalled onto the UI thread - the importer awaits
        // the answer off-thread. Completion upserts the new row in place and opens the read view.
        Func<LocalScribe.Core.Import.DurationMismatchInfo, Task<bool>> confirmMismatch = info =>
        {
            var tcs = new TaskCompletionSource<bool>();
            dispatch(() =>
            {
                static string Fmt(long ms)
                {
                    var span = TimeSpan.FromMilliseconds(ms);
                    return span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");
                }
                tcs.SetResult(MessageBox.Show(
                    $"This file's container claims a duration of {Fmt(info.ClaimedDurationMs)}, but the decoded " +
                    $"audio is {Fmt(info.DecodedDurationMs)}. The container metadata is unreliable; the decoded " +
                    "audio is used either way. Continue and record a marker in the transcript, or cancel the import?",
                    "Imported duration mismatch", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                    == MessageBoxResult.OK);
            });
            return tcs.Task;
        };
        // One-engine-at-a-time, the REVERSE direction (retranscription-versions plan): while an
        // import transcribes, the live engine AND the re-transcription runner must refuse to
        // start. Both consult SessionController.ExternalEngineBusy (Func<string?>; non-null =
        // busy reason), which the retranscription wiring already set for its own runs - so CHAIN
        // over the prior delegate, never clobber it. `importBusy` is set/cleared by the runner
        // wrapper inside openImport below.
        string? importBusy = null;
        var priorEngineBusy = comp.Controller.ExternalEngineBusy;   // MARKED CALL SITE (seam name)
        comp.Controller.ExternalEngineBusy = () => importBusy ?? priorEngineBusy?.Invoke();
        Action openImport = () =>
        {
            var decoder = new LocalScribe.Core.Import.FfmpegAudioDecoder(
                LocalScribe.Core.Import.FfmpegLocator.FindToolsDir());
            var importer = new LocalScribe.Core.Import.AudioImporter(comp.Paths, comp.Settings.Current,
                decoder, new LocalScribe.Core.Transcription.WhisperEngineFactory(),
                () => new LocalScribe.Core.Vad.SileroVadModel(
                    LocalScribe.Core.Transcription.ModelPaths.Require("silero_vad.onnx")),
                new LocalScribe.Core.Transcription.LiveHardwareProbe(),
                () => new LocalScribe.Core.Audio.StopwatchClock(), TimeProvider.System, comp.AppVersion);
            // Register the whole import run on the busy seam (chained above): Start/Re-transcribe
            // read "audio import" as the refusal reason for exactly as long as ImportAsync runs.
            ViewModels.ImportRunner runImport = async (req, progress, confirm, ct) =>
            {
                // B3-5 (whole-branch M-1): re-check the one-engine rule at import START, not just
                // when this dialog opened. A live recording or a re-transcription may have begun in
                // the interval; the reverse direction (Start/Re-transcribe refusing while importBusy)
                // is already covered, so this closes the forward direction. Nothing is created yet -
                // we throw before ImportAsync, so no partial folder. importBusy is still null here, so
                // ExternalEngineBusy reports only a re-transcription; the live engine is State.
                if (comp.Controller.State != LocalScribe.Core.Live.SessionState.Idle)
                    throw new InvalidOperationException(
                        "A live recording is in progress - stop it before importing audio.");
                if (comp.Controller.ExternalEngineBusy?.Invoke() is string engineBusy)
                    throw new InvalidOperationException(
                        $"Another engine is busy ({engineBusy}) - wait for it to finish before importing audio.");
                importBusy = "audio import";
                // Task.Run: ImportAsync is CPU-heavy (decode + the offline whisper pipeline, whose
                // worker loop is NOT self-dispatched) and the dialog VM awaits this on the UI thread -
                // without this the model load + full-file VAD/transcribe would freeze the dialog and
                // starve Cancel on a long jail-call import. Mirrors RetranscribeDialogViewModel's run
                // wrap; every progress/mismatch seam already marshals back via dispatch/TCS.
                try { return await Task.Run(() => importer.ImportAsync(req, progress, confirm, ct), ct); }
                finally { importBusy = null; }
            };
            var importVm = new ViewModels.ImportDialogViewModel(decoder, runImport,
                comp.Maintenance, pickOpenPath, confirmMismatch, errors, dispatch, TimeProvider.System);
            importVm.Completed += id =>
            {
                _ = sessionsVm.UpsertRowAsync(id);            // in-place row, no scroll jump
                _ = searchIndex.ReindexSessionAsync(id, _shutdownCts.Token);   // newly-imported session is searchable
                openReadView(id);                             // completion opens the session
            };
            _ = importVm.LoadMattersAsync();                  // best-effort; picker is optional
            new ImportDialog(importVm) { Owner = MainWindow }.ShowDialog();
        };
        sessionsVm.ImportRequested += openImport;

        // The openSessionDetails factory is declared above (hoisted over openReadView for the
        // read view's reassign-dialog hand-off); its Sessions-page subscription stays here.
        sessionsVm.OpenSessionDetailsRequested += openSessionDetails;

        // Export dialog (Task 9, design 3.4): a fresh VM + plain Window per request (short-lived
        // run-then-close flow, same as openSplitSpeakers - no dedup/reuse map). Title falls back to
        // the raw id if the row has since dropped out of the cached Rows list.
        Action<string> openExport = sessionId =>
        {
            string title = sessionsVm.Rows.FirstOrDefault(r => r.Id == sessionId)?.Title ?? sessionId;
            var exportVm = new ViewModels.ExportDialogViewModel(sessionId, title, comp.Maintenance,
                pickSavePath, revealFile, errors, dispatch);
            new ExportDialog(exportVm) { Owner = MainWindow }.ShowDialog();
        };
        sessionsVm.ExportRequested += openExport;

        // Matters-page tagged-session actions (design 2026-07-18 section 4): the primary "Open"
        // now opens the TRANSCRIPT read view - deliberately reversing the Stage 5.2 decision
        // that kept the read view Sessions-page-only. "Details" keeps the Session Details window
        // as the secondary action; both reuse the same dedup/activate factories above.
        mattersVm.OpenSessionDetailsRequested += openSessionDetails;
        mattersVm.OpenReadViewRequested += openReadView;

        // Stage 5.4 5.4 + design 2026-07-18: a tag/untag from the Matters page makes the Sessions
        // grid's matter chips for that row stale - refresh just that row in place (mirrors the
        // detailEditor.Saved wiring above). RefreshRowAsync catches its own faults.
        mattersVm.SessionUntagged += id => _ = sessionsVm.RefreshRowAsync(id);
        mattersVm.SessionTagged += id => _ = sessionsVm.RefreshRowAsync(id);

        // Tray with the re-creating MainWindow factory (Task 14's 5-arg ctor; MainWindow
        // widened by this task). Pages are humble shells built fresh per window open - a WPF
        // element cannot be re-hosted across windows - around the singleton VMs above.
        _tray = new TrayIconHost(session, lines, console, comp.Paths, comp.Settings,
            mainWindowFactory: () => new MainWindow(mainVm, windowState, comp.Settings,
                new StaticPageProvider(new Dictionary<Type, object>
                {
                    [typeof(Pages.SessionsPage)] = new Pages.SessionsPage(sessionsVm),
                    [typeof(Pages.SearchPage)] = new Pages.SearchPage(searchVm),
                    [typeof(Pages.MattersPage)] = new Pages.MattersPage(mattersVm),
                    [typeof(Pages.SettingsPage)] = new Pages.SettingsPage(settingsVm),
                })));

        // Stage 5.4 Phase 3 (design section 6): ANY Start - nav rail, console, or tray - opens the
        // Record console; the overlay pill already follows State via OverlayViewModel.IsVisible.
        // Idle->Recording only: a Resume (Paused->Recording) must not re-activate/steal focus.
        var lastState = LocalScribe.Core.Live.SessionState.Idle;
        session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ViewModels.SessionViewModel.State)) return;
            if (lastState == LocalScribe.Core.Live.SessionState.Idle
                && session.State == LocalScribe.Core.Live.SessionState.Recording)
                _tray?.OpenLiveView();
            lastState = session.State;
        };

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

        // Task 8: the advisory app-mute poll runs on its own slower cadence (2 s) - the UIA tray
        // walk is comparatively expensive and the signal is coarse. Poll() no-ops until Recording
        // and fails open, so starting it now (recording never begins during startup) is safe.
        _appMuteTimer = new System.Windows.Threading.DispatcherTimer
        { Interval = TimeSpan.FromSeconds(2) };
        _appMuteTimer.Tick += (_, _) => appMuteWatcher.Poll();
        _appMuteTimer.Start();

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
            recoverAll: ct => comp.Maintenance.RecoverAllAsync(ct,
                onRecovered: id => dispatch(() => _ = sessionsVm.UpsertRowAsync(id))),
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

        // Search-index build (design 2026-07-13 section 2.3): OFF the UI thread, after the recovery
        // scan so just-recovered sessions index in their finalized form. A cold cache shows the
        // Search page's "indexing..." state until IsReady flips (ReadyChanged re-runs any pending
        // query). Best-effort by design: per-session failures surface on SessionSkipped (Trace),
        // and a derived cache must never fault startup. No exit-time flush - the debounced write
        // plus the self-healing load cover an exit mid-debounce.
        _ = orchestrator.ScanCompleted.ContinueWith(async _ =>
        {
            try { await searchIndex.InitializeAsync(_shutdownCts.Token); }
            catch (OperationCanceledException) { }    // shutdown mid-build: self-heals next launch
            catch { }
        }, TaskScheduler.Default);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdownCts.Cancel();                   // stop an in-flight startup scan politely
        _timer?.Stop();
        _appMuteTimer?.Stop();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
