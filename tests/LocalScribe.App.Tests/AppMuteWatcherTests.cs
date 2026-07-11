using System.Collections.Generic;
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class AppMuteWatcherTests
{
    private sealed class FakeSource : IAppMuteSignalSource
    {
        public AppMuteReading Next = new(AppMuteState.Unknown, null);
        public int Reads;
        public AppMuteReading Read() { Reads++; return Next; }
    }

    [Fact]
    public void Polls_only_while_recording()
    {
        var src = new FakeSource();
        bool recording = false;
        var w = new AppMuteWatcher(src, () => recording);
        w.Poll();
        Assert.Equal(0, src.Reads);                             // not recording: no UIA touch at all
        recording = true;
        w.Poll();
        Assert.Equal(1, src.Reads);
    }

    [Fact]
    public void Raises_only_on_change_and_resets_to_unknown_when_not_recording()
    {
        var src = new FakeSource();
        bool recording = true;
        var w = new AppMuteWatcher(src, () => recording);
        var events = new List<AppMuteReading>();
        w.ReadingChanged += events.Add;

        src.Next = new(AppMuteState.Muted, "Webex");
        w.Poll(); w.Poll();                                     // second poll: same value, no event
        Assert.Single(events);

        recording = false;
        w.Poll();                                               // leaving Recording resets to Unknown
        Assert.Equal(2, events.Count);
        Assert.Equal(AppMuteState.Unknown, events[^1].State);
    }
}
