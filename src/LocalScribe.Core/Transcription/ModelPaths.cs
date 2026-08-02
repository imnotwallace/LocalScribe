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
    /// mapped to "{name}" (e.g. "base.en"). Quantized files (ggml-{name}-q8_0.bin) normalize to the
    /// canonical name - quantization is a file detail ModelFileResolver picks per backend, so a
    /// quantized-only disk still makes the model selectable. Empty if the models dir is
    /// missing/unreadable. Used by BackendSelector so "auto" only resolves to a model that can
    /// actually load (design section 1).</summary>
    public static IReadOnlySet<string> AvailableModels() => AvailableModels(ModelsRoot);

    /// <summary>Same enumeration against an explicit root - the delegation seam for
    /// SettingsPageViewModel.BuildModelChoices and its hermetic tests. A distinct overload
    /// (not an optional parameter) so the existing Func&lt;IReadOnlySet&lt;string&gt;&gt;
    /// method-group injections (App.xaml.cs) keep compiling.</summary>
    public static IReadOnlySet<string> AvailableModels(string modelsRoot)
    {
        try
        {
            if (!Directory.Exists(modelsRoot)) return new HashSet<string>();
            return Directory.EnumerateFiles(modelsRoot, "ggml-*.bin")
                .Select(f => Path.GetFileNameWithoutExtension(f)["ggml-".Length..])
                .Select(ModelFileResolver.CanonicalName)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new HashSet<string>();   // missing/unreadable models dir -> no models (never throw)
        }
    }
}
