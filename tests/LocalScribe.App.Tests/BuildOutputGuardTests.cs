using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pins the EnsureBuildOutputStaysInsideRepo guard in src/Directory.Build.props.
///
/// WHY THIS EXISTS: a dozen plans under docs/plans/ prescribe
/// `-p:BaseOutputPath=&lt;%TEMP%\...&gt;` as the workaround for MSB3027 (the running app locking
/// bin\). It relocates the test assembly outside the repository, so RepoPaths.SolutionRoot()
/// below cannot find .git - every RepoPaths-anchored test then fails or, worse, validates a
/// different tree - and on this machine it also left 27 stray build folders (~500 MB) at the root
/// of C:. The guard turns that from a documented rule into a build error.
///
/// Two of these facts SPAWN A REAL BUILD. That is deliberate: a source-text assertion can pin the
/// target's text while the target silently never fires, which is the exact class of defect this
/// round's DiagnosticsWiringTests idiom exists to catch. Each spawn costs ~1s because the guard
/// errors before PrepareForBuild's MakeDir, so no compilation happens and nothing is written.</summary>
public sealed class BuildOutputGuardTests
{
    private const string TargetName = "EnsureBuildOutputStaysInsideRepo";

    private static string PropsPath()
        => Path.Combine(RepoPaths.SolutionRoot(), "src", "Directory.Build.props");

    private static string ProbeProject()
        => Path.Combine(RepoPaths.SolutionRoot(), "src", "LocalScribe.Core", "LocalScribe.Core.csproj");

    [Fact]
    public void The_props_file_declares_the_guard_and_hooks_it_before_the_output_dir_is_created()
    {
        string props = File.ReadAllText(PropsPath());

        Assert.Contains("<Target Name=\"" + TargetName + "\" BeforeTargets=\"PrepareForBuild\">", props);

        // PrepareForBuild is the hook precisely because its MakeDir task is what first creates
        // $(OutDir). A later hook (BeforeTargets="Build", say) would still fail the build but only
        // AFTER the stray folder had been created outside the repo.
        Assert.DoesNotContain("<Target Name=\"" + TargetName + "\" BeforeTargets=\"Build\">", props);

        // Error, not Warning: a warning is indistinguishable from the MSB3027 noise this flag was
        // reached for in the first place.
        Assert.Contains("Code=\"LS0001\"", props);
        Assert.Contains("Code=\"LS0002\"", props);
    }

    [Fact]
    public void The_guard_anchors_on_the_repo_root_and_leaves_PublishDir_alone()
    {
        string props = File.ReadAllText(PropsPath());

        // The repo root is derived from the props file's own location, so the guard keeps working
        // in a linked worktree (where the root is not F:\LocalScribe).
        Assert.Contains("NormalizeDirectory('$(MSBuildThisFileDirectory)..')", props);

        // Both escape routes, MEASURED 2026-08-05 on SDK 10.0.302:
        //   dotnet build/test -p:BaseOutputPath=X -> BaseOutputPath=X
        //   dotnet build -o X                     -> BaseOutputPath stays bin\, OutDir=X
        Assert.Contains("'$(BaseOutputPath)'", props);
        Assert.Contains("'$(OutDir)'", props);

        // PublishDir is deliberately NOT guarded. `dotnet publish -o <dir>` sets PublishDir ONLY -
        // BaseOutputPath and OutDir stay inside the repo - and publishing the Assistant/Diarizer
        // helpers to a scratch folder before copying the single .exe is a real, documented workflow
        // (docs/plans/2026-07-19-llm-foundation-summaries-plan.md). Guarding it would break that.
        Assert.DoesNotContain("$(PublishDir)", props);
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

    /// <summary>Runs `dotnet build` on a real src project with one extra argument. --no-restore
    /// keeps it to evaluation plus the guard; --nologo keeps the output assertable.</summary>
    private static (int Exit, string Output) RunMsBuild(string extraArg)
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
        psi.ArgumentList.Add(ProbeProject());
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("--no-restore");
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
        Assert.True(proc.WaitForExit(120_000), "dotnet build did not exit within 120s");
        proc.WaitForExit(); // flushes the async readers

        lock (sb)
        {
            return (proc.ExitCode, sb.ToString());
        }
    }
}
