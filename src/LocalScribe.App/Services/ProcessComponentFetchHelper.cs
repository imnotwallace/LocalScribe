using System.Diagnostics;

namespace LocalScribe.App.Services;

/// <summary>Production IComponentFetchHelper (Tier 1 plan D, T1-10, 2026-08-05): spawns
/// LocalScribe.Fetch.exe, writes the job as one JSON line to its stdin, and forwards each stdout
/// line to the caller. A near-copy of ProcessDiarisationHelper, deliberately - including killing
/// the whole child process TREE on cancellation, because a plain Kill() signals only the
/// immediate child.
///
/// Spawned ONLY from an explicit Download click. This class is the entire reason the two
/// shipping assemblies stay clean under the grep in NoNetworkInAppOrCoreTests: the transfer runs
/// in another executable that this one merely starts and stops. A humble object at the process
/// boundary - not unit-tested; ComponentFetchClientTests covers the wire contract over a fake
/// helper instead.</summary>
public sealed class ProcessComponentFetchHelper(string exePath) : IComponentFetchHelper
{
    public async Task<int> RunAsync(string jobJson, Action<string> onStdoutLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start the download helper");
        await using var reg = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* best-effort: the process may have exited between the check and the kill */ }
        });

        await proc.StandardInput.WriteAsync(jobJson);
        proc.StandardInput.Close();

        string? line;
        while ((line = await proc.StandardOutput.ReadLineAsync(ct)) != null)
            onStdoutLine(line);

        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }
}
