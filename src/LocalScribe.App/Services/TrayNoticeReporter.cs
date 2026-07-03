namespace LocalScribe.App.Services;

/// <summary>IUiErrorReporter for startup/background work (design 7.5: background operations
/// surface via tray balloon, not an InfoBar). WPF-free: App injects a dispatcher-marshaled
/// TrayIconHost.ShowNotice hook as the notify sink.</summary>
public sealed class TrayNoticeReporter(Action<string> notify) : IUiErrorReporter
{
    public void Report(string context, Exception ex) => notify(context + ": " + ex.Message);
    public void Info(string message) => notify(message);
}
