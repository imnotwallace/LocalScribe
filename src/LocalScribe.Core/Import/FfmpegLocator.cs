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
        => FindToolsDir(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("LOCALSCRIBE_FFMPEG"));

    /// <summary>The probe, against an explicit base directory and env value (Tier 1D, 2026-08-06).
    /// An overload rather than reading AppContext.BaseDirectory inline, following the
    /// AssistantHelperLocator.FindExe(baseDir, envOverride) precedent and matching
    /// ModelPaths.ResolveRoot, whose probe order was settled against this one by the 2026-08-06
    /// packaging design note: env, then BESIDE THE BINARY, then the repo walk-up.
    ///
    /// This locator already validated every hit, which is the half ModelPaths was missing. The
    /// change here is the walk-up no longer RETURNS on the first .slnx it finds - it breaks and
    /// falls through, so an incomplete repo tools\ffmpeg\ reads the same as no repo at all.</summary>
    public static string? FindToolsDir(string baseDirectory, string? env)
    {
        if (!string.IsNullOrEmpty(env) && HasTools(env)) return Path.GetFullPath(env);

        string beside = Path.Combine(baseDirectory, "ffmpeg");
        if (HasTools(beside)) return beside;

        for (var d = new DirectoryInfo(baseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx")))
            {
                string repoTools = Path.Combine(d.FullName, "tools", "ffmpeg");
                if (HasTools(repoTools)) return repoTools;
                break;
            }
        return null;
    }

    private static bool HasTools(string dir)
        => File.Exists(Path.Combine(dir, "ffmpeg.exe")) && File.Exists(Path.Combine(dir, "ffprobe.exe"));
}
