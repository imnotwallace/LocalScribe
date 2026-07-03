using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
namespace LocalScribe.App.ViewModels;

/// <summary>WPF-free state for the manager shell (design section 2): the selected nav section
/// (MainWindow mirrors NavigationView selection into it; page VMs read it) and the InfoBar
/// error queue. This VM is a singleton across MainWindow RE-CREATIONS - the window genuinely
/// closes and TrayIconHost builds a fresh one per open - so section choice and queued errors
/// survive a close/reopen.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public InfoBarErrorReporter Errors { get; }

    [ObservableProperty]
    private string _selectedSection = "Sessions";

    public MainWindowViewModel(InfoBarErrorReporter errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = errors;
    }
}
