using System.IO;
using System.Reflection;
using LocalScribe.App;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pins the version stamp (Tier 1 plan A, 2026-08-05, spec item T1-1). TWO strings and
/// both matter: the NUMERIC assembly version is what CompositionRoot.cs:67 turns into
/// SessionRecord.AppVersion in every session.json - append-only evidentiary data that read
/// "1.0.0" (the SDK default) on every session ever recorded before this round - while the
/// InformationalVersion carries the git SHA for support. Reading src/Directory.Build.props as TEXT
/// follows XamlHygieneTests.AppIcon_ExistsAndIsWiredInCsproj, which asserts on raw csproj text the
/// same way; there is no other way to pin an MSBuild property from a test.</summary>
public sealed class BuildVersionTests
{
    private static string PropsPath()
        => Path.Combine(RepoPaths.SolutionRoot(), "src", "Directory.Build.props");

    [Fact]
    public void Src_props_sets_the_version_and_suppresses_the_sdk_source_revision()
    {
        Assert.True(File.Exists(PropsPath()), "missing " + PropsPath());
        string props = File.ReadAllText(PropsPath());
        Assert.Contains("<Version>0.9.0</Version>", props);
        // MEASURED 2026-08-05 on SDK 10.0.302: the SDK's built-in source-link already appends
        // "+<40-char sha>" to InformationalVersion with NO custom target, so without this
        // suppression the stamp came out as "0.9.0+g4ddb7d4.4ddb7d47ab606d0..." - two SHAs, one
        // of them full length. The plan's own short-sha stamp is the one we keep.
        Assert.Contains(
            "<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>",
            props);
    }

    [Fact]
    public void The_git_sha_is_shape_checked_before_it_is_stamped()
    {
        // F12 (final whole-branch review, 2026-08-05): Exec's ConsoleToMSBuild captures STDERR as
        // well as stdout and flattens the captured item list into the property joined with ';'. A
        // git that SUCCEEDS while printing a "warning:" line (an unusual worktree, a config or
        // detached-head notice) would otherwise stamp "0.9.0+gwarning...;4ddb7d4" and break the
        // Settings About line and every support paste-in - and IgnoreExitCode plus the ExitCode
        // check catch only a FAILING command, never a succeeding-but-noisy one. MSBuild has no
        // regex in Conditions, so the guard is the Regex property function; a rejected value clears
        // the property and the existing fallback stamps the bare $(Version).
        string props = File.ReadAllText(PropsPath());
        Assert.Contains("[System.Text.RegularExpressions.Regex]::IsMatch('$(_LsGitSha)', '^[0-9a-f]+$')", props);
        // The unguarded form: the sha went straight from the Exec output into the stamp.
        Assert.DoesNotContain("$(Version)+g$(_LsGitShaOutput.Trim())", props);
    }

    [Fact]
    public void The_version_stamp_is_scoped_to_src_and_not_the_repo_root()
    {
        // A repo-root Directory.Build.props would silently apply to all 13 csproj files, including
        // tools/generate-icon and tools/UiaProbe, and would drag the <Version> stamp with it -
        // Assembly.GetName().Version lands in every session.json as evidentiary, append-only data.
        // MSBuild stops at the FIRST match walking up, so keeping the VERSION stamp under src/ is
        // what scopes it to the eight shipping projects.
        Assert.False(File.Exists(Path.Combine(RepoPaths.SolutionRoot(), "Directory.Build.props")));
        Assert.False(File.Exists(Path.Combine(RepoPaths.SolutionRoot(), "Directory.Build.targets")));

        // F8 (2026-08-05) added a SECOND Directory.Build.props under tests/, and this fact would
        // read as a promise that no such file exists anywhere but src/ if it did not say so. That
        // file imports the shared build-output guard and NOTHING else - asserted in detail by
        // BuildOutputGuardTests.Both_src_and_tests_import_the_one_shared_guard_and_neither_redeclares_it.
        // The point of the rule is the version stamp's scope, and that is unchanged.
        string tests = Path.Combine(RepoPaths.SolutionRoot(), "tests", "Directory.Build.props");
        Assert.True(File.Exists(tests), "missing " + tests);
        Assert.DoesNotContain("<Version>", File.ReadAllText(tests));
    }

    [Fact]
    public void The_app_assembly_reports_the_real_numeric_version()
        => Assert.Equal("0.9.0", typeof(CompositionRoot).Assembly.GetName().Version?.ToString(3));

    [Fact]
    public void The_core_assembly_is_stamped_from_the_same_props_file()
        => Assert.Equal("0.9.0",
            typeof(LocalScribe.Core.Storage.StoragePaths).Assembly.GetName().Version?.ToString(3));

    [Fact]
    public void The_informational_version_carries_an_optional_short_git_sha()
    {
        string? info = typeof(CompositionRoot).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(info));
        // TWO legal shapes and the test must accept both: a git checkout stamps "0.9.0+g1628935";
        // a source drop with no .git falls back to a bare "0.9.0" (MEASURED both ways 2026-08-05).
        //
        // F11 (final whole-branch review, 2026-08-05): {7,}, not {7}. `git rev-parse --short=7` is
        // a MINIMUM, not a fixed width - git LENGTHENS an abbreviated sha whenever 7 characters are
        // ambiguous in the object database, so an exact-7 pin would eventually fail on an unrelated
        // commit, with no code change, as the repo grows.
        Assert.Matches(@"^0\.9\.0(\+g[0-9a-f]{7,})?$", info!);
    }
}
