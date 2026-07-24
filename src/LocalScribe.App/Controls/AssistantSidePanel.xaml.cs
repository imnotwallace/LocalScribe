using System.Windows;
using System.Windows.Controls;
using LocalScribe.App.ViewModels;
namespace LocalScribe.App.Controls;

/// <summary>The reusable Assistant side panel (addendum 2026-07-25). Code-behind exists only for
/// the two things bindings cannot do: opening the overflow ContextMenu from a left-click, and the
/// rename box's Enter/Esc keys (KeyBindings outside the visual tree don't resolve - the
/// ReadViewWindow find-box precedent).</summary>
public partial class AssistantSidePanel : UserControl
{
    public AssistantSidePanel() => InitializeComponent();

    private AssistantSidePanelViewModel? Vm => DataContext as AssistantSidePanelViewModel;

    private void OnOverflowClick(object sender, RoutedEventArgs e)
    {
        if (OverflowButton.ContextMenu is not { } menu) return;
        menu.PlacementTarget = OverflowButton;
        menu.DataContext = DataContext;   // ContextMenu is not in the visual tree - inherit manually
        menu.IsOpen = true;
    }

    private void OnRenameBoxPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Vm is null) return;
        if (e.Key == System.Windows.Input.Key.Enter)
        { Vm.Threads.CommitRenameCommand.Execute(null); e.Handled = true; }
        else if (e.Key == System.Windows.Input.Key.Escape)
        { Vm.Threads.CancelRenameCommand.Execute(null); e.Handled = true; }
    }
}
