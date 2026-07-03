using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class StartupOrchestratorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-startup-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static RecoveryScanResult Result(string[] recovered, params (string Id, string Error)[] failures)
        => new(recovered, failures);

    [Fact]
    public async Task Recovered_sessions_notify_once_and_rebuild_runs_after_the_scan()
    {
        var order = new List<string>();
        var notices = new List<string>();
        var orchestrator = new StartupOrchestrator(
            recoverAll: _ => { order.Add("scan"); return Task.FromResult(Result(new[] { "a", "b" })); },
            rebuildIndex: _ => { order.Add("rebuild"); return Task.CompletedTask; },
            new FakeUiErrorReporter(), notices.Add);

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "scan", "rebuild" }, order);       // design 4.3: rebuild AFTER the scan
        Assert.Equal(new[] { "Recovered 2 interrupted session(s)" }, notices);
        Assert.True(orchestrator.ScanCompleted.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Nothing_recovered_means_no_balloon_but_rebuild_still_runs()
    {
        var notices = new List<string>();
        bool rebuilt = false;
        var orchestrator = new StartupOrchestrator(
            _ => Task.FromResult(Result(Array.Empty<string>())),
            _ => { rebuilt = true; return Task.CompletedTask; },
            new FakeUiErrorReporter(), notices.Add);

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.Empty(notices);
        Assert.True(rebuilt);
    }

    [Fact]
    public async Task Per_session_failures_are_reported_individually_not_swallowed()
    {
        var errors = new FakeUiErrorReporter();
        bool rebuilt = false;
        var orchestrator = new StartupOrchestrator(
            _ => Task.FromResult(Result(new[] { "ok-1" }, ("bad-1", "torn file"), ("bad-2", "locked"))),
            _ => { rebuilt = true; return Task.CompletedTask; },
            errors, _ => { });

        await orchestrator.RunAsync(CancellationToken.None);

        Assert.Equal(2, errors.Reports.Count);
        Assert.Contains(errors.Reports, r => r.Context.Contains("bad-1") && r.Ex.Message.Contains("torn file"));
        Assert.Contains(errors.Reports, r => r.Context.Contains("bad-2") && r.Ex.Message.Contains("locked"));
        Assert.True(rebuilt);                                   // failures never stop the rebuild
        Assert.True(orchestrator.ScanCompleted.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_faulted_scan_is_reported_and_ScanCompleted_still_completes()
    {
        var errors = new FakeUiErrorReporter();
        var orchestrator = new StartupOrchestrator(
            _ => throw new IOException("storage offline"),
            _ => Task.CompletedTask,
            errors, _ => { });

        await orchestrator.RunAsync(CancellationToken.None);    // must not throw

        Assert.Single(errors.Reports);
        Assert.True(orchestrator.ScanCompleted.IsCompleted);    // the sessions page banner always clears
    }

    [Fact]
    public async Task Start_is_never_blocked_by_a_slow_scan()
    {
        // TaskCompletionSource-gated fake: the scan is "in flight" until we say otherwise.
        var gate = new TaskCompletionSource<RecoveryScanResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var orchestrator = new StartupOrchestrator(
            _ => gate.Task, _ => Task.CompletedTask, new FakeUiErrorReporter(), _ => { });
        Task scan = orchestrator.RunAsync(CancellationToken.None);

        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());

        await vm.StartCommand.ExecuteAsync(null);               // recording while the scan hangs
        Assert.Equal(SessionState.Recording, vm.State);
        Assert.False(orchestrator.ScanCompleted.IsCompleted);
        await vm.StopCommand.ExecuteAsync(null);

        gate.SetResult(Result(Array.Empty<string>()));
        await scan;
        Assert.True(orchestrator.ScanCompleted.IsCompletedSuccessfully);
    }

    [Fact]
    public void TrayNoticeReporter_formats_context_and_message_into_the_notify_sink()
    {
        var notices = new List<string>();
        var reporter = new TrayNoticeReporter(notices.Add);
        reporter.Report("Recovery of session x", new InvalidOperationException("torn"));
        reporter.Info("hello");
        Assert.Equal(new[] { "Recovery of session x: torn", "hello" }, notices);
    }
}
