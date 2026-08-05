namespace LocalScribe.App.Services;

/// <summary>Tier 1 plan A (2026-08-05, fix round 1): the ONE ceiling both exit-path diagnostic
/// flushes bound their wait to - App.OnExit's blocking backstop and TrayIconHost's Exit-menu
/// await. Before this constant existed each site carried its own literal, and the two had already
/// drifted once: round 1 shipped OnExit BOUNDED but the tray Exit flush fully UNBOUNDED, which
/// would hang the app's only Exit menu item forever against a wedged drain (dead disk, vanished
/// network path, antivirus holding the file). A plain value, not a WPF type, so the number is
/// reachable from a real unit test rather than only a source-text pin - App.xaml.cs and
/// TrayIconHost.cs have zero test coverage (see DiagnosticsWiringTests' class doc), but this file
/// does not need WPF and so is not stuck in that boat.</summary>
public static class ShutdownFlush
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
}
