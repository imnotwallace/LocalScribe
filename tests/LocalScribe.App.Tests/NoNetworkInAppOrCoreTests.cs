using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The zero-network property, pinned (Tier 1 plan D, T1-10, 2026-08-05). A grep for
/// the network-stack namespaces and types over the two shipping projects a user actually runs
/// returns ZERO matches, and that is checkable by anyone in one command - it is the strongest
/// form the product's privacy claim can take. Tier 1D adds a component downloader, which lives
/// in a SEPARATE helper executable spawned on explicit user action (the ProcessDiarisationHelper
/// pattern), precisely so this stays at zero. An in-process client for that stack is REJECTED
/// regardless of convenience.
///
/// obj/ and bin/ are excluded because the SDK writes
/// obj/&lt;cfg&gt;/&lt;tfm&gt;/LocalScribe.Core.GlobalUsings.g.cs containing a generated global
/// using for that namespace (a consequence of ImplicitUsings, not of any call). Generated
/// output is not source and tripping over it would make this test worthless.
///
/// Velopack's updater type is pinned alongside: Velopack is referenced for INSTALL hooks only,
/// and the spec's out-of-scope list rules out in-process auto-update. Constructing one would be
/// the first line of network code back into the app.</summary>
public sealed class NoNetworkInAppOrCoreTests
{
    private static readonly Regex Forbidden = new(
        @"System\.Net|HttpClient|Socket|WebRequest|\bDns\b|UpdateManager", RegexOptions.Compiled);

    private static IEnumerable<string> ShippingSources(string projectFolder)
    {
        string root = Path.Combine(RepoPaths.SolutionRoot(), "src", projectFolder);
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)
                     && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("LocalScribe.App")]
    [InlineData("LocalScribe.Core")]
    public void No_shipping_source_file_names_the_network_stack(string projectFolder)
    {
        var hits = new List<string>();
        foreach (string file in ShippingSources(projectFolder))
        {
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (Forbidden.IsMatch(lines[i]))
                    hits.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }

        Assert.True(hits.Count == 0,
            $"{projectFolder} must contain no network-stack references. Found {hits.Count}:"
            + Environment.NewLine + string.Join(Environment.NewLine, hits)
            + Environment.NewLine
            + "The downloader belongs in src/LocalScribe.Fetch, spawned as a stdio child. "
            + "If this fired on a COMMENT, reword it - the claim is grep-checkable, so the grep "
            + "must stay clean of the words themselves.");
    }

    [Fact]
    public void The_scan_actually_covers_a_meaningful_number_of_files()
    {
        // A guard on the guard: if a path change silently made ShippingSources enumerate nothing,
        // the two facts above would pass vacuously and the property would be unprotected.
        Assert.True(ShippingSources("LocalScribe.App").Count() > 50);
        Assert.True(ShippingSources("LocalScribe.Core").Count() > 100);
    }

    [Fact]
    public void The_fetch_helper_is_a_separate_project_and_is_the_ONLY_one_that_may_use_the_network()
    {
        // The constraint is architectural, so assert the architecture: a project that exists, is
        // in the solution, and is not referenced by App or Core (a ProjectReference would drag
        // its dependency graph back into the very assemblies this class protects).
        string root = RepoPaths.SolutionRoot();
        Assert.True(File.Exists(Path.Combine(root, "src", "LocalScribe.Fetch", "LocalScribe.Fetch.csproj")));
        Assert.Contains("LocalScribe.Fetch", File.ReadAllText(Path.Combine(root, "LocalScribe.slnx")));

        foreach (string proj in new[] { "LocalScribe.App", "LocalScribe.Core" })
        {
            string csproj = File.ReadAllText(
                Path.Combine(root, "src", proj, proj + ".csproj"));
            Assert.DoesNotContain("LocalScribe.Fetch", csproj);
        }
    }
}
