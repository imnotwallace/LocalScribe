using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace LocalScribe.App;

/// <summary>The two Win32 calls the overlay needs (design decision 12). Call both from
/// OnSourceInitialized (the HWND must exist).</summary>
public static class NativeWindowInterop
{
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint WDA_NONE = 0x0;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern long GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern long SetWindowLongPtrW(nint hwnd, int index, long value);

    /// <summary>WDA_EXCLUDEFROMCAPTURE: the pill vanishes from screen shares/recordings while
    /// staying visible locally - a lawyer sharing their screen over Webex gets a clean share
    /// and the recording signal stays local (Win10 2004+; returns false silently before).</summary>
    public static void ExcludeFromCapture(Window window)
        => SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, WDA_EXCLUDEFROMCAPTURE);

    /// <summary>WDA_NONE: undo ExcludeFromCapture - the window becomes visible to screen
    /// shares/recordings again (the Privacy toggle was turned off).</summary>
    public static void IncludeInCapture(Window window)
        => SetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, WDA_NONE);

    /// <summary>WS_EX_NOACTIVATE + TOOLWINDOW: clicking Pause/Stop mid-call never steals focus
    /// from the meeting; no taskbar/alt-tab presence.</summary>
    public static void MakeNoActivate(Window window)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE,
            GetWindowLongPtrW(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }
}
