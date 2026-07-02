namespace LocalScribe.App.ViewModels;

/// <summary>Keeps the overlay pill inside the virtual screen (design decision 12: remembered
/// position clamped on load - a monitor may have been unplugged since last run).</summary>
public static class ScreenClamp
{
    public static (double X, double Y) Clamp(double x, double y, double w, double h,
        double vx, double vy, double vw, double vh)
    {
        if (double.IsNaN(x) || double.IsNaN(y))
            return (vx + vw - w - 16, vy + 16);            // fallback: top-right with margin
        return (Math.Clamp(x, vx, Math.Max(vx, vx + vw - w)),
                Math.Clamp(y, vy, Math.Max(vy, vy + vh - h)));
    }
}
