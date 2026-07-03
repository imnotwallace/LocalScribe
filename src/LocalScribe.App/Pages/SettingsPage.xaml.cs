namespace LocalScribe.App.Pages;

/// <summary>Navigation host for the Settings section. Task 21 built the real Settings UI as
/// the LocalScribe.App.SettingsPage UserControl (deliberate name reuse in a different
/// namespace); this page stays the type MainWindow.xaml's TargetPageType names - so the
/// provider-returned instance type always matches the requested page type - and simply hosts
/// the UserControl as its Content. The parameterless shell ctor is gone along with the
/// default activator that needed it: StaticPageProvider constructs this page.</summary>
public partial class SettingsPage
{
    public SettingsPage(ViewModels.SettingsPageViewModel vm)
    {
        InitializeComponent();
        Content = new LocalScribe.App.SettingsPage(vm);
    }
}
