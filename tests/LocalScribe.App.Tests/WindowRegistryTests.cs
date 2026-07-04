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
    public void Register_same_session_twice_appends_without_evicting_the_first()
    {
        // A ReadViewWindow and a SplitSpeakersWindow can both be open for the same session id
        // at once (Task 9 review fix) - the second Register must not evict the first's close
        // action. OpenCount still counts the session once (it is keyed by id, not by window).
        var reg = new WindowRegistry();
        int aClosed = 0, bClosed = 0;
        reg.Register("s1", () => aClosed++);
        reg.Register("s1", () => bClosed++);
        Assert.Equal(1, reg.OpenCount);

        reg.CloseAllFor("s1");

        Assert.Equal(1, aClosed);
        Assert.Equal(1, bClosed);
    }

    [Fact]
    public void Unregister_untracks_without_invoking_close()
    {
        var reg = new WindowRegistry();
        bool closed = false;
        Action close = () => closed = true;
        reg.Register("s1", close);

        reg.Unregister("s1", close);

        Assert.False(reg.IsOpen("s1"));
        Assert.False(closed);                 // window closed itself; close action must NOT re-fire
        Assert.Equal(0, reg.OpenCount);
    }

    [Fact]
    public void Unregister_removes_only_the_specified_window_leaving_the_other_open()
    {
        // Proves the multi-window-per-id extension: closing (say) the Split-speakers dialog
        // normally must not deregister a still-open ReadViewWindow for the same session, and
        // vice versa - each window's own close action is removed by reference identity.
        var reg = new WindowRegistry();
        int aClosed = 0, bClosed = 0;
        Action closeA = () => aClosed++;
        Action closeB = () => bClosed++;
        reg.Register("s1", closeA);
        reg.Register("s1", closeB);

        reg.Unregister("s1", closeA);

        Assert.True(reg.IsOpen("s1"));        // the other window is still tracked
        Assert.Equal(1, reg.OpenCount);

        reg.CloseAllFor("s1");

        Assert.Equal(0, aClosed);              // already unregistered - must not fire
        Assert.Equal(1, bClosed);
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
    public void CloseAllFor_closes_every_window_registered_for_the_same_session()
    {
        // The evidentiary-delete scenario this whole fix exists for: a read view AND a Split-
        // speakers dialog both open for session X. Deleting X must close BOTH before the recycle
        // so neither's audio file handle blocks ShellRecycleBin.
        var reg = new WindowRegistry();
        int readViewClosed = 0, splitClosed = 0;
        reg.Register("s1", () => readViewClosed++);
        reg.Register("s1", () => splitClosed++);

        reg.CloseAllFor("s1");

        Assert.Equal(1, readViewClosed);
        Assert.Equal(1, splitClosed);
        Assert.False(reg.IsOpen("s1"));
        Assert.Equal(0, reg.OpenCount);
    }

    [Fact]
    public void CloseAllFor_is_reentrancy_safe_when_close_calls_Unregister()
    {
        // A real ReadViewWindow's Closed handler calls Unregister; CloseAllFor triggers that
        // close, so the tracked action re-enters the registry. It must not throw or double-remove.
        var reg = new WindowRegistry();
        Action close = null!;
        close = () => reg.Unregister("s1", close);
        reg.Register("s1", close);

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
