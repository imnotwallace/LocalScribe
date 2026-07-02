using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
namespace LocalScribe.App.ViewModels;

/// <summary>Placeholder for Task 2 (TDD, replaced there). Exists only so CompositionRoot's
/// controller/settings can be threaded through App's tray-first bootstrap and so the app
/// compiles/runs to a killable tray icon in this task.</summary>
public sealed class SessionViewModel
{
    public SessionViewModel(SessionController controller, Settings settings, Action<Action> dispatch)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(dispatch);
    }
}
