using System.IO;
using System.Linq;
using LocalScribe.App.Services;
using LocalScribe.Core.Import;
using LocalScribe.Core.Transcription;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>THE deliverable of the packaging round (2026-08-06 design note, decision 4): every
/// component locator, run against a real published output directory with NO .slnx anywhere above
/// it, must resolve.
///
/// Why this and not the downloader: every locator carries a dev-convenience probe that walks up
/// to the repo root, and on a developer's machine those probes succeed whatever the install
/// layout is. So the suite is green on a machine where the shipped layout is broken - which is
/// exactly what happened, for months, to ffmpeg. Nothing had ever exercised the shipping path.
///
/// The trick that makes this honest is COPYING the published tree to a temp directory outside the
/// repo before probing it. publish\ sits inside the checkout, so probing it in place would still
/// find LocalScribe.slnx two levels up and the walk-up would rescue every miss, which is the
/// failure mode this test exists to catch.
///
/// SKIPPED (not failed) when publish\app is absent: build.ps1 produces it and a plain
/// `dotnet test` run has no obligation to have built an installer first. It is a CI job that runs
/// build.ps1 and then this, per the design note - "it belongs in CI, over the real dotnet publish
/// output, not over bin\Debug".</summary>
public sealed class PublishedLayoutTests : IDisposable
{
    private readonly string _staged =
        Path.Combine(Path.GetTempPath(), "ls-published-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_staged, recursive: true); } catch { } }

    /// <summary>The published app directory, copied OUT of the repo so no .slnx sits above it -
    /// or null when there is no published build to check.</summary>
    private string? StagePublishedApp()
    {
        string published = Path.Combine(RepoPaths.SolutionRoot(), "publish", "app");
        if (!Directory.Exists(published)) return null;
        // An EMPTY directory is not a published build (2026-08-11). build.ps1 used to create its
        // output folders before running this very suite, so the gate saw a bare publish\app, staged
        // an empty tree, and failed every locator assertion - meaning a from-clean build.ps1 could
        // never pass its own gate. The script now creates them afterwards; this guard means any
        // other caller that leaves a scaffold behind cannot resurrect the same failure.
        if (!Directory.EnumerateFileSystemEntries(published).Any()) return null;

        // Copy the shallow shape the locators actually probe. A full recursive copy of a 1.2 GB
        // self-contained publish would make this test take minutes for no extra coverage: every
        // probe below looks at <base>\ffmpeg\, <base>\models\, <base>\assistant\ and two exes
        // beside the binary.
        Directory.CreateDirectory(_staged);
        foreach (string dir in Directory.EnumerateDirectories(published))
        {
            string name = Path.GetFileName(dir);
            if (name is not ("ffmpeg" or "models" or "assistant")) continue;
            foreach (string src in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(published, src);
                string dest = Path.Combine(_staged, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                // Zero-length stand-ins: every probe here is presence/size-based, and copying
                // multi-gigabyte weights would be pointless. Length > 0 is what the probes check.
                File.WriteAllText(dest, "x");
            }
        }
        foreach (string exe in Directory.EnumerateFiles(published, "*.exe"))
            File.WriteAllText(Path.Combine(_staged, Path.GetFileName(exe)), "x");

        // Prove the premise before relying on it: nothing above the staged tree may be a checkout.
        for (var d = new DirectoryInfo(_staged); d is not null; d = d.Parent)
            Assert.False(File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx")),
                "the staged tree must have no .slnx above it, or the repo walk-up would rescue "
                + "every miss and this test would prove nothing");
        return _staged;
    }

    [Fact]
    public void Every_component_resolves_against_a_published_layout_with_no_repo_above_it()
    {
        if (StagePublishedApp() is not { } app) return;   // no published build - nothing to check

        // ffmpeg: the one that was actually broken. FindToolsDir requires BOTH exes.
        Assert.Equal(Path.Combine(app, "ffmpeg"), FfmpegLocator.FindToolsDir(app, env: null));

        // models: must land BESIDE the binary, not at some repo root that is not there.
        Assert.Equal(Path.Combine(app, "models"), ModelPaths.ResolveRoot(app, env: null));

        // The two stdio helpers CompositionRoot resolves at AppContext.BaseDirectory.
        foreach (string helper in new[] { "LocalScribe.Diarizer.exe", "LocalScribe.Fetch.exe" })
            Assert.True(File.Exists(Path.Combine(app, helper)),
                helper + " must sit beside the app - CompositionRoot resolves it at "
                + "AppContext.BaseDirectory and there is no repo to fall back to on an installed machine");

        // Speaker detection needs the helper AND both sherpa models; this is the single probe the
        // Components panel shows as one row, so it answers "can Split Speakers run at all".
        Assert.Null(DiarisationAvailability.Probe(
            name => Path.Combine(app, "models", name),
            Path.Combine(app, "LocalScribe.Diarizer.exe")));
    }

    [Fact]
    public void The_published_models_folder_carries_the_component_manifest_the_panel_reads()
    {
        if (StagePublishedApp() is not { } app) return;

        // Without it the Components panel renders its probe-only rows and offers no downloads at
        // all - so a user with no weights would have no in-app route to obtain any.
        Assert.True(File.Exists(Path.Combine(app, "models", ComponentCatalog.FileName)),
            "models\\" + ComponentCatalog.FileName + " must ship - it is the pin list the "
            + "Components panel fetches from, and build.ps1 bundles it deliberately even though "
            + "the weights it names are not bundled");
    }

    [Fact]
    public void No_user_data_is_present_anywhere_in_a_published_build()
    {
        // build.ps1 gates on this too; asserting it here as well means a hand-made publish that
        // never ran the script cannot quietly carry someone's session into a package.
        string published = Path.Combine(RepoPaths.SolutionRoot(), "publish", "app");
        if (!Directory.Exists(published)) return;

        foreach (string pattern in new[] { "settings.json", "*.flac", "*.jsonl" })
            Assert.Empty(Directory.EnumerateFiles(published, pattern, SearchOption.AllDirectories));
        foreach (string name in new[] { "sessions", "diagnostics" })
            Assert.Empty(Directory.EnumerateDirectories(published, name, SearchOption.AllDirectories));
    }
}
