using LocalScribe.Core.Import;
using LocalScribe.Core.Transcription;

namespace LocalScribe.Core.Tests;

/// <summary>The two locator defects the 2026-08-06 packaging design note names, fixed BEFORE
/// packaging depends on them (Tier 1D, decision 3).
///
/// Both were dev-only conveniences that would cheerfully mask a broken INSTALL on a developer's
/// machine. ffmpeg had been resolving through the repo walk-up for months, so nothing had ever
/// exercised the shipping path - which is exactly how the Tier 1C smoke run found Import greyed
/// out in a worktree.
///
/// (a) ModelPaths returned its walk-up result UNCONDITIONALLY, so the first .slnx above the
///     binary won even when its models\ was absent, making the beside-the-binary fallback
///     unreachable whenever any .slnx was an ancestor.
/// (b) The two locators disagreed about probe order. Settled on env -> beside-the-binary -> repo
///     walk-up for both, so the SHIPPING path is the one exercised first everywhere and the dev
///     convenience is the fallback rather than the default.</summary>
public sealed class ComponentLocatorOrderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-locator-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Dir(params string[] parts)
    {
        string p = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    private void Slnx(string dir) => File.WriteAllText(Path.Combine(dir, "LocalScribe.slnx"), "<Solution/>");
    private static void Tools(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ffmpeg.exe"), "x");
        File.WriteAllText(Path.Combine(dir, "ffprobe.exe"), "x");
    }

    [Fact]
    public void ModelPaths_falls_THROUGH_a_repo_whose_models_folder_does_not_exist()
    {
        // THE DEFECT. A published app under a checkout - or a git worktree, which is how this was
        // found - has a .slnx above it whose models\ is absent. The old code returned that
        // non-existent path anyway and the app reported "Model 'small.en' is not downloaded"
        // instead of looking beside the binary, where the installer actually puts them.
        string repo = Dir("repo");
        Slnx(repo);
        string bin = Dir("repo", "publish");
        Directory.CreateDirectory(Path.Combine(bin, "models"));   // the SHIPPED location

        Assert.Equal(Path.Combine(bin, "models"), ModelPaths.ResolveRoot(bin, env: null));
    }

    [Fact]
    public void ModelPaths_still_uses_the_repo_models_folder_when_it_really_is_there()
    {
        // The dev convenience must survive: a bin\Debug build with no models\ beside it still
        // finds the repo's 12 GB library rather than demanding a second copy.
        string repo = Dir("repo2");
        Slnx(repo);
        string models = Dir("repo2", "models");
        string bin = Dir("repo2", "src", "App", "bin", "Debug");

        Assert.Equal(models, ModelPaths.ResolveRoot(bin, env: null));
    }

    [Fact]
    public void ModelPaths_probes_beside_the_binary_BEFORE_the_repo_walk_up()
    {
        // Order defect (b): the installed layout is the one that must be exercised first
        // everywhere, so a stale repo checkout above an install can never shadow it.
        string repo = Dir("repo3");
        Slnx(repo);
        Dir("repo3", "models");                                    // repo copy exists
        string bin = Dir("repo3", "publish");
        Directory.CreateDirectory(Path.Combine(bin, "models"));    // and so does the shipped one

        Assert.Equal(Path.Combine(bin, "models"), ModelPaths.ResolveRoot(bin, env: null));
    }

    [Fact]
    public void ModelPaths_env_override_still_wins_over_everything()
    {
        // What makes a worktree, a test fixture and a portable install work - never remove it.
        string repo = Dir("repo4");
        Slnx(repo);
        Dir("repo4", "models");
        string elsewhere = Dir("elsewhere");

        Assert.Equal(Path.GetFullPath(elsewhere), ModelPaths.ResolveRoot(Dir("repo4", "publish"), elsewhere));
    }

    [Fact]
    public void ModelPaths_with_nothing_anywhere_still_returns_the_beside_the_binary_path()
    {
        // Non-null by contract: Require() composes its "run tools/fetch-models.ps1" message from
        // this path, so it must name the place the user should put the files.
        string bin = Dir("bare");
        Assert.Equal(Path.Combine(bin, "models"), ModelPaths.ResolveRoot(bin, env: null));
    }

    [Fact]
    public void Ffmpeg_probes_in_the_same_settled_order_and_validates_every_hit()
    {
        string repo = Dir("f1");
        Slnx(repo);
        Tools(Path.Combine(repo, "tools", "ffmpeg"));
        string bin = Dir("f1", "publish");
        Tools(Path.Combine(bin, "ffmpeg"));

        // beside-the-binary wins over the repo walk-up, matching ModelPaths exactly.
        Assert.Equal(Path.Combine(bin, "ffmpeg"), FfmpegLocator.FindToolsDir(bin, env: null));
    }

    [Fact]
    public void Ffmpeg_falls_through_a_repo_whose_tools_folder_is_incomplete()
    {
        string repo = Dir("f2");
        Slnx(repo);
        Directory.CreateDirectory(Path.Combine(repo, "tools", "ffmpeg"));   // exists but EMPTY
        Assert.Null(FfmpegLocator.FindToolsDir(Dir("f2", "publish"), env: null));
    }

    [Fact]
    public void Ffmpeg_ignores_an_env_override_that_does_not_actually_contain_the_tools()
    {
        // A stale LOCALSCRIBE_FFMPEG must not disable Import outright when a real copy is
        // present beside the binary - it degrades to the next probe rather than to null.
        string bin = Dir("f3");
        Tools(Path.Combine(bin, "ffmpeg"));
        Assert.Equal(Path.Combine(bin, "ffmpeg"), FfmpegLocator.FindToolsDir(bin, Dir("empty-env")));
    }
}
