// tests/LocalScribe.Core.Tests/ClockTests.cs
using LocalScribe.Core.Audio;
using Xunit;

public class ClockTests
{
    [Fact]
    public void FakeClock_returns_configured_value_and_is_settable()
    {
        var clock = new FakeClock();
        Assert.Equal(0, clock.ElapsedMs);
        clock.ElapsedMs = 1500;
        Assert.Equal(1500, clock.ElapsedMs);
    }
}
