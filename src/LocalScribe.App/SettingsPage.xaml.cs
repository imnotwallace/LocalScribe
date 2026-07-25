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
        // Same pattern, same reason, for the Voiceprints list (Task 13): voiceprints are ENROLLED
        // on the Split-speakers dialog, so a list read once at startup would show pre-enrollment
        // counts on the one screen whose job is to say what is stored. RefreshPeopleAsync reports
        // its own failures, so fire-and-forget is safe.
        Loaded += (_, _) => { vm.RefreshAssistantHelperNote(); _ = vm.RefreshPeopleAsync(); };
    }
}
