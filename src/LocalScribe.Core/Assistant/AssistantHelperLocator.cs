namespace LocalScribe.Core.Assistant;

/// <summary>Resolves LocalScribe.Assistant.exe the way FfmpegLocator resolves ffmpeg: the
/// LOCALSCRIBE_ASSISTANT env var (a folder containing the exe), else the "assistant\" FOLDER
/// PUBLISH beside the binary (design 2026-07-23 section 2 - a folder, NOT single-file like
/// Diarizer, because LLamaSharp probes its own runtimes/&lt;rid&gt;/native/&lt;variant&gt;/ layout
/// relative to the app directory), else "tools\assistant\" at the repo root (dev, found by
/// walking up to LocalScribe.slnx). Null when absent - the App then disables the assistant
/// with MissingMessage instead of failing on first use (which is exactly how the single-file
/// helper shipped broken).</summary>
public static class AssistantHelperLocator
{
    public const string ExeName = "LocalScribe.Assistant.exe";

    public const string MissingMessage =
        "The assistant helper is not deployed. Publish it with: dotnet publish src/LocalScribe.Assistant "
        + "-c Release -r win-x64 --self-contained -o <app folder>\\assistant, then verify with "
        + "tools/verify-assistant-publish.ps1 (or set LOCALSCRIBE_ASSISTANT to a folder containing "
        + ExeName + ").";

    public static string? FindExe()
        => FindExe(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("LOCALSCRIBE_ASSISTANT"));

    /// <summary>Testable core; production calls the parameterless overload.</summary>
    public static string? FindExe(string baseDir, string? envOverride)
    {
        if (!string.IsNullOrEmpty(envOverride))
        {
            string fromEnv = Path.Combine(envOverride, ExeName);
            if (File.Exists(fromEnv)) return Path.GetFullPath(fromEnv);
        }

        string beside = Path.Combine(baseDir, "assistant", ExeName);
        if (File.Exists(beside)) return beside;

        for (var d = new DirectoryInfo(baseDir); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx")))
            {
                string dev = Path.Combine(d.FullName, "tools", "assistant", ExeName);
                return File.Exists(dev) ? dev : null;
            }
        return null;
    }
}
