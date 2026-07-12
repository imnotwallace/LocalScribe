using System.Collections.Concurrent;
using System.IO;
using LocalScribe.App;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
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
        Assert.NotNull(comp.RemoteOverride);             // Stage 5.4 Phase 3: per-session seam
        Assert.Null(comp.RemoteOverride.Override);       // no override at startup
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
