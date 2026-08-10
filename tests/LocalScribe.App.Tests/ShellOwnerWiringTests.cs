using System.IO;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pins the Application.MainWindow wiring as SOURCE TEXT (Tier 1 plan D, T1-5,
/// 2026-08-05). Application.MainWindow was never assigned, so WPF auto-assigned it to the first
/// Window constructed - OverlayWindow - and the three CenterOwner dialogs centred on the
/// recording pill. The fix is two lines in TrayIconHost plus a closed-window guard in App, none
/// of which can be executed here: this suite has no STA/dispatcher harness, so no test can
/// construct a Window at all. A source-text assertion is the honest instrument - the same one
/// XamlHygieneTests.AppIcon_ExistsAndIsWiredInCsproj uses on the csproj - and it stops a
/// refactor dropping the assignment or, worse, the clear-on-close half.</summary>
public sealed class ShellOwnerWiringTests
{
    private static string Read(string relative)
        => File.ReadAllText(RepoPaths.AppXaml(relative));

    [Fact]
    public void Tray_assigns_the_shell_as_Application_MainWindow_when_it_opens_it()
    {
        string tray = Read("TrayIconHost.cs");
        Assert.Contains("Application.Current.MainWindow = _main;", tray);
    }

    [Fact]
    public void Tray_clears_Application_MainWindow_when_the_shell_closes()
    {
        // The shell GENUINELY closes and is re-created (TrayIconHost's own doc comment). A closed
        // Window left as Owner makes the next ShowDialog throw InvalidOperationException, so the
        // clear is not optional tidiness - it is the half that keeps Export openable after the
        // user closes the manager window once.
        string tray = Read("TrayIconHost.cs");
        Assert.Contains("Application.Current.MainWindow = null;", tray);
    }

    [Fact]
    public void No_dialog_takes_the_raw_MainWindow_as_its_Owner_any_more()
    {
        // Owner = MainWindow was the defect. Every site must go through ShellOwner(), which
        // returns null for an unloaded or closed window rather than handing WPF a dead Owner.
        string app = Read("App.xaml.cs");
        Assert.DoesNotContain("{ Owner = MainWindow }", app);
        Assert.Contains("Window? ShellOwner()", app);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(app, @"Owner = ShellOwner\(\)").Count);
    }
}
