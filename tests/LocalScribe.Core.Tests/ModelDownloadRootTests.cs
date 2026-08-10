using LocalScribe.Core.Transcription;

namespace LocalScribe.Core.Tests;

/// <summary>Where DOWNLOADED models live (2026-08-11).
///
/// THE DEFECT: in-app downloads went to ModelPaths.Resolve -> "models\" beside the binary, which on
/// an installed machine is %LOCALAPPDATA%\LocalScribe\current\models - inside the versioned
/// application directory the installer manages. Measured on a real install: 1.9 GB sitting there,
/// including a fetched large-v3-turbo that the installer never bundled. Anything an update replaces
/// takes multi-gigabyte downloads with it, and the user re-fetches with no explanation.
///
/// The fix splits ONE root into two roles. Bundled weights ship beside the binary and are versioned
/// with the app - correctly so, since an update should be able to change them. Downloaded weights
/// are USER-ACQUIRED data and belong outside any versioned folder, exactly like sessions and
/// settings already are. Reads search both; writes go to the download root only.
///
/// A source checkout keeps its existing behaviour untouched: the repo's models\ is both roots, so
/// nothing about the dev loop or the fixture tests changes.</summary>
public sealed class ModelDownloadRootTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-modelroots-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Dir(params string[] parts)
    {
        string p = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    private static void Slnx(string dir) =>
        File.WriteAllText(Path.Combine(dir, "LocalScribe.slnx"), "<Solution/>");

    private static string File_(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, name);
        File.WriteAllText(p, "x");
        return p;
    }

    [Fact]
    public void An_installed_app_downloads_OUTSIDE_the_versioned_folder()
    {
        // app\ stands in for current\; shared\ for the sibling that survives an update.
        string app = Dir("app");
        Dir("app", "models");                       // the bundled set the installer shipped
        string shared = Path.Combine(_root, "shared");

        var roots = ModelPaths.ResolveRoots(app, env: null, sharedRoot: shared);

        Assert.Equal(shared, roots.Download);
        Assert.Equal(Path.Combine(app, "models"), roots.Bundled);
    }

    [Fact]
    public void A_source_checkout_still_writes_into_the_repo_models_folder()
    {
        // Unchanged dev behaviour: no AppData involvement, so the existing loop and the
        // fixture-gated tests keep resolving exactly as before.
        string repo = Dir("repo");
        Slnx(repo);
        string repoModels = Dir("repo", "models");
        string app = Dir("repo", "src", "bin");
        string shared = Path.Combine(_root, "shared");

        var roots = ModelPaths.ResolveRoots(app, env: null, sharedRoot: shared);

        Assert.Equal(repoModels, roots.Download);
        Assert.Equal(repoModels, roots.Bundled);
    }

    [Fact]
    public void An_explicit_env_override_stays_a_single_root_for_both_roles()
    {
        // LOCALSCRIBE_MODELS means "the models are HERE" - splitting it would resolve some files
        // somewhere the user did not name, which is the opposite of what an override is for.
        string app = Dir("app");
        string over = Dir("override");
        string shared = Path.Combine(_root, "shared");

        var roots = ModelPaths.ResolveRoots(app, over, shared);

        Assert.Equal(over, roots.Download);
        Assert.Equal(over, roots.Bundled);
    }

    [Fact]
    public void A_downloaded_model_is_found_even_though_it_is_not_beside_the_binary()
    {
        string app = Dir("app");
        Dir("app", "models");
        string shared = Dir("shared");
        string fetched = File_(shared, "ggml-large-v3.bin");

        var roots = ModelPaths.ResolveRoots(app, env: null, sharedRoot: shared);

        Assert.Equal(fetched, ModelPaths.ResolveIn(roots, "ggml-large-v3.bin"));
    }

    [Fact]
    public void A_bundled_model_is_still_found_beside_the_binary()
    {
        string app = Dir("app");
        string bundled = File_(Path.Combine(app, "models"), "ggml-base.en.bin");
        string shared = Dir("shared");

        var roots = ModelPaths.ResolveRoots(app, env: null, sharedRoot: shared);

        Assert.Equal(bundled, ModelPaths.ResolveIn(roots, "ggml-base.en.bin"));
    }

    [Fact]
    public void A_missing_model_names_the_DOWNLOAD_root_as_where_it_ought_to_go()
    {
        // The "not downloaded" message and the fetch destination must agree, or the user is told
        // to put the file somewhere the app will not look.
        string app = Dir("app");
        Dir("app", "models");
        string shared = Path.Combine(_root, "shared");

        var roots = ModelPaths.ResolveRoots(app, env: null, sharedRoot: shared);

        Assert.Equal(Path.Combine(shared, "ggml-medium.en.bin"),
            ModelPaths.ResolveIn(roots, "ggml-medium.en.bin"));
    }

    /// <summary>MEASURED 2026-08-11 by running the real installer over a live install: the whole of
    /// %LOCALAPPDATA%\LocalScribe is the install root and is wiped, not just the versioned current\
    /// folder inside it. A marker at %LOCALAPPDATA%\LocalScribe\models was DESTROYED;
    /// %LOCALAPPDATA%\LocalScribeModels survived.
    ///
    /// The first version of this fix put the download root at the destroyed path, reasoning that a
    /// sibling of current\ was outside the versioned folder and therefore safe. It was not, and no
    /// amount of reading the code would have shown it - only running the upgrade did. This test
    /// pins the property that actually matters, so nobody "tidies" the download root back under the
    /// application directory because it looks neater there.</summary>
    [Fact]
    public void The_shared_root_is_not_inside_the_installer_managed_tree()
    {
        string installRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalScribe");

        string shared = ModelPaths.SharedRoot;

        Assert.False(
            shared.StartsWith(installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "the download root must not sit under the installer's tree - everything under "
            + installRoot + " is removed by an install, including downloaded weights");
    }

    [Fact]
    public void Available_models_is_the_union_of_both_roots()
    {
        // Otherwise "auto" and the Start presence gate would ignore everything the user downloaded.
        string app = Dir("app");
        File_(Path.Combine(app, "models"), "ggml-base.en.bin");
        string shared = Dir("shared");
        File_(shared, "ggml-large-v3.bin");

        var roots = ModelPaths.ResolveRoots(app, env: null, sharedRoot: shared);
        var available = ModelPaths.AvailableModelsIn(roots);

        Assert.Contains("base.en", available);
        Assert.Contains("large-v3", available);
    }
}
