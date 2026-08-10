using System.IO;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pins the Components panel's XAML wiring as SOURCE TEXT (2026-08-11), the
/// ShellOwnerWiringTests instrument, for a defect a ViewModel test structurally cannot catch.
///
/// `RefreshCommand` existed on the ViewModel from the day the panel shipped and behaved correctly
/// when invoked - a VM test calling it passes either way. It was simply bound to nothing in the
/// XAML, so no user could ever reach it, and the Tier 1D smoke runbook's "press Refresh" step was
/// unperformable. That is the whole failure mode: working logic with no route to it, invisible to
/// every test that exercises the logic directly.
///
/// This suite has no STA/dispatcher harness so the page cannot be constructed here; asserting on
/// the markup is the honest instrument.</summary>
public sealed class ComponentsPanelWiringTests
{
    private static string SettingsPageXaml() => File.ReadAllText(RepoPaths.AppXaml("SettingsPage.xaml"));

    [Fact]
    public void Refresh_is_reachable_from_the_markup()
    {
        Assert.Contains("Components.RefreshCommand", SettingsPageXaml());
    }

    [Fact]
    public void The_component_button_binds_its_label_rather_than_hardcoding_Download()
    {
        // The button is now offered for an installed component too, so fixed "Download" text would
        // read as a mistake against a row already marked Installed.
        string xaml = SettingsPageXaml();

        Assert.Contains("{Binding DownloadLabel}", xaml);
        Assert.DoesNotContain("<Button Content=\"Download\"", xaml);
    }
}
