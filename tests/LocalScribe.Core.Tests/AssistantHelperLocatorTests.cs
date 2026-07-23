using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

public sealed class AssistantHelperLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-helper-loc-").FullName;
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string MakeExe(params string[] relDirParts)
    {
        string dir = Path.Combine([_root, .. relDirParts]);
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, AssistantHelperLocator.ExeName);
        File.WriteAllText(exe, "x");
        return exe;
    }

    [Fact]
    public void Env_override_wins_when_it_contains_the_exe()
    {
        string exe = MakeExe("override");
        MakeExe("base", "assistant");   // would otherwise win
        Assert.Equal(exe, AssistantHelperLocator.FindExe(
            Path.Combine(_root, "base"), Path.Combine(_root, "override")));
    }

    [Fact]
    public void Env_override_without_the_exe_is_ignored()
    {
        Directory.CreateDirectory(Path.Combine(_root, "empty-override"));
        string beside = MakeExe("base", "assistant");
        Assert.Equal(beside, AssistantHelperLocator.FindExe(
            Path.Combine(_root, "base"), Path.Combine(_root, "empty-override")));
    }

    [Fact]
    public void Assistant_subfolder_beside_the_binary_is_found()
    {
        string beside = MakeExe("base", "assistant");
        Assert.Equal(beside, AssistantHelperLocator.FindExe(Path.Combine(_root, "base"), null));
    }

    [Fact]
    public void Repo_root_tools_assistant_is_the_dev_fallback()
    {
        // base dir nested under a repo root marked by LocalScribe.slnx
        File.WriteAllText(Path.Combine(_root, "LocalScribe.slnx"), "<Solution/>");
        string dev = MakeExe("tools", "assistant");
        string baseDir = Path.Combine(_root, "src", "App", "bin", "Debug");
        Directory.CreateDirectory(baseDir);
        Assert.Equal(dev, AssistantHelperLocator.FindExe(baseDir, null));
    }

    [Fact]
    public void Absent_everywhere_is_null_and_the_message_names_the_publish_command()
    {
        string baseDir = Path.Combine(_root, "lonely");
        Directory.CreateDirectory(baseDir);
        Assert.Null(AssistantHelperLocator.FindExe(baseDir, null));
        Assert.Contains("dotnet publish src/LocalScribe.Assistant", AssistantHelperLocator.MissingMessage);
        Assert.Contains("LOCALSCRIBE_ASSISTANT", AssistantHelperLocator.MissingMessage);
    }
}
