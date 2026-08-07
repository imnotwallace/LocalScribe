using System.IO;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pins build.ps1's load-bearing ORDER as source text (Tier 1 plan D, T1-10,
/// 2026-08-05). A build script cannot be executed from a unit test - it publishes four projects
/// and needs a network for the Velopack tool - but a few facts about it are worth more than the
/// rest of it put together, and every one is silently reversible by a well-meaning edit:
///
/// (1) verify-diarizer.ps1 must run while the app directory holds NOTHING BUT THE APP PUBLISH -
///     i.e. before any helper payload is copied in. That guard asserts sherpa-onnx-c-api.dll is
///     absent from the app directory (sherpa's ORT 1.24.4 would shadow App's 1.22 and break
///     Silero VAD), so running it at that one point means any hit can only have come from the app
///     publish itself - a resurrected ProjectReference. Run it later and a copy step masks or
///     mis-attributes the failure.
/// (2) The signing path must DEGRADE to an unsigned build rather than failing, so CI works
///     before a certificate exists.
/// (3) No user data may reach the publish output.</summary>
public sealed class ShippingScriptTests
{
    private static string Script()
        => File.ReadAllText(Path.Combine(RepoPaths.SolutionRoot(), "build.ps1"));

    [Fact]
    public void The_build_script_exists_and_chains_every_publish_guard()
    {
        string s = Script();
        foreach (string guard in new[]
                 { "verify-diarizer.ps1", "verify-assistant-publish.ps1", "verify-mcp-publish.ps1" })
            Assert.Contains(guard, s);
    }

    [Fact]
    public void The_diarizer_guard_runs_before_the_helper_is_copied_beside_the_app()
    {
        string s = Script();
        int guard = s.IndexOf("verify-diarizer.ps1", StringComparison.Ordinal);
        int copy = s.IndexOf("Copy-Item $diarizerExe", StringComparison.Ordinal);
        Assert.True(guard >= 0 && copy >= 0);
        Assert.True(guard < copy,
            "verify-diarizer.ps1 asserts sherpa's loose payload is ABSENT from the app directory, "
            + "so it must run while that directory holds only the app publish - before any helper "
            + "is copied into it, so a hit can only mean the app publish itself produced it.");
    }

    [Fact]
    public void An_absent_certificate_warns_loudly_and_still_produces_an_unsigned_build()
    {
        string s = Script();
        Assert.Contains("UNSIGNED", s);
        Assert.Contains("LOCALSCRIBE_SIGN_THUMBPRINT", s);
        Assert.Contains("--signParams", s);
    }

    [Fact]
    public void The_large_models_guard_is_opt_in_so_a_tiny_base_bundle_is_not_a_failure()
    {
        string s = Script();
        Assert.Contains("verify-import-models.ps1", s);
        Assert.Contains("WithLargeModels", s);
    }

    [Fact]
    public void The_model_free_test_filter_is_the_gate()
    {
        Assert.Contains("Category!=Fixture", Script());
    }

    [Fact]
    public void Both_single_file_publishes_bundle_their_natives_instead_of_leaving_them_loose()
    {
        // PublishSingleFile ALONE leaves native dependencies loose beside the exe, and both the
        // diarizer and the fetch helper are copied out of their staging folder BY EXE ONLY. Drop
        // the flag from either and the shipped helper cannot start - the diarizer half surfaces as
        // a DiarisationException, the fetch half as "the download helper exited with code N", and
        // neither says the real reason. There must be exactly one flag per single-file publish.
        string s = Script();
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            s, @"-p:IncludeNativeLibrariesForSelfExtract=true").Count);
        Assert.Contains("$strayFetch", s);      // and the assertion that proves it took effect
    }

    [Fact]
    public void The_package_version_is_shape_checked_so_an_unevaluated_property_cannot_reach_vpk()
    {
        // build.ps1 reads Directory.Build.props with [xml], which does NOT evaluate MSBuild
        // property functions. An emptiness guard cannot catch a literal "$(Version)" - it is
        // non-empty, therefore truthy - so the guard must check the SHAPE. Without this, a
        // packVersion that is not SemVer only fails at the manual packaging run, deep inside vpk.
        string s = Script();
        Assert.Contains(@"'^\d+\.\d+\.\d+$'", s);
        Assert.DoesNotContain("PackableVersion", s);
    }

    [Fact]
    public void The_publish_output_is_checked_for_user_data_before_anything_is_packaged()
    {
        // Sessions live in %USERPROFILE%\LocalScribe and settings.json in %APPDATA%\LocalScribe,
        // so no user data can reach the publish output by the current layout. That is true by
        // ACCIDENT of layout, not by contract, and this installer is about to be handed to
        // strangers. The guard makes it true by contract: a settings.json, a sessions or
        // diagnostics folder, or any .flac/.jsonl in the package is a build failure, not a
        // surprise a recipient discovers.
        string s = Script();
        Assert.Contains("$userDataPatterns", s);
        foreach (string pattern in new[] { "settings.json", "sessions", "diagnostics", "*.flac", "*.jsonl" })
            Assert.Contains(pattern, s);
    }

    [Fact]
    public void Ffmpeg_is_bundled_into_the_package_so_Import_is_not_dead_on_a_fresh_install()
    {
        // Found by RUNNING the script (2026-08-06): the plan's build.ps1 had no ffmpeg step at
        // all, so the first installer built from it carried no ffmpeg\ directory and Import would
        // have been permanently greyed out on every installed machine. That is the exact
        // shipped-to-a-stranger failure the packaging design note exists to prevent, and a green
        // build said nothing about it.
        string s = Script();
        Assert.Contains("$ffmpegOut", s);
        Assert.Contains("ffplay.exe", s);       // excluded by name - 17 MB nothing probes for
        Assert.Contains("ffprobe.exe", s);      // and both required exes are asserted present
    }

    [Fact]
    public void Release_assets_are_hashed_because_an_unsigned_download_has_no_other_integrity_signal()
    {
        // This ships UNSIGNED by default as a public GitHub release, so a published SHA-256 is the
        // open-source substitute for a certificate and the only way a stranger can check they got
        // what was built. It is also consistent with everything else this product does - every
        // model in component-manifest.json is SHA-256 pinned and verified fail-closed, and every
        // finalized session is sealed in manifest.json.
        string s = Script();
        Assert.Contains("SHA256SUMS.txt", s);
        Assert.Contains("Get-FileHash", s);
        // coreutils format ("<hash>  <name>") so `sha256sum -c` works, and no BOM - sha256sum
        // folds a BOM into the first hash and rejects every line.
        Assert.Contains("UTF8Encoding", s);
    }

    [Fact]
    public void CI_builds_and_runs_the_model_free_suite_on_push_with_fixtures_kept_manual()
    {
        string wf = File.ReadAllText(Path.Combine(
            RepoPaths.SolutionRoot(), ".github", "workflows", "ci.yml"));

        Assert.Contains("windows-latest", wf);            // net10.0-windows + WPF: no other runner works
        Assert.Contains("dotnet build", wf);
        Assert.Contains("Category!=Fixture", wf);
        // The fixture suite needs model weights and privileged audio that are never committed, so
        // it can only ever be a MANUAL run on a machine that has them.
        Assert.Contains("workflow_dispatch", wf);
        Assert.Contains("Category=Fixture", wf);
    }
}
