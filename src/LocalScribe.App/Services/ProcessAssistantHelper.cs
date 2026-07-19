// src/LocalScribe.App/Services/ProcessAssistantHelper.cs
using System.Diagnostics;
using System.IO;
using System.Text;
using LocalScribe.Core.Assistant;

namespace LocalScribe.App.Services;

/// <summary>Production IAssistantProcessFactory (design 2026-07-18 section 7.1): spawns
/// LocalScribe.Assistant.exe out-of-process and exposes its stdin/stdout as line streams.
/// Mirrors ProcessDiarisationHelper mechanics exactly - redirected pipes, no shell, no
/// window, and Kill(entireProcessTree: true) because the helper's native llama.cpp runtime
/// may own worker threads/child processes a plain Kill() would orphan. Unlike the Diarizer
/// one-shot, stdin STAYS OPEN so keepAlive chat can send further requests (recorded
/// deviation 1). Humble object at the process boundary - the protocol behavior is pinned by
/// ProcessAssistantHelperTests against a scripted stub, and AssistantJobRunnerTests against
/// in-process fakes. The optional arguments seam exists for those stub tests; production
/// passes only the exe path (CompositionRoot).</summary>
public sealed class ProcessAssistantHelper(string exePath, string? arguments = null) : IAssistantProcessFactory
{
    public Task<IAssistantProcess> StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // UTF-8 (no BOM) on BOTH pipes so non-ASCII in transcripts / model output survives the
            // wire unmangled (Task-7 review M2); LocalScribe.Assistant.exe pins the matching
            // Console.Input/OutputEncoding at startup, so both ends agree. No BOM: a leading BOM on
            // the first stdin write would corrupt the helper's first request-line parse.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        if (arguments is not null) psi.Arguments = arguments;
        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start the assistant helper");
        return Task.FromResult<IAssistantProcess>(new Wrapper(proc));
    }

    private sealed class Wrapper(Process proc) : IAssistantProcess
    {
        public async Task WriteRequestLineAsync(string requestJson, CancellationToken ct)
        {
            await proc.StandardInput.WriteLineAsync(requestJson.AsMemory(), ct);
            await proc.StandardInput.FlushAsync(ct);
        }

        public async Task<string?> ReadEventLineAsync(CancellationToken ct)
            => await proc.StandardOutput.ReadLineAsync(ct);

        public void Kill()
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* best-effort: it may have exited between the check and the kill */ }
        }

        public ValueTask DisposeAsync()
        {
            Kill();
            proc.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
