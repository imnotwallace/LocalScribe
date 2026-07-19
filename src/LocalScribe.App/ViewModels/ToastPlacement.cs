namespace LocalScribe.App.ViewModels;

/// <summary>Pure decision logic for AdvisoryToastWindow (design 2026-07-18 sections 4/5.3), split
/// out because WPF windows are not unit-testable headlessly (the ScreenClamp precedent): WHERE the
/// toast goes (bottom-right of the primary work area, clamped on-screen) and WHETHER it auto-
/// dismisses. The window itself is a humble shell over these two functions.</summary>
public static class ToastPlacement
{
    /// <summary>Bottom-right corner of the given work area with a margin, clamped to the work-area
    /// origin so an oversized toast can never land off-screen. Callers pass
    /// SystemParameters.WorkArea (the PRIMARY work area - taskbar already excluded).</summary>
    public static (double Left, double Top) BottomRight(double workLeft, double workTop,
        double workWidth, double workHeight, double toastWidth, double toastHeight,
        double margin = 16)
        => (Math.Max(workLeft, workLeft + workWidth - toastWidth - margin),
            Math.Max(workTop, workTop + workHeight - toastHeight - margin));

    /// <summary>Auto-dismiss decision: positive seconds -> the timer interval; zero/negative ->
    /// null (sticky toast, no timer). For the stop-confirm toast, dismissal without a click means
    /// "keep recording" - the safe default (evidentiary rule, design section 4).</summary>
    public static TimeSpan? DismissInterval(int autoDismissSeconds)
        => autoDismissSeconds > 0 ? TimeSpan.FromSeconds(autoDismissSeconds) : null;
}
