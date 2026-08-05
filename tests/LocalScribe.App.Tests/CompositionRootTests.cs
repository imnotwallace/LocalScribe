using System.Collections.Concurrent;
using System.IO;
using LocalScribe.App;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Diagnostics;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class CompositionRootTests
{
    [Fact]
    public void Build_produces_an_idle_controller_and_expanded_paths()
    {
        var comp = CompositionRoot.Build();
        Assert.Equal(SessionState.Idle, comp.Controller.State);
        Assert.False(comp.Paths.Root.Contains('%'));     // env vars expanded by StoragePaths
        Assert.NotNull(comp.Settings.Current);
        Assert.NotNull(comp.Maintenance);
        Assert.NotNull(comp.Windows);
        Assert.False(string.IsNullOrEmpty(comp.AppVersion));
        // Tier 1 plan A (2026-08-05): TWO version strings, deliberately. AppVersion is the numeric
        // one that lands in every session.json; BuildInfo carries the git SHA and never does.
        Assert.Equal("0.9.0", comp.AppVersion);
        Assert.False(string.IsNullOrEmpty(comp.BuildInfo));
        Assert.StartsWith(comp.AppVersion, comp.BuildInfo);
        Assert.NotNull(comp.Log);
        Assert.Null(comp.Log.LastError);                 // nothing has failed during Build()
        Assert.NotNull(comp.RemoteOverride);             // Stage 5.4 Phase 3: per-session seam
        Assert.Null(comp.RemoteOverride.Override);       // no override at startup
    }

    /// <summary>Fix round 1 on Task 8 (2026-08-05): CompositionRoot.cs's ExternalEngineBusy
    /// interpolates RetranscriptionRunner.RunningSessionId - a SessionId
    /// (yyyy-MM-dd_HHmm_{App}_{Slug(title)}, i.e. the matter/client name) - into a
    /// SessionController.Notice string. Task 8 wired SessionController.Notice to a durable log
    /// (SessionDiagnosticsRecorder) for the first time, so an unmarked id there would have been
    /// the THIRD instance of a leak this plan has already been bitten by twice. Mirrors
    /// TrayNoticeReporterTests.An_id_bearing_Report_context_is_redacted_at_the_default_setting -
    /// same "mark at the source, strip at the display boundary" pattern, same real-disk proof.
    ///
    /// CORRECTION (fix round 2, 2026-08-05, Important finding): the line below is a HAND-WRITTEN
    /// expression that MIRRORS CompositionRoot.cs's shape, not a load of CompositionRoot.cs itself
    /// - an earlier version of this comment claimed "the exact expression", which was false and
    /// meant this test kept passing even if DiagnosticRedaction.Mark(rid) were deleted from
    /// CompositionRoot.cs:143. This test proves the STRIP half of the fix (a marked Notice string
    /// is displayed plain and logged redacted) against a real SessionController/SessionViewModel/
    /// SessionDiagnosticsRecorder/DiagnosticLog; it does NOT prove CompositionRoot.cs itself
    /// performs the mark. That half is pinned separately by
    /// DiagnosticsWiringTests.ExternalEngineBusy_marks_the_session_id_before_it_reaches_SessionController_Notice,
    /// which reads CompositionRoot.cs's actual source text - LiveTestDoubles.MakeController builds
    /// a controller with no hardware/model dependencies, standing up a real "busy"
    /// RetranscriptionRunner here would need a genuine in-flight re-transcription, which is not a
    /// price worth paying twice for the same guarantee.</summary>
    [Fact]
    public async Task ExternalEngineBusy_notice_stays_plain_on_screen_and_is_redacted_on_disk()
    {
        string root = Path.Combine(Path.GetTempPath(), "ls-comproot-notice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (controller, _, _, _) = LiveTestDoubles.MakeController(root);
            string sessionId = "2026-08-05_1430_Webex_smith-v-jones-settlement-call";
            // MIRRORS CompositionRoot.cs's ExternalEngineBusy shape (post-fix form) - see the class
            // doc above. This is a hand-written copy, NOT a load of the real file; the real file's
            // Mark(rid) call is pinned by DiagnosticsWiringTests instead.
            controller.ExternalEngineBusy = () =>
                $"Cannot start recording - a re-transcription ({DiagnosticRedaction.Mark(sessionId)}) is still running.";

            var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
                startOptions: LiveTestDoubles.Options());
            string? shown = null;
            vm.NoticeRaised += n => shown = n;

            // Real DiagnosticLog to real disk, default settings (IncludeTranscriptText = false) -
            // exactly App.xaml.cs's comp.Controller.Notice += sessionDiag.Notice wiring.
            var log = new DiagnosticLog(new StoragePaths(root),
                new ManualUtcTimeProvider(new DateTimeOffset(2026, 8, 5, 9, 30, 0, TimeSpan.Zero)),
                () => new LoggingSetting());
            var sessionDiag = new SessionDiagnosticsRecorder(log,
                () => controller.CurrentSessionId ?? controller.FinalizingSessionId);
            controller.Notice += sessionDiag.Notice;

            await vm.StartCommand.ExecuteAsync(null);   // refused: hits ExternalEngineBusy, never starts
            await log.FlushAsync(default);

            Assert.Equal(SessionState.Idle, vm.State);  // refused - no session was ever created
            // The on-screen text is BYTE-IDENTICAL to the unredacted string - the id stays fully
            // readable to the user recording their own session.
            string expectedPlain =
                $"Cannot start recording - a re-transcription ({sessionId}) is still running.";
            Assert.Equal(expectedPlain, shown);
            Assert.Equal(expectedPlain, vm.LastNotice);
            Assert.DoesNotContain("<<", shown);
            Assert.DoesNotContain(">>", shown);

            string[] diskLines = await File.ReadAllLinesAsync(
                Path.Combine(new StoragePaths(root).DiagnosticsDir, "diag-202608.jsonl"));
            // One event, one log line: SessionDiagnosticsRecorder is the ONLY subscriber that
            // writes to the log (SessionViewModel's subscriber only touches UI state), so a
            // single Notice must never produce more than one entry.
            string diskText = Assert.Single(diskLines);
            Assert.DoesNotContain("smith-v-jones-settlement-call", diskText);   // slug never on disk
            Assert.Contains("[redacted]", diskText);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    /// <summary>DETERMINISTIC pattern-level regression for the CompositionRoot startup deadlock
    /// (Stage 3b review MUST-FIX 1). This pins the exact hazard CompositionRoot.Build's settings
    /// load guards against, WITHOUT relying on nondeterministic File I/O.
    ///
    /// The hazard: Build() runs inline from App.OnStartup on the WPF UI thread under a
    /// DispatcherSynchronizationContext. Core's storage helpers await with no
    /// ConfigureAwait(false), so a plain "someAsyncOp().GetAwaiter().GetResult()" on that thread
    /// deadlocks whenever the awaited op posts its continuation back to the (now blocked) UI
    /// thread's SynchronizationContext.
    ///
    /// The end-to-end test below drives this through a real settings.json read, but a just-
    /// written cached file read on Windows can complete SYNCHRONOUSLY
    /// (FILE_SKIP_COMPLETION_PORT_ON_SUCCESS), in which case even the unfixed sync-over-async
    /// would not deadlock - so that test can false-green. This test removes that nondeterminism:
    /// the awaited op is "await Task.Yield()", which ALWAYS posts its continuation to the current
    /// SynchronizationContext. Under the single-threaded stub (never pumped while the owning
    /// thread is blocked), the unwrapped form deadlocks EVERY run.
    ///
    /// RED (unwrapped "LoadLikeOp().GetAwaiter().GetResult()"): the Yield continuation is queued
    /// to the stub context, whose only thread is blocked inside GetResult() and never pumps it -
    /// the worker never finishes, so the bounded Join times out. The test asserts the worker did
    /// NOT complete - that is what reliably distinguishes the unfixed sync-over-async from the
    /// fixed Task.Run wrap, EVERY run (no dependence on File I/O completing async).
    /// GREEN (wrapped "Task.Run(() => LoadLikeOp()).GetAwaiter().GetResult()"): Task.Run's
    /// delegate runs on a pool thread where SynchronizationContext.Current is null, so Yield's
    /// continuation runs on the pool (never posts to the stub) - GetResult only blocks until the
    /// pool work finishes and returns 42 well within the timeout.</summary>
    [Fact]
    public void TaskRun_wrap_breaks_the_sync_over_async_UI_deadlock()
    {
        // A local async op that DETERMINISTICALLY goes async by capturing the current context:
        // Task.Yield always posts the continuation to SynchronizationContext.Current (unlike
        // File I/O, which may complete inline). This is the pattern CompositionRoot.Build's
        // settings load embodies (an awaited op whose continuation wants the UI thread back).
        static async Task<int> LoadLikeOp() { await Task.Yield(); return 42; }

        var timeout = TimeSpan.FromSeconds(2);

        // GREEN: the Task.Run wrap (exactly CompositionRoot.Build's fixed form) must complete
        // under the SAME single-threaded UI stub and return the value.
        int greenResult = 0;
        Exception? greenEx = null;
        var greenWorker = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new SingleThreadedUiStub());
                greenResult = Task.Run(() => LoadLikeOp()).GetAwaiter().GetResult();
            }
            catch (Exception ex) { greenEx = ex; }
        })
        { IsBackground = true };
        greenWorker.Start();
        bool greenJoined = greenWorker.Join(timeout);

        Assert.True(greenJoined,
            "Task.Run-wrapped op deadlocked under the single-threaded stub - the fix pattern is broken");
        Assert.Null(greenEx);
        Assert.Equal(42, greenResult);

        // RED: the UNWRAPPED sync-over-async form must deadlock DETERMINISTICALLY under the same
        // stub. The worker below is expected to hang forever (background thread, leaked on
        // purpose); we assert it does NOT complete within the bounded timeout, which proves the
        // hazard is real and that the GREEN path above is what actually avoids it.
        bool redCompleted = false;
        var redWorker = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SingleThreadedUiStub());
            // Unwrapped form - identical to Build() BEFORE the Task.Run fix. Deadlocks here.
            _ = LoadLikeOp().GetAwaiter().GetResult();
            redCompleted = true;   // unreachable while the deadlock stands
        })
        { IsBackground = true };
        redWorker.Start();
        bool redJoined = redWorker.Join(timeout);

        Assert.False(redJoined,
            "unwrapped sync-over-async did NOT deadlock under the single-threaded stub - " +
            "the regression test can no longer distinguish fixed from unfixed (false-green risk)");
        Assert.False(redCompleted);
    }

    /// <summary>End-to-end integration smoke for the same MUST-FIX 1 fix, run against a real
    /// settings.json under the single-threaded stub. BEST-EFFORT ONLY: a just-written cached
    /// file read on Windows can complete synchronously, in which case even the unfixed form
    /// would not deadlock - so this test alone cannot reliably guard the fix (see the
    /// deterministic TaskRun_wrap_breaks_the_sync_over_async_UI_deadlock test above for the
    /// reliable guard). Kept as a realistic exercise of Build()'s actual load expression against
    /// Core's real SettingsStore/SchemaGuard/JsonFile path.
    ///
    /// Build() hardcodes %APPDATA%/LocalScribe/settings.json, so it can't be pointed at a temp
    /// path without changing Core; per the task brief's fallback, this runs the SAME load
    /// expression Build() uses (post-fix, Task.Run-wrapped) against a temp settings.json and
    /// asserts it completes within a 5s timeout rather than hanging.</summary>
    [Fact]
    public async Task Settings_load_expression_does_not_deadlock_under_a_single_threaded_sync_context()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ls-comproot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string settingsPath = Path.Combine(dir, "settings.json");
            // Seed a real settings.json first, on the test runner's own (non-repro) context.
            // This makes LoadOrDefaultAsync take the "file exists" path below, which needs a
            // real (genuinely asynchronous) file read for the deadlock to manifest.
            await new SettingsStore(settingsPath).SaveAsync(new Settings(), CancellationToken.None);

            bool completed = false;
            Exception? threadEx = null;
            var worker = new Thread(() =>
            {
                try
                {
                    // Minimal stand-in for WPF's DispatcherSynchronizationContext: a single
                    // owning thread (this one) whose Post() marshals continuations back to it.
                    SynchronizationContext.SetSynchronizationContext(new SingleThreadedUiStub());

                    // THE EXACT EXPRESSION from CompositionRoot.Build() (post-fix form):
                    var settings = Task.Run(() => new SettingsStore(settingsPath).LoadOrDefaultAsync(default))
                        .GetAwaiter().GetResult();

                    completed = settings is not null;
                }
                catch (Exception ex) { threadEx = ex; }
            })
            { IsBackground = true };

            worker.Start();
            bool joined = worker.Join(TimeSpan.FromSeconds(5));

            Assert.True(joined,
                "settings load deadlocked under a single-threaded SynchronizationContext " +
                "(CompositionRoot startup-deadlock regression)");
            Assert.Null(threadEx);
            Assert.True(completed);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Exactly one owning thread; Post() enqueues and nothing ever drains the queue -
    /// on purpose. That is the real bug's mechanism: the "owning thread" (here, the worker
    /// thread above) is the one blocked inside GetAwaiter().GetResult() and never returns to a
    /// message loop to service its own posted continuations.</summary>
    private sealed class SingleThreadedUiStub : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state)
            => throw new NotSupportedException("not needed for this repro");
    }
}
