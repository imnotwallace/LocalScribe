using System.Windows;
using LocalScribe.App.ViewModels;
namespace LocalScribe.App;

/// <summary>Modal "Correct text..." dialog (Stage 6.1). Humble shell: seeding, diffing,
/// validation, and the gated save all live in CorrectTextViewModel; this closes with
/// DialogResult=true only when the VM reports the save landed (the caller then reloads rows).</summary>
public partial class CorrectTextDialog
{
    private readonly CorrectTextViewModel _vm;

    public CorrectTextDialog(CorrectTextViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_vm.IsSaving) return;
        if (await _vm.SaveAsync(CancellationToken.None)) DialogResult = true;
    }
}
