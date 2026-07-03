// src/LocalScribe.App/ViewModels/ReadViewPlacement.cs
namespace LocalScribe.App.ViewModels;

/// <summary>Pure placement math for read-view windows (design section 2): the remembered
/// "readViewDefault" placement, cascaded +24px per already-open read view, clamped to the
/// virtual screen via the existing ScreenClamp. WPF-free and unit-testable; the window
/// supplies the virtual-screen metrics.</summary>
public static class ReadViewPlacement
{
    public const double CascadeOffsetPx = 24;

    public static (double X, double Y, double? Width, double? Height) Next(
        WindowPlacement? saved, int alreadyOpenCount, double windowWidth, double windowHeight,
        double vx, double vy, double vw, double vh)
    {
        double baseX, baseY;
        if (saved is null)
            (baseX, baseY) = ScreenClamp.Clamp(double.NaN, double.NaN,
                windowWidth, windowHeight, vx, vy, vw, vh);          // fallback: top-right with margin
        else
            (baseX, baseY) = (saved.X, saved.Y);

        double offset = CascadeOffsetPx * alreadyOpenCount;
        var (x, y) = ScreenClamp.Clamp(baseX + offset, baseY + offset,
            windowWidth, windowHeight, vx, vy, vw, vh);
        return (x, y, saved?.Width, saved?.Height);
    }
}
