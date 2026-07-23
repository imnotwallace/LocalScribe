namespace LocalScribe.App;

/// <summary>Humble shell for the Settings page - pure XAML assembly over the tested
/// SettingsPageViewModel. Hosted by MainWindow's NavigationView.</summary>
public partial class SettingsPage
{
    public SettingsPage(ViewModels.SettingsPageViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // Design 3.1 "page navigation refresh" pattern (Sessions/Matters/Search): Loaded fires
        // on every re-navigation into Settings. RefreshAssistantHelperNote() is a cheap
        // File.Exists chain, so re-running it here picks up a helper folder deployed after
        // startup without an app restart - keeping this note truthful with the Assistant tab
        // and assistant chat, which both re-probe on every use (Task 5 review finding).
        Loaded += (_, _) => vm.RefreshAssistantHelperNote();
    }
}
