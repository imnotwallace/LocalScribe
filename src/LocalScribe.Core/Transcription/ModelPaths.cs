namespace LocalScribe.Core.Transcription;

/// <summary>Single resolver for local ML model files. Probe order, settled with FfmpegLocator
/// by the 2026-08-06 packaging design note (Tier 1D decision 3): the LOCALSCRIBE_MODELS env var,
/// else "models/" BESIDE THE BINARY (the installed layout), else "models/" at the repo root
/// (dev convenience, found by walking up to LocalScribe.slnx), else the beside-the-binary path
/// as the name of the place the files ought to go.</summary>
public static class ModelPaths
{
    public static string ModelsRoot
        => ResolveRoot(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("LOCALSCRIBE_MODELS"));

    /// <summary>The probe, against an explicit base directory and env value (Tier 1D, 2026-08-06).
    /// An overload rather than reading AppContext.BaseDirectory inline, following the
    /// AssistantHelperLocator.FindExe(baseDir, envOverride) precedent, because the ordering below
    /// is exactly what a packaging regression breaks and it was previously untestable.
    ///
    /// TWO defects fixed here, both named by the packaging design note:
    /// (a) the repo walk-up used to return its result UNCONDITIONALLY, without checking the
    ///     directory existed, so the first .slnx above the binary won even when its models\ was
    ///     absent - which made the beside-the-binary fallback unreachable whenever any .slnx was
    ///     an ancestor, and is why a worktree reported "Model 'small.en' is not downloaded"
    ///     rather than falling through.
    /// (b) this probed the walk-up BEFORE beside-the-binary while FfmpegLocator did the reverse.
    ///     On an installed machine there is no .slnx above the exe so both landed in the same
    ///     place, which is precisely why the inconsistency survived - it is a trap for the next
    ///     person, and it meant the SHIPPING path was never the one exercised first.
    ///
    /// Returns non-null always: Require() composes its "run tools/fetch-models.ps1" message from
    /// this path, so it must name where the user should put the files even when nothing is
    /// present.</summary>
    public static string ResolveRoot(string baseDirectory, string? env)
    {
        // The env override is what makes a worktree, a test fixture and a portable install work.
        // Unlike the two probes below it is NOT existence-checked: an explicit override that is
        // wrong should surface as "models are missing HERE", not silently resolve somewhere else.
        if (!string.IsNullOrEmpty(env)) return Path.GetFullPath(env);

        string beside = Path.Combine(baseDirectory, "models");
        if (Directory.Exists(beside)) return beside;

        for (var d = new DirectoryInfo(baseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx")))
            {
                string repoModels = Path.Combine(d.FullName, "models");
                // The existence check that defect (a) was missing. Fall THROUGH when it is
                // absent - matching FfmpegLocator, which has always validated its hit.
                if (Directory.Exists(repoModels)) return repoModels;
                break;
            }

        return beside;
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
