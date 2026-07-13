namespace LocalScribe.Core.Import;

/// <summary>Resolves the ffmpeg/ffprobe tools folder the way ModelPaths resolves models: the
/// LOCALSCRIBE_FFMPEG env var, else "ffmpeg\" beside the binary (Stage 7 bundles it there, the
/// Diarizer.exe precedent), else "tools\ffmpeg\" at the repo root (dev: tools/fetch-ffmpeg.ps1's
/// output, found by walking up to LocalScribe.slnx). Null when neither exe is present - the App
/// then disables Import with MissingMessage instead of crashing (design section 4.2).</summary>
public static class FfmpegLocator
{
    public const string MissingMessage =
        "Run tools/fetch-ffmpeg.ps1 (or set LOCALSCRIBE_FFMPEG to a folder containing ffmpeg.exe and ffprobe.exe).";

    public static string? FindToolsDir()
    {
        string? env = Environment.GetEnvironmentVariable("LOCALSCRIBE_FFMPEG");
        if (!string.IsNullOrEmpty(env) && HasTools(env)) return Path.GetFullPath(env);

        string beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        if (HasTools(beside)) return beside;

        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx")))
            {
                string repoTools = Path.Combine(d.FullName, "tools", "ffmpeg");
                return HasTools(repoTools) ? repoTools : null;
            }
        return null;
    }

    private static bool HasTools(string dir)
        => File.Exists(Path.Combine(dir, "ffmpeg.exe")) && File.Exists(Path.Combine(dir, "ffprobe.exe"));
}
