using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class WindowRegistryTests
{
    [Fact]
    public void Register_makes_the_session_open_and_counts_it()
    {
        var reg = new WindowRegistry();
        Assert.False(reg.IsOpen("s1"));
        Assert.Equal(0, reg.OpenCount);

        reg.Register("s1", () => { });

        Assert.True(reg.IsOpen("s1"));
        Assert.Equal(1, reg.OpenCount);
    }

    [Fact]
    public void OpenCount_counts_distinct_sessions()
    {
        var reg = new WindowRegistry();
        reg.Register("s1", () => { });
        reg.Register("s2", () => { });
        Assert.Equal(2, reg.OpenCount);
    }

    [Fact]
    public void Register_same_session_twice_replaces_and_does_not_double_count()
    {
        var reg = new WindowRegistry();
        reg.Register("s1", () => { });
        reg.Register("s1", () => { });
        Assert.Equal(1, reg.OpenCount);
    }

    [Fact]
    public void Unregister_untracks_without_invoking_close()
    {
        var reg = new WindowRegistry();
        bool closed = false;
        reg.Register("s1", () => closed = true);

        reg.Unregister("s1");

        Assert.False(reg.IsOpen("s1"));
        Assert.False(closed);                 // window closed itself; close action must NOT re-fire
        Assert.Equal(0, reg.OpenCount);
    }

    [Fact]
    public void CloseAllFor_invokes_the_close_action_then_untracks()
    {
        var reg = new WindowRegistry();
        int closes = 0;
        reg.Register("s1", () => closes++);

        reg.CloseAllFor("s1");

        Assert.Equal(1, closes);
        Assert.False(reg.IsOpen("s1"));
        Assert.Equal(0, reg.OpenCount);
    }

    [Fact]
    public void CloseAllFor_is_reentrancy_safe_when_close_calls_Unregister()
    {
        // A real ReadViewWindow's Closing handler calls Unregister; CloseAllFor triggers that
        // close, so the tracked action re-enters the registry. It must not throw or double-remove.
        var reg = new WindowRegistry();
        reg.Register("s1", () => reg.Unregister("s1"));

        reg.CloseAllFor("s1");

        Assert.False(reg.IsOpen("s1"));
        Assert.Equal(0, reg.OpenCount);
    }

    [Fact]
    public void CloseAllFor_unknown_session_is_a_no_op()
    {
        var reg = new WindowRegistry();
        reg.CloseAllFor("missing");           // must not throw
        Assert.Equal(0, reg.OpenCount);
    }
}
