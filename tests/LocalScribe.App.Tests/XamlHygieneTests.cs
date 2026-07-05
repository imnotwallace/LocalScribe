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
        // Stage 5.4 4.3: there must be NO app-global implicit (keyless) TextBlock style. As a Style
        // Setter it overrode accent-button templates and painted dark text on Primary/Danger fills.
        // Default text color now comes from an inheritable TextElement.Foreground on each page/window
        // root (see PageAndWindowRoots_SetInheritableForeground), NOT from this shared dictionary.
        // Keyed styles are written "<Style x:Key=... TargetType=\"TextBlock\">" so only the keyless
        // implicit style matches the tag below.
        Assert.DoesNotContain("<Style TargetType=\"TextBlock\">", xaml);
        Assert.DoesNotContain("TextFillColorPrimaryBrush", xaml);
    }

    [Fact]
    public void PageAndWindowRoots_SetInheritableForeground()
    {
        // Stage 5.4 4.3: the app-global implicit TextBlock style was removed because, as a Style
        // Setter, it overrode accent-button templates and painted dark text on Primary/Danger
        // fills. The replacement is an inheritable TextElement.Foreground on each page/window ROOT
        // container. An inherited value never beats a value set closer in the tree, so accent-button
        // templates keep their white TextOnAccent foreground while loose TextBlocks still inherit a
        // visible color on Mica. Every text-bearing top-level surface must carry the marker.
        const string marker =
            "TextElement.Foreground=\"{DynamicResource TextFillColorPrimaryBrush}\"";
        var roots = new[]
        {
            "MainWindow.xaml",
            Path.Combine("Pages", "SessionsPage.xaml"),
            Path.Combine("Pages", "MattersPage.xaml"),
            "SettingsPage.xaml",
            "ReadViewWindow.xaml",
            "LiveViewWindow.xaml",
            "SessionDetailsWindow.xaml",
            "SplitSpeakersWindow.xaml",
            "OverlayWindow.xaml",
            "ConsentDialog.xaml",
        };
        foreach (var rel in roots)
        {
            string xaml = File.ReadAllText(RepoPaths.AppXaml(rel));
            Assert.True(xaml.Contains(marker),
                $"{rel} must set an inheritable {marker} on its root container");
        }
    }

    [Fact]
    public void AppXaml_DoesNotHardcodeDarkTheme()
    {
        string appXaml = File.ReadAllText(RepoPaths.AppXaml("App.xaml"));
        Assert.DoesNotContain("Theme=\"Dark\"", appXaml);
    }

    [Fact]
    public void AppIcon_ExistsAndIsWiredInCsproj()
    {
        string icoPath = RepoPaths.AppXaml(Path.Combine("Assets", "LocalScribe.ico"));
        Assert.True(File.Exists(icoPath), $"missing branded icon at {icoPath}");
        Assert.True(new FileInfo(icoPath).Length > 0, "icon file is empty");
        string csproj = File.ReadAllText(RepoPaths.AppXaml("LocalScribe.App.csproj"));
        Assert.Contains("<ApplicationIcon>Assets\\LocalScribe.ico</ApplicationIcon>", csproj);
    }

    [Fact]
    public void ShippedXaml_HasNoDisallowedHardcodedBrushes()
    {
        string appDir = Path.Combine(RepoPaths.SolutionRoot(), "src", "LocalScribe.App");
        // Allow-list: exact literal strings that are intentional and theme-agnostic. Keep this list
        // tiny and documented; every entry needs a reason in the code comment above it.
        var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // (none — all hardcoded ARGB brushes were replaced with Fluent theme resources in
            // Tasks 13-14; the consent dialog's warning banner uses
            // SystemFillColorCautionBackgroundBrush and the overlay pill uses
            // ControlFillColorSecondaryBrush + Opacity, so no literal is needed.)
        };
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(file))
            {
                var m = System.Text.RegularExpressions.Regex.Matches(line, "#[0-9A-Fa-f]{6,8}\\b");
                foreach (System.Text.RegularExpressions.Match hit in m)
                    if (!allow.Contains(hit.Value))
                        offenders.Add($"{Path.GetFileName(file)}: {hit.Value}  ({line.Trim()})");
            }
        }
        Assert.True(offenders.Count == 0, "Hardcoded ARGB brushes found:\n" + string.Join("\n", offenders));
    }
}
