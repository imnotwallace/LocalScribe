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
            Path.Combine("Pages", "SearchPage.xaml"),
            Path.Combine("Pages", "MattersPage.xaml"),
            "SettingsPage.xaml",
            "ReadViewWindow.xaml",
            "LiveViewWindow.xaml",
            "SessionDetailsWindow.xaml",
            "SplitSpeakersWindow.xaml",
            "OverlayWindow.xaml",
            "ConsentDialog.xaml",
            "ImportDialog.xaml",
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
    public void SessionDetails_binds_the_AI_draft_label_on_both_the_streaming_and_persisted_panels()
    {
        // Evidentiary rule (design 7.6, label-on-every-AI-surface): the locked AI-draft label
        // must render on BOTH AI-text surfaces - the streaming preview panel (IsRunning, before
        // any version exists) and the persisted summary panel (HasSummary). Only the XAML
        // binding delivers this guarantee; a VM-only assertion comparing the constant to itself
        // (AssistantTabViewModelTests) proves nothing about what actually renders. This test
        // fails if either binding is deleted.
        string xaml = File.ReadAllText(RepoPaths.AppXaml("SessionDetailsWindow.xaml"));
        const string draftLabelBinding = "{Binding Assistant.DraftLabel}";

        int streamingStart = xaml.IndexOf("Assistant.IsRunning, Converter", StringComparison.Ordinal);
        int persistedStart = xaml.IndexOf("Assistant.HasSummary, Converter", StringComparison.Ordinal);
        Assert.True(streamingStart >= 0, "streaming (IsRunning) panel not found");
        Assert.True(persistedStart >= 0, "persisted (HasSummary) panel not found");
        Assert.True(streamingStart < persistedStart, "expected the streaming panel before the persisted panel");

        string streamingRegion = xaml[streamingStart..persistedStart];
        string persistedRegion = xaml[persistedStart..];
        Assert.Contains(draftLabelBinding, streamingRegion);
        Assert.Contains(draftLabelBinding, persistedRegion);

        // Belt-and-suspenders (brief's minimum bar): >= 2 distinct DraftLabel bindings total, so
        // deleting either one drops the count below 2 and fails even if the region split above
        // ever changes shape.
        int total = System.Text.RegularExpressions.Regex.Matches(xaml,
            System.Text.RegularExpressions.Regex.Escape(draftLabelBinding)).Count;
        Assert.True(total >= 2, $"expected >= 2 DraftLabel bindings, found {total}");
    }

    [Fact]
    public void AssistantChatPanel_labels_both_the_streaming_and_the_turn_AI_text()
    {
        // Evidentiary rule (design 7.6, label-on-every-AI-surface - branch-6 Task-13 lesson):
        // the locked AI-draft label must render on BOTH AI-text surfaces of the shared chat
        // panel - the in-progress streaming preview AND each persisted turn in the history.
        // Only the XAML binding delivers this guarantee; this fails if either is removed.
        string xaml = File.ReadAllText(RepoPaths.AppXaml(Path.Combine("Controls", "AssistantChatPanel.xaml")));
        Assert.Contains("{x:Static vm:AssistantChatViewModel.AiDraftLabel}", xaml);
        Assert.Contains("{Binding AiLabel}", xaml);
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
