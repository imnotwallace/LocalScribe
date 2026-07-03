using LocalScribe.App.ViewModels;

namespace LocalScribe.App;

/// <summary>Humble shell: shown modally from App.OnStartup BEFORE the tray exists (design 6.3).
/// ShowDialog() returns true only on Accept; Decline AND closing via the title bar both read as
/// not-accepted (DialogResult stays false/null), so the App shuts down - a dismissed notice is
/// never treated as consent.</summary>
public partial class ConsentDialog
{
    public ConsentDialog(ConsentViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.Closed += accepted =>
        {
            DialogResult = accepted;
            Close();
        };
    }
}
