using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace LocalScribe.App;

/// <summary>Shared visual-tree scroll utilities (UX round 2026-08-02 items 1+2). Hoisted from the
/// two verbatim private FindScrollViewer copies in ReadViewWindow/LiveViewWindow so the read
/// view's anchor helpers (item 2) and the transport plan's sync-follow (items 7-9) build on ONE
/// lookup instead of a third copy.</summary>
public static class ScrollHelpers
{
    /// <summary>The first ScrollViewer beneath root (a ListView's template scroll host), or null
    /// before the control's template has been applied.</summary>
    public static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }
}
