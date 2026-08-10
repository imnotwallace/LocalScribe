namespace LocalScribe.Core.Mcp;

/// <summary>Resolves LocalScribe.Mcp.exe, the way AssistantHelperLocator resolves the assistant
/// helper: the LOCALSCRIBE_MCP env var (a folder containing the exe), else the "mcp\" FOLDER
/// PUBLISH beside the binary (what build.ps1 produces), else "tools\mcp\" at the repo root (dev,
/// found by walking up to LocalScribe.slnx).
///
/// The server is published self-contained, so it CANNOT sit in the app's own root without its
/// runtime colliding with the app's - hence the subfolder, and hence this locator. Settings
/// previously composed &lt;app&gt;\LocalScribe.Mcp.exe directly, which named a path that exists on no
/// installed machine; the resulting failure appears inside the MCP client as "server failed to
/// start", never in LocalScribe, so it is close to undiagnosable from this side.</summary>
public static class McpServerLocator
{
    public const string ExeName = "LocalScribe.Mcp.exe";
    public const string FolderName = "mcp";

    public static string? FindExe()
        => FindExe(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("LOCALSCRIBE_MCP"));

    /// <summary>Testable core; production calls the parameterless overload.</summary>
    public static string? FindExe(string baseDir, string? envOverride)
    {
        if (!string.IsNullOrEmpty(envOverride))
        {
            string fromEnv = Path.Combine(envOverride, ExeName);
            if (File.Exists(fromEnv)) return Path.GetFullPath(fromEnv);
        }

        string beside = ShippingPath(baseDir);
        if (File.Exists(beside)) return beside;

        for (var d = new DirectoryInfo(baseDir); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx")))
            {
                string dev = Path.Combine(d.FullName, "tools", FolderName, ExeName);
                return File.Exists(dev) ? dev : null;
            }
        return null;
    }

    /// <summary>Where the exe belongs when it is not deployed. The config snippet falls back to
    /// this rather than emitting an empty command: an absent server should read as "put it here",
    /// which the user can act on, not as a malformed config the client rejects for another reason.
    /// The ModelPaths.ResolveRoot precedent - a resolver that finds nothing still has to be able to
    /// name the place the files ought to go.</summary>
    public static string ShippingPath(string baseDir) => Path.Combine(baseDir, FolderName, ExeName);
}
