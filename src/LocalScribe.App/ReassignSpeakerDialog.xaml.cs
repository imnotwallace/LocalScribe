using System.Windows;
using LocalScribe.App.ViewModels;
namespace LocalScribe.App;

/// <summary>Modal "Reassign speaker..." dialog (Stage 6.1). Humble shell over
/// ReassignSpeakerViewModel; the no-candidates state hands off to Session Details (the one
/// identity-creation flow) and closes without saving.</summary>
public partial class ReassignSpeakerDialog
{
    private readonly ReassignSpeakerViewModel _vm;

    public ReassignSpeakerDialog(ReassignSpeakerViewModel vm)
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

    private void OnOpenSessionDetails(object sender, RoutedEventArgs e)
    {
        _vm.RequestOpenSessionDetails();
        Close();
    }
}
