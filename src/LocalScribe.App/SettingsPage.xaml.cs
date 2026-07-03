namespace LocalScribe.App;

/// <summary>Humble shell for the Settings page - pure XAML assembly over the tested
/// SettingsPageViewModel. Hosted by MainWindow's NavigationView.</summary>
public partial class SettingsPage
{
    public SettingsPage(ViewModels.SettingsPageViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
