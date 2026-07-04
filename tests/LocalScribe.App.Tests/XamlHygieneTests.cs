using System.IO;
using Xunit;

namespace LocalScribe.App.Tests;

public static class RepoPaths
{
    // Walk up from the test assembly to the repo root (the folder containing .git).
    // Anchored on .git, not the solution file (this repo uses LocalScribe.slnx).
    public static string SolutionRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    public static string AppXaml(string relative) =>
        Path.Combine(SolutionRoot(), "src", "LocalScribe.App", relative);
}

public class XamlHygieneTests
{
    [Fact]
    public void SharedDictionary_DeclaresRequiredKeys()
    {
        string xaml = File.ReadAllText(RepoPaths.AppXaml(Path.Combine("Styles", "Fluent.Shared.xaml")));
        foreach (var key in new[] { "MutedText", "WarningText", "FieldLabel", "FieldRow", "Note", "SectionCard" })
            Assert.Contains($"x:Key=\"{key}\"", xaml);
        // Implicit TextBlock style (no key) that sets the Fluent foreground.
        Assert.Contains("TargetType=\"TextBlock\"", xaml);
        Assert.Contains("TextFillColorPrimaryBrush", xaml);
    }

    [Fact]
    public void AppXaml_DoesNotHardcodeDarkTheme()
    {
        string appXaml = File.ReadAllText(RepoPaths.AppXaml("App.xaml"));
        Assert.DoesNotContain("Theme=\"Dark\"", appXaml);
    }
}
