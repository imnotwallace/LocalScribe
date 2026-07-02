using System.IO;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class OverlayViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-ov-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private (OverlayViewModel Overlay, SessionViewModel Session) Make(Settings settings)
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, settings, a => a(),
            startOptions: LiveTestDoubles.Options());
        return (new OverlayViewModel(session, settings), session);
    }

    [Fact]
    public async Task Visible_only_while_recording_or_paused()
    {
        var (overlay, session) = Make(new Settings());
        Assert.False(overlay.IsVisible);                   // Idle
        await session.StartCommand.ExecuteAsync(null);
        Assert.True(overlay.IsVisible);                    // Recording
        await session.PauseResumeCommand.ExecuteAsync(null);
        Assert.True(overlay.IsVisible);                    // Paused (spec 2.1)
        await session.PauseResumeCommand.ExecuteAsync(null);
        await session.StopCommand.ExecuteAsync(null);
        Assert.False(overlay.IsVisible);                   // Idle again (Finalizing hides too)
    }

    [Fact]
    public async Task Disabled_overlay_never_shows()
    {
        var (overlay, session) = Make(new Settings
        { Overlay = new OverlaySetting { Enabled = false } });
        await session.StartCommand.ExecuteAsync(null);
        Assert.False(overlay.IsVisible);
        await session.StopCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Session_name_suppressed_by_default_opt_in_via_tooltip()
    {
        var (overlay, session) = Make(new Settings());     // ShowSessionName default false
        await session.StartCommand.ExecuteAsync(null);
        Assert.Null(overlay.TooltipText);                  // privileged matter never rendered
        await session.StopCommand.ExecuteAsync(null);

        var (overlay2, session2) = Make(new Settings
        { Overlay = new OverlaySetting { ShowSessionName = true } });
        await session2.StartCommand.ExecuteAsync(null);
        Assert.NotNull(overlay2.TooltipText);              // opt-in: tooltip only
        await session2.StopCommand.ExecuteAsync(null);
    }
}
