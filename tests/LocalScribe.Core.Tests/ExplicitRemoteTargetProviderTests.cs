using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using Xunit;

namespace LocalScribe.Core.Tests;

/// <summary>Design 2026-07-12 section "Architecture 2": the live swap must build a source for a
/// SPECIFIC requested target, not whatever ambient settings resolve to. FakeProvider's explicit
/// overload resolves the snapshot through the real planner so controller/marker tests are honest.</summary>
public sealed class ExplicitRemoteTargetProviderTests
{
    [Fact]
    public void Explicit_per_app_resolves_that_app_through_the_planner()
    {
        var p = new FakeProvider
        { ActiveSessions = { } };
        p.ActiveSessions.Add(new AudioSessionInfo(5151, "Zoom"));
        var (src, snap) = p.CreateRemote(new FakeClock(),
            new RemoteSetting { Mode = RemoteMode.PerProcess, App = "Zoom" });
        Assert.NotNull(src);
        Assert.Equal(RemoteMode.PerProcess, snap.Mode);
        Assert.Equal("Zoom", snap.App);
        Assert.False(snap.FellBackToSystemMix);
        Assert.Equal(1, p.RemoteCreates);
    }

    [Fact]
    public void Explicit_system_mix_resolves_system_mix_not_a_fallback()
    {
        var p = new FakeProvider();
        var (_, snap) = p.CreateRemote(new FakeClock(), new RemoteSetting { Mode = RemoteMode.SystemMix });
        Assert.Equal(RemoteMode.SystemMix, snap.Mode);
        Assert.False(snap.FellBackToSystemMix);
    }

    [Fact]
    public void Explicit_overload_can_be_forced_to_throw()
    {
        var p = new FakeProvider { ThrowOnNextRemoteCreate = true };
        Assert.Throws<System.InvalidOperationException>(
            () => p.CreateRemote(new FakeClock(), new RemoteSetting { Mode = RemoteMode.SystemMix }));
    }
}
