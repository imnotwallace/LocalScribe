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

    /// <summary>The two roles one models folder used to serve (2026-08-11). <paramref name="Bundled"/>
    /// is what the installer shipped beside the binary - versioned with the app, and correctly so,
    /// since an update should be able to replace it. <paramref name="Download"/> is where in-app
    /// fetches land: USER-ACQUIRED data, which belongs outside any versioned folder for the same
    /// reason sessions and settings already do.
    ///
    /// They were one root, so downloads went to `current\models\` on an installed machine - inside
    /// the directory an update replaces. Measured on a real 0.9.0 install: 1.9 GB, including a
    /// fetched large-v3-turbo the installer never bundled.</summary>
    public readonly record struct ModelRoots(string Download, string Bundled);

    /// <summary>Pure root resolution. Reads search Download then Bundled; writes go to Download.</summary>
    public static ModelRoots ResolveRoots(string baseDirectory, string? env, string sharedRoot)
    {
        // An explicit override means "the models are HERE". Splitting it would resolve some files
        // somewhere the user never named - the opposite of what an override is for.
        if (!string.IsNullOrEmpty(env))
        {
            string e = Path.GetFullPath(env);
            return new ModelRoots(e, e);
        }

        string bundled = ResolveRoot(baseDirectory, env);

        // A source checkout keeps writing into the repo's models\, so the dev loop and the
        // fixture-gated tests are untouched. Detected by the same walk-up ResolveRoot uses: if it
        // landed on the repo folder, this is a checkout, not an install.
        for (var d = new DirectoryInfo(baseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx")))
            {
                string repoModels = Path.Combine(d.FullName, "models");
                if (string.Equals(bundled, repoModels, StringComparison.OrdinalIgnoreCase))
                    return new ModelRoots(repoModels, repoModels);
                break;
            }

        return new ModelRoots(sharedRoot, bundled);
    }

    /// <summary>First root that actually holds the file; otherwise the DOWNLOAD root, so the
    /// "not downloaded" message and the fetch destination always name the same place - telling a
    /// user to put a file somewhere the app will not look is its own defect.</summary>
    public static string ResolveIn(ModelRoots roots, string fileName)
    {
        string download = Path.Combine(roots.Download, fileName);
        if (File.Exists(download)) return download;
        string bundled = Path.Combine(roots.Bundled, fileName);
        return File.Exists(bundled) ? bundled : download;
    }

    /// <summary>Union across both roots - otherwise "auto" and the Start presence gate would
    /// ignore everything the user downloaded.</summary>
    public static IReadOnlySet<string> AvailableModelsIn(ModelRoots roots)
    {
        var all = new HashSet<string>(AvailableModels(roots.Download), StringComparer.Ordinal);
        all.UnionWith(AvailableModels(roots.Bundled));
        return all;
    }

    /// <summary>The version-independent per-user download root: a SIBLING of the versioned app
    /// folder, never inside it.</summary>
    public static string SharedRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LocalScribe", "models");

    public static ModelRoots Roots => ResolveRoots(
        AppContext.BaseDirectory, Environment.GetEnvironmentVariable("LOCALSCRIBE_MODELS"), SharedRoot);

    /// <summary>Where an in-app download must be written.</summary>
    public static string DownloadRoot => Roots.Download;

    public static string Resolve(string fileName) => ResolveIn(Roots, fileName);

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
    public static IReadOnlySet<string> AvailableModels() => AvailableModelsIn(Roots);

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
