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
        var (controller, settings, paths) = CompositionRoot.Build();
        Assert.Equal(SessionState.Idle, controller.State);
        Assert.False(paths.Root.Contains('%'));          // env vars expanded by StoragePaths
        Assert.NotNull(settings);
    }

    /// <summary>Regression for the CompositionRoot startup deadlock (Stage 3b review MUST-FIX
    /// 1): Build() runs inline from App.OnStartup, i.e. on the WPF UI thread under a
    /// DispatcherSynchronizationContext. Core's storage helpers (SettingsStore/SchemaGuard/
    /// JsonFile) await with no ConfigureAwait(false), so a plain
    /// "LoadOrDefaultAsync(...).GetAwaiter().GetResult()" deadlocks once settings.json exists:
    /// the real file read's continuation tries to post back to this same UI thread, which is
    /// already blocked inside GetResult() and never gets back to its message loop to run it.
    ///
    /// Build() hardcodes %APPDATA%/LocalScribe/settings.json, so it can't be pointed at a temp
    /// path without changing Core. Per the task brief's fallback, this test instead runs the
    /// SAME load expression Build() uses, against a real temp settings.json, under a minimal
    /// single-threaded SynchronizationContext stand-in for DispatcherSynchronizationContext -
    /// and asserts it completes within a 5s timeout rather than hanging.
    ///
    /// RED (pre-fix, expression = "new SettingsStore(path).LoadOrDefaultAsync(default)
    /// .GetAwaiter().GetResult()" with no Task.Run wrap): the worker thread never joins inside
    /// 5s - this assertion fails.
    /// GREEN (post-fix, Task.Run(...) wrap, as below): Task.Run's delegate runs on a pool
    /// thread where SynchronizationContext.Current is null, so its await continuations never
    /// try to post back to the stub context - GetResult() only blocks until the pool work
    /// finishes, and the worker joins well under 5s.</summary>
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
