// src/LocalScribe.App/Services/ProcessDiarisationHelper.cs
using System.Diagnostics;
using System.Text.Json;
using LocalScribe.Core.Diarisation;

namespace LocalScribe.App.Services;

/// <summary>Production IDiarisationHelper (Stage 5 design section 4): spawns
/// LocalScribe.Diarizer.exe out-of-process, writes the job as one JSON line to its stdin, and
/// forwards each stdout line to the caller (SherpaHelperDiariser classifies progress/result/error
/// lines). On cancellation the whole child process TREE is killed - the helper may itself spawn
/// native worker threads/processes under sherpa-onnx, and a plain Process.Kill() only signals the
/// immediate child. This is a humble object at the native/process boundary (like
/// MediaPlayerDualAudioPlayer) - not unit-tested; SherpaHelperDiariserTests cover the JSON
/// contract against a fake IDiarisationHelper instead.</summary>
public sealed class ProcessDiarisationHelper(string exePath) : IDiarisationHelper
{
    public async Task<int> RunAsync(DiarisationJob job, Action<string> onStdoutLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to start diarizer");
        await using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* best-effort: the process may have exited between the check and the kill */ }
        });

        await proc.StandardInput.WriteAsync(JsonSerializer.Serialize(job, DiarisationJson.Options));
        proc.StandardInput.Close();

        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
            onStdoutLine(line);

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }
}
