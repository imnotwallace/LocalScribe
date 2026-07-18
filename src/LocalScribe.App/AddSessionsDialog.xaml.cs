using System.Windows;
using LocalScribe.App.ViewModels;
namespace LocalScribe.App;

/// <summary>Modal multi-select session picker for the Matters page (design 2026-07-18
/// section 4). Humble shell: all list/filter/selection logic lives in
/// AddSessionsPickerViewModel; OK with nothing checked is a harmless no-op batch.</summary>
public partial class AddSessionsDialog
{
    public AddSessionsDialog(AddSessionsPickerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
