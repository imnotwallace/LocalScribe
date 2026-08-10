using LocalScribe.Core.Mcp;

namespace LocalScribe.Core.Tests;

/// <summary>McpServerLocator (2026-08-11). The MCP server is published self-contained into
/// &lt;app&gt;\mcp\ - it cannot sit in the app's own root without its runtime colliding with the
/// app's - and Settings previously composed &lt;app&gt;\LocalScribe.Mcp.exe directly. That handed
/// every user a "Copy config" command pointing at a path no installed machine has, and the failure
/// surfaces inside the MCP client ("server failed to start"), never in LocalScribe.
///
/// Probe order matches AssistantHelperLocator and ModelPaths exactly - env, then beside the binary,
/// then the repo walk-up - so the SHIPPING path is the one exercised first and the dev convenience
/// is the fallback rather than the default (ComponentLocatorOrderTests records why that ordering is
/// load-bearing).</summary>
public sealed class McpServerLocatorTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-mcploc-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Dir(params string[] parts)
    {
        string p = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    private static string Exe(string dir)
    {
        Directory.CreateDirectory(dir);
        string p = Path.Combine(dir, McpServerLocator.ExeName);
        File.WriteAllText(p, "x");
        return p;
    }

    private static void Slnx(string dir) =>
        File.WriteAllText(Path.Combine(dir, "LocalScribe.slnx"), "<Solution/>");

    [Fact]
    public void Finds_the_exe_in_the_mcp_subfolder_beside_the_binary()
    {
        string app = Dir("app");
        string expected = Exe(Path.Combine(app, "mcp"));

        Assert.Equal(expected, McpServerLocator.FindExe(app, envOverride: null));
    }

    [Fact]
    public void Does_not_look_in_the_app_root_itself()
    {
        // The whole defect in one assertion: an exe sitting in the app root is NOT the published
        // layout, and treating it as one is what produced an unusable config for every user.
        string app = Dir("app");
        Exe(app);

        Assert.Null(McpServerLocator.FindExe(app, envOverride: null));
    }

    [Fact]
    public void The_env_override_beats_the_folder_beside_the_binary()
    {
        string app = Dir("app");
        Exe(Path.Combine(app, "mcp"));
        string elsewhere = Dir("elsewhere");
        string expected = Exe(elsewhere);

        Assert.Equal(expected, McpServerLocator.FindExe(app, elsewhere));
    }

    [Fact]
    public void An_env_override_without_the_exe_falls_through_rather_than_winning()
    {
        string app = Dir("app");
        string expected = Exe(Path.Combine(app, "mcp"));
        string emptyOverride = Dir("empty");

        Assert.Equal(expected, McpServerLocator.FindExe(app, emptyOverride));
    }

    [Fact]
    public void Falls_back_to_the_repo_publish_location_for_a_source_build()
    {
        string repo = Dir("repo");
        Slnx(repo);
        string app = Dir("repo", "src", "bin");
        string expected = Exe(Path.Combine(repo, "tools", "mcp"));

        Assert.Equal(expected, McpServerLocator.FindExe(app, envOverride: null));
    }

    [Fact]
    public void Returns_null_when_the_server_is_not_deployed_anywhere()
    {
        string repo = Dir("repo");
        Slnx(repo);
        string app = Dir("repo", "src", "bin");

        Assert.Null(McpServerLocator.FindExe(app, envOverride: null));
    }

    [Fact]
    public void The_shipping_path_names_the_mcp_subfolder_even_when_nothing_is_deployed()
    {
        // The config snippet falls back to this, so it must stay a real rooted path: an absent
        // server should read as "put it here", not as an empty command the client rejects for an
        // unrelated reason.
        string app = Dir("app");

        Assert.Equal(Path.Combine(app, "mcp", McpServerLocator.ExeName),
            McpServerLocator.ShippingPath(app));
    }
}
