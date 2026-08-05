using System.IO;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Source-text pins for the diagnostics wiring in App.xaml.cs and TrayIconHost.cs (Tier 1
/// plan A, 2026-08-05). Those two files have NO unit coverage at all - 105 test files, no
/// AppTests.cs, no TrayIconHostTests.cs - and every policy this round adds is already extracted
/// into a WPF-free tested class. What is left is one-line wiring, and a text assertion is the only
/// guard available for it; XamlHygieneTests.AppIcon_ExistsAndIsWiredInCsproj asserts on raw csproj
/// text the same way. If one of these fails after a refactor, re-point the pin - do not delete it
/// and do not delete the wiring.</summary>
public sealed class DiagnosticsWiringTests
{
    private static string App() => File.ReadAllText(RepoPaths.AppXaml("App.xaml.cs"));

    [Fact]
    public void Startup_records_the_build_stamp_as_the_first_diagnostic_line()
    {
        string app = App();
        Assert.Contains("_log = comp.Log;", app);
        Assert.Contains("\"LocalScribe started\"", app);
        Assert.Contains("\"build=\" + comp.BuildInfo", app);
    }
}
