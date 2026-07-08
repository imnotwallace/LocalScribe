namespace LocalScribe.Core.Transcription;

/// <summary>Single resolver for local ML model files (dev/fixture use; Stage 7 owns
/// download + SHA pinning). Env var LOCALSCRIBE_MODELS overrides; else "models/" at the
/// repo root (found by walking up to LocalScribe.slnx); else "models/" beside the binary.</summary>
public static class ModelPaths
{
    public static string ModelsRoot
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("LOCALSCRIBE_MODELS");
            if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var d = dir; d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx")))
                    return Path.Combine(d.FullName, "models");
            return Path.Combine(AppContext.BaseDirectory, "models");
        }
    }

    public static string Resolve(string fileName) => Path.Combine(ModelsRoot, fileName);

    /// <summary>Fixture-test guard: returns the path or throws with the fetch instruction.</summary>
    public static string Require(string fileName)
    {
        string path = Resolve(fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"Model file missing: {path}. Run tools/fetch-models.ps1 first " +
                "(or set LOCALSCRIBE_MODELS).", path);
        return path;
    }

    /// <summary>The set of Whisper model names present on disk: each "ggml-{name}.bin" in ModelsRoot
    /// mapped to "{name}" (e.g. "base.en"). Empty if the models dir is missing/unreadable. Used by
    /// BackendSelector so "auto" only resolves to a model that can actually load (design section 1).</summary>
    public static IReadOnlySet<string> AvailableModels()
    {
        try
        {
            if (!Directory.Exists(ModelsRoot)) return new HashSet<string>();
            return Directory.EnumerateFiles(ModelsRoot, "ggml-*.bin")
                .Select(f => Path.GetFileNameWithoutExtension(f)["ggml-".Length..])
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (IOException) { return new HashSet<string>(); }
    }
}
