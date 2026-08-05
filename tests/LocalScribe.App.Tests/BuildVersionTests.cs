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
    public void The_props_file_is_scoped_to_src_and_not_the_repo_root()
    {
        // A repo-root Directory.Build.props would silently apply to all 13 csproj files, including
        // tools/generate-icon and tools/UiaProbe. MSBuild stops at the FIRST match walking up, so
        // keeping the only copy under src/ is what scopes it to the eight shipping projects.
        Assert.False(File.Exists(Path.Combine(RepoPaths.SolutionRoot(), "Directory.Build.props")));
        Assert.False(File.Exists(Path.Combine(RepoPaths.SolutionRoot(), "Directory.Build.targets")));
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
        Assert.Matches(@"^0\.9\.0(\+g[0-9a-f]{7})?$", info!);
    }
}
