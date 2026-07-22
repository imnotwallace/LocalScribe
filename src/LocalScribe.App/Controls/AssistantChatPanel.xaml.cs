using System.Windows.Controls;
namespace LocalScribe.App.Controls;

/// <summary>Shared chat surface (design 2026-07-18 section 7.6): Session Details' Assistant tab
/// and the Matters Assistant tab both host this over an AssistantChatViewModel DataContext.
/// The BindingProxy carries the VM's NavigateChipCommand into the chip-row templates (the
/// read-view context-menu precedent - a row template cannot walk to the VM's command).</summary>
public partial class AssistantChatPanel : UserControl
{
    public AssistantChatPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ((BindingProxy)Resources["ChatProxy"]).Data = DataContext;
    }
}
