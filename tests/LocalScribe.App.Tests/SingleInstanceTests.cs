using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SingleInstanceTests
{
    private static string UniqueName() => "ls-si-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void First_acquire_succeeds_and_second_returns_null()
    {
        string name = UniqueName();
        using var first = SingleInstance.TryAcquire(name, () => { });
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryAcquire(name, () => { }));
    }

    [Fact]
    public void SignalExisting_fires_the_holders_callback()
    {
        string name = UniqueName();
        int fired = 0;
        using var first = SingleInstance.TryAcquire(name, () => Interlocked.Increment(ref fired));
        Assert.NotNull(first);

        Assert.True(SingleInstance.SignalExisting(name));
        // Observable effect, never Thread.Sleep: the callback runs on the guard's wait thread.
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref fired) >= 1, TimeSpan.FromSeconds(5)),
            "activate callback did not fire within 5s");
    }

    [Fact]
    public void SignalExisting_returns_false_when_no_instance_holds_the_name()
    {
        Assert.False(SingleInstance.SignalExisting(UniqueName()));
    }

    [Fact]
    public void Dispose_releases_the_name_so_reacquire_succeeds()
    {
        string name = UniqueName();
        var first = SingleInstance.TryAcquire(name, () => { });
        Assert.NotNull(first);
        first!.Dispose();

        using var second = SingleInstance.TryAcquire(name, () => { });
        Assert.NotNull(second);
    }
}
