using System.Diagnostics;
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Assistant;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>REAL cross-process protocol tests (design 2026-07-18 section 8): a scripted
/// PowerShell stub speaks the exact JSON-lines contract, so framing, keep-alive, crash
/// surfacing, and cancel-kill are pinned without any model. Windows PowerShell 5 ships on
/// every supported box (SystemDirectory path), so no extra tooling is needed.</summary>
public sealed class ProcessAssistantHelperTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ls-stub-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // The stub: one JSON request per stdin line; scripted JSON-lines out; behavior keyed by
    // marker words inside the request's payload; a per-process counter proves keep-alive
    // requests land on the SAME process. ASCII only (project rule).
    private const string StubScript = """
        $out = [Console]::Out
        $n = 0
        while ($null -ne ($line = [Console]::In.ReadLine())) {
            $n = $n + 1
            if ($line.Contains('CRASH-NOW')) { exit 3 }
            if ($line.Contains('HANG-NOW')) { Start-Sleep -Seconds 300; exit 0 }
            $out.WriteLine('{"type":"progress","phase":"prefill","current":1,"total":2}')
            $out.WriteLine('this line is native noise and must be skipped')
            $out.WriteLine('{"type":"chunk","text":"req-' + $n + '"}')
            $out.WriteLine('{"type":"done","stats":{"backend":"cpu","promptTokens":10,"outputTokens":1}}')
            $out.Flush()
            if (-not $line.Contains('"keepAlive":true')) { exit 0 }
        }
        exit 0
        """;

    private ProcessAssistantHelper MakeStubFactory()
    {
        string stubPath = Path.Combine(_dir, "stub-assistant.ps1");
        File.WriteAllText(stubPath, StubScript);
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return new ProcessAssistantHelper(powershell,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{stubPath}\"");
    }

    private static AssistantRequest Req(string marker, bool keepAlive = false)
        => new("summarize", @"C:\models\q.gguf", 8192, "auto", keepAlive,
            AssistantWire.PromptPayload(marker, 600));

    [Fact]
    public async Task Round_trip_streams_progress_chunk_done_and_skips_noise()
    {
        var runner = new AssistantJobRunner(MakeStubFactory());
        var events = new List<AssistantEvent>();
        await foreach (var e in runner.RunAsync(Req("normal job"), CancellationToken.None))
            events.Add(e);

        Assert.Equal(new AssistantEvent[]
        {
            new AssistantProgress("prefill", 1, 2),
            new AssistantChunk("req-1"),
            new AssistantDone("cpu", 10, 1),
        }, events);
    }

    [Fact]
    public async Task Helper_crash_surfaces_as_a_visible_error_event()
    {
        // Design 7.7: helper crash -> visible error. The stub exits 3 with no terminal line.
        var runner = new AssistantJobRunner(MakeStubFactory());
        var events = new List<AssistantEvent>();
        await foreach (var e in runner.RunAsync(Req("CRASH-NOW"), CancellationToken.None))
            events.Add(e);
        var err = Assert.IsType<AssistantError>(Assert.Single(events));
        Assert.Contains("exited before completing", err.Message);
    }

    [Fact]
    public async Task Cancel_kills_the_stub_promptly()
    {
        var runner = new AssistantJobRunner(MakeStubFactory());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in runner.RunAsync(Req("HANG-NOW"), cts.Token)) { }
        });
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30),
            "cancel must kill the hung helper, not wait out its 300 s sleep");
    }

    [Fact]
    public async Task Keep_alive_chat_reuses_one_stub_process_across_questions()
    {
        // The stub's per-process counter is the proof: warmup sees req-1, answers see req-2/req-3.
        var sessions = new AssistantChatSessionFactory(MakeStubFactory());
        await using var chat = await sessions.StartAsync(Req("warmup", keepAlive: true), CancellationToken.None);

        var a1 = new List<AssistantEvent>();
        await foreach (var e in chat.AskAsync(AssistantWire.PromptPayload("q1", 400), CancellationToken.None)) a1.Add(e);
        var a2 = new List<AssistantEvent>();
        await foreach (var e in chat.AskAsync(AssistantWire.PromptPayload("q2", 400), CancellationToken.None)) a2.Add(e);

        Assert.Contains(new AssistantChunk("req-2"), a1);   // same process as the warmup (req-1)
        Assert.Contains(new AssistantChunk("req-3"), a2);   // and still the same process
    }

    // The four tests above are the brief's exact harness. The two below are additional proofs
    // requested by the Task-6 review gate for this task: DisposeAsync must ACTUALLY kill a
    // still-running keepAlive helper (not just release .NET handles), and the stdin/stdout
    // pipes must never deadlock even under a much heavier line volume than the brief's 4
    // lines/request stub exercises.

    [Fact]
    public async Task DisposeAsync_terminates_a_still_running_keepAlive_process()
    {
        // Cancel_kills_the_stub_promptly (above) proves cancellation kills the process via the
        // runner's ct.Register(proc.Kill) - that path calls Kill() explicitly before disposal.
        // AssistantChatSessionFactory.StartAsync's warmup-failure catch block, by contrast, calls
        // ONLY proc.DisposeAsync() with no preceding Kill() - so DisposeAsync itself must
        // terminate a live process, not merely dispose .NET handles around a leaked OS process.
        // This test drives ProcessAssistantHelper directly (bypassing the runner) so DisposeAsync
        // is exercised with no prior Kill() call, and confirms via the real OS process id - found
        // by diffing "powershell.exe" (Windows PowerShell 5) snapshots, since this box's own
        // tooling runs "pwsh.exe" (PowerShell 7), never "powershell.exe" - that the stub is
        // actually gone afterward. No internals/reflection: only the public IAssistantProcess
        // surface plus System.Diagnostics.Process are used.
        var before = Process.GetProcessesByName("powershell").Select(p => p.Id).ToHashSet();
        IAssistantProcessFactory factory = MakeStubFactory();
        IAssistantProcess proc = await factory.StartAsync(CancellationToken.None);
        int stubPid = -1;
        try
        {
            var after = Process.GetProcessesByName("powershell").Select(p => p.Id).ToHashSet();
            var newIds = after.Except(before).ToList();
            Assert.Single(newIds);
            stubPid = newIds[0];
            Assert.True(IsRunning(stubPid), "the stub should be alive right after spawn");

            // keepAlive:true -> the stub loops back to ReadLine() after "done" instead of
            // exiting, so without a real kill it would still be running after this method returns.
            var req = new AssistantRequest("summarize", @"C:\models\q.gguf", 8192, "auto", true,
                AssistantWire.PromptPayload("stay-alive", 600));
            await proc.WriteRequestLineAsync(AssistantWire.SerializeRequest(req), CancellationToken.None);
            string? line;
            do { line = await proc.ReadEventLineAsync(CancellationToken.None); }
            while (line is not null && AssistantWire.ParseEventLine(line) is not AssistantDone);
            Assert.True(IsRunning(stubPid), "the keepAlive stub must still be resident before dispose");

            await proc.DisposeAsync();   // the thing under test - no Kill() call precedes this

            var deadline = DateTime.UtcNow.AddSeconds(5);
            bool gone = false;
            while (DateTime.UtcNow < deadline)
            {
                if (!IsRunning(stubPid)) { gone = true; break; }
                await Task.Delay(50);
            }
            Assert.True(gone, "DisposeAsync must terminate a still-running keepAlive helper process");
        }
        finally
        {
            if (stubPid >= 0 && IsRunning(stubPid))
            {
                try { Process.GetProcessById(stubPid).Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    private static bool IsRunning(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task Many_line_stream_does_not_deadlock_reading_while_writing()
    {
        // Requirement: reading stdout must never block writing stdin (or vice-versa). This stub
        // floods stdout with far more lines than a default OS pipe buffer holds before emitting
        // "done", proving the async read loop keeps draining without the runner ever needing a
        // synchronous drain (the classic MSDN redirected-IO deadlock is calling
        // Process.WaitForExit() before stdout is emptied when the child out-produces the pipe
        // buffer - neither the runner nor ProcessAssistantHelper does that). Bounded by a
        // cancellation timeout: a hang here is a real bug, not something to paper over.
        const int lineCount = 5000;
        // A plain (non-interpolated) raw string, same style as StubScript above: every '$' here
        // is PowerShell syntax, not C# interpolation, so the literal JSON braces need no escaping.
        const string FloodScriptTemplate = """
            $out = [Console]::Out
            $line = [Console]::In.ReadLine()
            for ($i = 1; $i -le __LINE_COUNT__; $i++) {
                $out.WriteLine('{"type":"chunk","text":"c-' + $i + '"}')
            }
            $out.WriteLine('{"type":"done","stats":{"backend":"cpu","promptTokens":1,"outputTokens":1}}')
            $out.Flush()
            exit 0
            """;
        string stubPath = Path.Combine(_dir, "stub-flood.ps1");
        File.WriteAllText(stubPath, FloodScriptTemplate.Replace("__LINE_COUNT__", lineCount.ToString()));
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var runner = new AssistantJobRunner(new ProcessAssistantHelper(powershell,
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{stubPath}\""));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var events = new List<AssistantEvent>();
        await foreach (var e in runner.RunAsync(Req("flood"), cts.Token)) events.Add(e);

        Assert.Equal(lineCount + 1, events.Count);
        Assert.IsType<AssistantDone>(events[^1]);
    }
}
