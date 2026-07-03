using System.Collections.ObjectModel;
namespace LocalScribe.App.Services;

/// <summary>IUiErrorReporter surfacing into MainWindow's InfoBar (design 7.5). WPF-free: the
/// queue is plain ObservableCollection state; Report/Info may be called from any thread and
/// marshal through the injected dispatch (the UI thread in the app, an inline runner in
/// tests). MainWindow mirrors Messages[0] into the InfoBar and calls DismissOldest when the
/// user closes it; the collection outlives any single MainWindow instance, so errors queued
/// while the window is closed appear on next open.</summary>
public sealed class InfoBarErrorReporter(Action<Action> dispatch) : IUiErrorReporter
{
    public ObservableCollection<string> Messages { get; } = [];

    public void Report(string context, Exception ex)
        => dispatch(() => Messages.Add(context + ": " + ex.Message));

    public void Info(string message) => dispatch(() => Messages.Add(message));

    public void DismissOldest()
    {
        if (Messages.Count > 0) Messages.RemoveAt(0);
    }
}
