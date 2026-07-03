namespace LocalScribe.App.Pages;

/// <summary>Empty shell hosted by MainWindow's NavigationView; content lands with the
/// Sessions-page task. Parameterless ctor is load-bearing: no page-provider service is
/// registered, so NavigationView's default activator constructs pages reflectively.</summary>
public partial class SessionsPage
{
    public SessionsPage() => InitializeComponent();
}
