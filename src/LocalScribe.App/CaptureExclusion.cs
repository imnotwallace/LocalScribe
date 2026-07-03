using System.Windows;
namespace LocalScribe.App;

/// <summary>One-line policy shim over NativeWindowInterop for transcript-bearing windows
/// (design section 2: MainWindow, read views and the live view are capture-excluded by
/// default, governed by settings.Privacy.ExcludeWindowsFromCapture; OverlayWindow keeps its
/// own OverlaySetting.ExcludeFromCapture, unchanged). Must run after the HWND exists
/// (OnSourceInitialized or later). NOT headlessly unit-testable - SetWindowDisplayAffinity
/// needs a real HWND - so verification is a Stage 4 smoke-runbook item; the pure
/// decide-to-reapply logic lives in Services/CaptureExclusionPolicy and IS unit-tested.</summary>
public static class CaptureExclusion
{
    public static void Apply(Window window, bool exclude)
    {
        if (exclude) NativeWindowInterop.ExcludeFromCapture(window);
        else NativeWindowInterop.IncludeInCapture(window);
    }
}
