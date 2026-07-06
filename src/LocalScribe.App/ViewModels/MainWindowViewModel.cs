using CommunityToolkit.Mvvm.ComponentModel;
using LocalScribe.App.Services;
namespace LocalScribe.App.ViewModels;

/// <summary>WPF-free state for the manager shell (design section 2): the selected nav section
/// (MainWindow mirrors NavigationView selection into it; page VMs read it) and the InfoBar
/// error queue. This VM is a singleton across MainWindow RE-CREATIONS - the window genuinely
/// closes and TrayIconHost builds a fresh one per open - so section choice and queued errors
/// survive a close/reopen. Also exposes the shared SessionViewModel so the shell can host the
/// nav-rail Record command and the status strip (Stage 5.4 section 6).</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    public InfoBarErrorReporter Errors { get; }
    public SessionViewModel Session { get; }

    [ObservableProperty]
    private string _selectedSection = "Sessions";

    public MainWindowViewModel(InfoBarErrorReporter errors, SessionViewModel session)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(session);
        (Errors, Session) = (errors, session);
    }
}
