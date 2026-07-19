using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public class ToastPlacementTests
{
    // The toast window is a humble shell (WPF windows are not unit-testable headlessly - the
    // ScreenClamp/OverlayWindow precedent); ALL of its decision logic - placement + dismiss -
    // lives here, pure and exact.

    [Fact]
    public void BottomRight_sits_inside_the_work_area_with_the_default_margin()
        // 1920x1040 primary work area (1080 minus a 40px taskbar), 380x120 toast:
        // Left = 1920 - 380 - 16, Top = 1040 - 120 - 16.
        => Assert.Equal((1524d, 904d), ToastPlacement.BottomRight(0, 0, 1920, 1040, 380, 120));

    [Fact]
    public void BottomRight_respects_a_work_area_that_does_not_start_at_origin()
        // Multi-monitor: primary work area at (100, 50), 1000x700.
        => Assert.Equal((704d, 614d), ToastPlacement.BottomRight(100, 50, 1000, 700, 380, 120));

    [Fact]
    public void BottomRight_clamps_an_oversized_toast_to_the_work_area_origin()
        // A toast larger than the work area must pin to the origin, never land off-screen.
        => Assert.Equal((0d, 0d), ToastPlacement.BottomRight(0, 0, 300, 200, 380, 240));

    [Fact]
    public void DismissInterval_maps_seconds_to_a_timer_interval_or_none()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), ToastPlacement.DismissInterval(8));
        Assert.Null(ToastPlacement.DismissInterval(0));     // sticky: no timer at all
        Assert.Null(ToastPlacement.DismissInterval(-3));
    }
}
