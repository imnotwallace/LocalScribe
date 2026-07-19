using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ScreenClampTests
{
    // Virtual screen 0,0 1920x1080; pill 220x56.
    [Theory]
    [InlineData(100, 100, 100, 100)]                       // in bounds: unchanged
    [InlineData(-500, 100, 0, 100)]                        // off left: clamped to edge
    [InlineData(3000, 100, 1700, 100)]                     // off right: 1920-220
    [InlineData(100, -50, 100, 0)]                         // off top
    [InlineData(100, 2000, 100, 1024)]                     // off bottom: 1080-56
    public void Clamps_into_virtual_screen(double x, double y, double ex, double ey)
    {
        var (cx, cy) = ScreenClamp.Clamp(x, y, 220, 56, 0, 0, 1920, 1080);
        Assert.Equal(ex, cx);
        Assert.Equal(ey, cy);
    }

    [Fact]
    public void NaN_falls_back_to_top_right_with_margin()
    {
        var (cx, cy) = ScreenClamp.Clamp(double.NaN, double.NaN, 220, 56, 0, 0, 1920, 1080);
        Assert.Equal(1920 - 220 - 16, cx);
        Assert.Equal(16, cy);
    }

    [Fact]
    public void Negative_virtual_origin_multimonitor_is_respected()
    {
        // Second monitor to the LEFT: virtual screen starts at -1920.
        var (cx, _) = ScreenClamp.Clamp(-1800, 10, 220, 56, -1920, 0, 3840, 1080);
        Assert.Equal(-1800, cx);                           // valid position on the left monitor
    }

    // Compact-console pill (design 2026-07-18 section 6): pin the exact clamp behavior the 420x64
    // pill's restore path relies on (Task 4 loads the remembered "consoleCompact" position through
    // this SAME helper, the overlay-pill precedent). PASS immediately - these pin existing behavior
    // for the new caller; a future ScreenClamp change that breaks the pill now fails loudly here.
    [Theory]
    [InlineData(2500, 400, 1500, 400)]      // saved on a since-removed right monitor -> pulled inside (1920-420)
    [InlineData(1800, 1050, 1500, 1016)]    // partially off bottom-right -> fully visible (1080-64)
    [InlineData(-60, -20, 0, 0)]            // partially off top-left -> snapped to the origin
    public void Compact_pill_restore_clamps_into_the_virtual_screen(double x, double y, double ex, double ey)
    {
        var (cx, cy) = ScreenClamp.Clamp(x, y, 420, 64, 0, 0, 1920, 1080);
        Assert.Equal(ex, cx);
        Assert.Equal(ey, cy);
    }

    [Fact]
    public void Compact_pill_negative_origin_multimonitor_position_is_preserved()
    {
        // Left-of-primary monitor: virtual screen origin (-1920, 0), span 3840x1080. A valid
        // remembered pill position on the left monitor must NOT be dragged onto the primary.
        var (cx, cy) = ScreenClamp.Clamp(-1500, 900, 420, 64, -1920, 0, 3840, 1080);
        Assert.Equal(-1500, cx);
        Assert.Equal(900, cy);
    }
}
