using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class TrayTextParserTests
{
    // Captured 2026-07-11 during a real Webex call (uia-dump-20260711-091553/091613/091641.txt).
    private const string MutedFull = "Microphone Muted: Webex\nTo toggle mute button, press Win+Alt+K.\n\nApps using your microphone:\nWebex";
    private const string LiveFull = "Microphone Unmuted: Webex\nTo toggle mute button, press Win+Alt+K.\n\nApps using your microphone:\nWebex";

    [Theory]
    [InlineData(MutedFull, AppMuteState.Muted, "Webex")]
    [InlineData(LiveFull, AppMuteState.Live, "Webex")]
    // First line alone must also parse (the flyout body below it is not load-bearing):
    [InlineData("Microphone Muted: Webex", AppMuteState.Muted, "Webex")]
    [InlineData("Microphone Unmuted: Webex", AppMuteState.Live, "Webex")]
    // CRLF tolerance (UIA may deliver \r\n):
    [InlineData("Microphone Muted: Webex\r\nTo toggle mute button, press Win+Alt+K.", AppMuteState.Muted, "Webex")]
    // A different integrated app must flow through as its own name:
    [InlineData("Microphone Muted: Teams", AppMuteState.Muted, "Teams")]
    // Robustness:
    [InlineData("", AppMuteState.Unknown, null)]
    [InlineData(null, AppMuteState.Unknown, null)]
    [InlineData("Volume: 43%", AppMuteState.Unknown, null)]
    [InlineData("Steam - synchronizing", AppMuteState.Unknown, null)]
    [InlineData("Privacy Location in use by:\nWebex", AppMuteState.Unknown, null)]
    public void Parses_tray_icon_names(string? name, AppMuteState state, string? app)
    {
        var r = TrayTextParser.Parse(name);
        Assert.Equal(state, r.State);
        Assert.Equal(app, r.AppName);
    }
}
