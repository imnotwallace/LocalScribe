using System.Windows;
using LocalScribe.App.ViewModels;
namespace LocalScribe.App;

/// <summary>Modal "Add line here..." dialog (design 2026-08-11 section 2.3). Humble shell: seeding,
/// timestamp parsing, validation and the gated save all live in InsertLineViewModel; this closes
/// with DialogResult=true only when the VM reports the write landed (the caller then reloads rows).</summary>
public partial class InsertLineDialog
{
    private readonly InsertLineViewModel _vm;

    public InsertLineDialog(InsertLineViewModel vm)
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
