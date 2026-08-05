using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pins the EnsureBuildOutputStaysInsideRepo guard in build/BuildOutputGuard.targets.
///
/// WHY THIS EXISTS: a dozen plans under docs/plans/ prescribe
/// `-p:BaseOutputPath=&lt;%TEMP%\...&gt;` as the workaround for MSB3027 (the running app locking
/// bin\). It relocates the test assembly outside the repository, so RepoPaths.SolutionRoot()
/// below cannot find .git - every RepoPaths-anchored test then fails or, worse, validates a
/// different tree - and on this machine it also left 27 stray build folders (~500 MB) at the root
/// of C:. The guard turns that from a documented rule into a build error.
///
/// F8 (final whole-branch review, 2026-08-05): the target used to live inside
/// src/Directory.Build.props, which MSBuild never reaches from tests\ - so
/// `dotnet build tests/... --no-dependencies -o &lt;outside&gt;` evaluated no src\ project, no guard
/// ran, and the test assembly landed outside the repo. It now lives in ONE shared .targets file
/// imported by both src/Directory.Build.props and tests/Directory.Build.props, and the fourth spawn
/// below covers the test-project route directly.
///
/// FOUR of these facts SPAWN A REAL BUILD. That is deliberate: a source-text assertion can pin the
/// target's text while the target silently never fires, which is the exact class of defect this
/// round's DiagnosticsWiringTests idiom exists to catch. Each spawn costs ~1s because the guard
/// errors before PrepareForBuild's MakeDir, so no compilation happens and nothing is written.</summary>
public sealed class BuildOutputGuardTests
{
    private const string TargetName = "EnsureBuildOutputStaysInsideRepo";

    private static string GuardPath()
        => Path.Combine(RepoPaths.SolutionRoot(), "build", "BuildOutputGuard.targets");

    private static string SrcPropsPath()
        => Path.Combine(RepoPaths.SolutionRoot(), "src", "Directory.Build.props");

    private static string TestsPropsPath()
        => Path.Combine(RepoPaths.SolutionRoot(), "tests", "Directory.Build.props");

    private static string ProbeProject()
        => Path.Combine(RepoPaths.SolutionRoot(), "src", "LocalScribe.Core", "LocalScribe.Core.csproj");

    private static string TestProbeProject()
        => Path.Combine(RepoPaths.SolutionRoot(), "tests", "LocalScribe.App.Tests",
                        "LocalScribe.App.Tests.csproj");

    [Fact]
    public void The_guard_file_declares_the_target_and_hooks_it_before_the_output_dir_is_created()
    {
        string guard = File.ReadAllText(GuardPath());

        Assert.Contains("<Target Name=\"" + TargetName + "\" BeforeTargets=\"PrepareForBuild\">", guard);

        // PrepareForBuild is the hook precisely because its MakeDir task is what first creates
        // $(OutDir). A later hook (BeforeTargets="Build", say) would still fail the build but only
        // AFTER the stray folder had been created outside the repo.
        Assert.DoesNotContain("<Target Name=\"" + TargetName + "\" BeforeTargets=\"Build\">", guard);

        // Error, not Warning: a warning is indistinguishable from the MSB3027 noise this flag was
        // reached for in the first place.
        Assert.Contains("Code=\"LS0001\"", guard);
        Assert.Contains("Code=\"LS0002\"", guard);
    }

    [Fact]
    public void Both_src_and_tests_import_the_one_shared_guard_and_neither_redeclares_it()
    {
        // F8: ONE copy of the target logic. Two copies could drift apart silently, which is exactly
        // the failure the shared ShutdownFlush.Timeout constant was introduced for in this same
        // round - so the pin is "declared once, imported twice", not "present in both".
        string src = File.ReadAllText(SrcPropsPath());
        string tests = File.ReadAllText(TestsPropsPath());
        const string import = "<Import Project=\"$(MSBuildThisFileDirectory)..\\build\\BuildOutputGuard.targets\" />";

        Assert.Contains(import, src);
        Assert.Contains(import, tests);
        Assert.DoesNotContain("<Target Name=\"" + TargetName + "\"", src);
        Assert.DoesNotContain("<Target Name=\"" + TargetName + "\"", tests);

        // tests\ takes the guard and NOTHING else: a test assembly must not carry the product's
        // version stamp (Assembly.GetName().Version lands in every session.json as evidentiary,
        // append-only data) or the git-sha InformationalVersion.
        Assert.DoesNotContain("<Version>", tests);
        Assert.DoesNotContain("StampGitShaIntoInformationalVersion", tests);
    }

    [Fact]
    public void The_guard_anchors_on_the_repo_root_and_leaves_PublishDir_alone()
    {
        string guard = File.ReadAllText(GuardPath());

        // The repo root is derived from the GUARD FILE's own location - $(MSBuildThisFileDirectory)
        // is the importee's directory, not the importer's - so build\ .. resolves to the repo root
        // from both import sites, and the guard keeps working in a linked worktree (where the root
        // is not F:\LocalScribe). Moving the guard file up or down a level would break this.
        Assert.Contains("NormalizeDirectory('$(MSBuildThisFileDirectory)..')", guard);
        Assert.Equal(RepoPaths.SolutionRoot(),
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(GuardPath())!, "..")));

        // Both escape routes, MEASURED 2026-08-05 on SDK 10.0.302:
        //   dotnet build/test -p:BaseOutputPath=X -> BaseOutputPath=X
        //   dotnet build -o X                     -> BaseOutputPath stays bin\, OutDir=X
        Assert.Contains("'$(BaseOutputPath)'", guard);
        Assert.Contains("'$(OutDir)'", guard);

        // PublishDir is deliberately NOT guarded. `dotnet publish -o <dir>` sets PublishDir ONLY -
        // BaseOutputPath and OutDir stay inside the repo - and publishing the Assistant/Diarizer
        // helpers to a scratch folder before copying the single .exe is a real, documented workflow
        // (docs/plans/2026-07-19-llm-foundation-summaries-plan.md). Guarding it would break that.
        Assert.DoesNotContain("$(PublishDir)", guard);
    }

    [Fact]
    public void A_build_with_BaseOutputPath_outside_the_repo_fails_and_writes_nothing_there()
    {
        string outside = UnusedTempPath();

        (int exit, string output) = RunMsBuild("-p:BaseOutputPath=" + outside + Path.DirectorySeparatorChar);

        Assert.NotEqual(0, exit);
        Assert.Contains("error LS0001", output);
        // The whole point of hooking PrepareForBuild: a rejected build leaves no residue outside
        // the repository. If this ever fails, the guard is firing too late.
        Assert.False(Directory.Exists(outside), "guard fired but still created " + outside);
    }

    [Fact]
    public void A_build_with_OutDir_outside_the_repo_fails_and_writes_nothing_there()
    {
        string outside = UnusedTempPath();

        // This is the shape `dotnet build -o <dir>` produces; BaseOutputPath is untouched by it,
        // so LS0001 cannot catch it and LS0002 must.
        (int exit, string output) = RunMsBuild("-p:OutDir=" + outside + Path.DirectorySeparatorChar);

        Assert.NotEqual(0, exit);
        Assert.Contains("error LS0002", output);
        Assert.False(Directory.Exists(outside), "guard fired but still created " + outside);
    }

    [Fact]
    public void A_TEST_project_build_outside_the_repo_is_rejected_too()
    {
        // F8 (final whole-branch review, 2026-08-05): THE gap. `--no-dependencies` evaluates no
        // src\ project, so before tests/Directory.Build.props existed no guard ran at all on this
        // route: the test assembly landed outside the repository and RepoPaths.SolutionRoot()
        // walked up past the repo into whatever .git it found first - silently validating a
        // different tree, which is rationale #1 in the guard's own comment block and is about THIS
        // very assembly. Deliberately spawned against a TEST csproj, not a src one.
        string outside = UnusedTempPath();

        (int exit, string output) = RunMsBuild("-p:OutDir=" + outside + Path.DirectorySeparatorChar,
            project: TestProbeProject(), noDependencies: true);

        Assert.NotEqual(0, exit);
        Assert.Contains("error LS0002", output);
        Assert.False(Directory.Exists(outside), "guard fired but still created " + outside);
    }

    [Fact]
    public void The_guard_passes_for_the_repo_s_own_output_paths()
    {
        // Positive control. Without it a condition that rejects EVERY path would satisfy both facts
        // above. Runs the target alone (-t:), so no compilation and no contention with the bin\
        // assemblies this test host has loaded.
        (int exit, string output) = RunMsBuild("-t:" + TargetName);

        Assert.Equal(0, exit);
        Assert.DoesNotContain("error LS000", output);
    }

    private static string UnusedTempPath()
        => Path.Combine(Path.GetTempPath(), "localscribe-outputguard-" + Guid.NewGuid().ToString("N"));

    /// <summary>Runs `dotnet build` on a real project with one extra argument. --no-restore keeps
    /// it to evaluation plus the guard; --nologo keeps the output assertable. `noDependencies`
    /// reproduces the route F8 closed: with it, a test project's src\ ProjectReferences are never
    /// evaluated, so nothing but tests/Directory.Build.props can supply the guard.</summary>
    private static (int Exit, string Output) RunMsBuild(string extraArg, string? project = null,
        bool noDependencies = false)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = RepoPaths.SolutionRoot(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(project ?? ProbeProject());
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("--no-restore");
        if (noDependencies) psi.ArgumentList.Add("--no-dependencies");
        // Explicit: a test must not leave persistent MSBuild worker processes on the user's machine
        // after the run, and node reuse is on by default for `dotnet build`.
        psi.ArgumentList.Add("-nodeReuse:false");
        psi.ArgumentList.Add(extraArg);

        var sb = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (sb) { sb.AppendLine(e.Data); } } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (sb) { sb.AppendLine(e.Data); } } };

        Assert.True(proc.Start(), "could not start dotnet");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            // F9 (final whole-branch review, 2026-08-05): the Assert.True below THROWS on timeout,
            // and `using var proc` disposes WITHOUT killing - so a dotnet build wedged on a NuGet
            // or antivirus lock left an orphaned dotnet process on the user's machine after the run
            // had already reported a failure. entireProcessTree because `dotnet build` spawns
            // MSBuild workers (nodeReuse is off above, but the in-flight children are still ours).
            Assert.True(proc.WaitForExit(120_000), "dotnet build did not exit within 120s");
            proc.WaitForExit(); // flushes the async readers
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        }

        lock (sb)
        {
            return (proc.ExitCode, sb.ToString());
        }
    }
}
