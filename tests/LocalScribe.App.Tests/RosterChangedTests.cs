// tests/LocalScribe.App.Tests/RosterChangedTests.cs
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Task 17 live roster sync: WindowRegistry.RosterChanged is the notification home
/// Session Details' save path fires and ReadViewWindow subscribes to (design section 4).</summary>
public sealed class RosterChangedTests
{
    [Fact]
    public void NotifyRosterChanged_RaisesEventWithSessionId()
    {
        var reg = new WindowRegistry();
        string? got = null;
        reg.RosterChanged += id => got = id;

        reg.NotifyRosterChanged("s-1");

        Assert.Equal("s-1", got);
    }

    [Fact]
    public void NotifyRosterChanged_with_no_subscribers_does_not_throw()
    {
        var reg = new WindowRegistry();
        reg.NotifyRosterChanged("s-1");   // must not throw (RosterChanged is null)
    }
}
