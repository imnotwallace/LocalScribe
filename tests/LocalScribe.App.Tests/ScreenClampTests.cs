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
}
