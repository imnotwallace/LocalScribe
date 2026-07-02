using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.Core.Tests;

/// <summary>Pure mapping table for the Stage 4 AppKind derivation (design 7.4). Images are the
/// extensionless process names the planner sees (AudioSessionInfo), matched case-insensitively
/// by containment - the same matching style RemoteCapturePlanner itself uses.</summary>
public sealed class AppKindResolverTests
{
    [Theory]
    [InlineData("CiscoCollabHost", AppKind.Webex)]     // Stage-1 finding: Webex renders here
    [InlineData("Webex", AppKind.Webex)]
    [InlineData("webex", AppKind.Webex)]               // case-insensitive
    [InlineData("CiscoCollabHost.exe", AppKind.Webex)] // containment tolerates a stray extension
    [InlineData("Zoom", AppKind.Zoom)]
    [InlineData("ZOOM", AppKind.Zoom)]
    [InlineData("ms-teams", AppKind.Teams)]
    [InlineData("Teams", AppKind.Teams)]
    [InlineData("msedgewebview2", AppKind.Browser)]    // Teams' webview counts as Browser (locked)
    [InlineData("chrome", AppKind.Browser)]
    [InlineData("msedge", AppKind.Browser)]
    [InlineData("firefox", AppKind.Browser)]
    [InlineData("brave", AppKind.Browser)]
    [InlineData("opera", AppKind.Browser)]
    [InlineData("Spotify", AppKind.Manual)]            // unknown image -> Manual
    [InlineData("", AppKind.Manual)]
    [InlineData(null, AppKind.Manual)]
    public void FromProcessImage_maps_known_images(string? image, AppKind expected)
        => Assert.Equal(expected, AppKindResolver.FromProcessImage(image));
}
