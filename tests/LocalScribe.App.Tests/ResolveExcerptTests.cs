using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Excerpt range parsing and validation (design 2026-08-04 section 8). Lives in the
/// service, not the view model, because only the service has the session's local start (wallclock
/// mode) and its duration (bounds).</summary>
public sealed class ResolveExcerptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-exc-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    /// <summary>A 30-minute session starting 09:00 local (UTC), with turns only in the first 24 s.</summary>
    private async Task<MaintenanceService> MakeAsync(string timestampsMode = "relative")
    {
        var paths = new StoragePaths(_root);
        var settings = new FakeSettingsService(new Settings { Timestamps = timestampsMode });
        var svc = new MaintenanceService(paths, settings, new FakeRecycleBin(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 3, 6, 0, 0, TimeSpan.Zero)));
        Directory.CreateDirectory(paths.SessionDir("s1"));
        await new SessionStore(paths.SessionJson("s1")).SaveAsync(new SessionRecord
        {
            Id = "s1", App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 3, 9, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, default);
        await new MetadataStore(paths.MetaJson("s1")).SaveAsync(new SessionMeta { Title = "Doe intake" }, default);

        long[][] times = [[0, 4000], [4400, 9000], [9400, 14000], [14400, 19000], [19400, 24000]];
        string[] words = ["one", "two", "three", "four", "five"];
        var store = new TranscriptStore(paths.TranscriptJsonl("s1"));
        for (int i = 0; i < words.Length; i++)
            await store.AppendAsync(TranscriptLine.Segment(i, TranscriptSource.Local,
                times[i][0], times[i][1], words[i], "Me"), default);
        return svc;
    }

    [Fact]
    public async Task Parses_relative_stamps()
    {
        var svc = await MakeAsync();
        var range = await svc.ResolveExcerptAsync("s1", "00:05", "00:15", default);
        Assert.Equal(5000, range.FromMs);
        Assert.Equal(15000, range.ToMs);
    }

    [Fact]
    public async Task Empty_from_means_start_and_empty_to_means_end()
    {
        var svc = await MakeAsync();
        var range = await svc.ResolveExcerptAsync("s1", "", "", default);
        Assert.Equal(0, range.FromMs);
        Assert.Equal(1_800_000, range.ToMs);
    }

    [Fact]
    public async Task Wallclock_mode_parses_against_the_sessions_own_local_start()
    {
        // The session starts 09:00; 09:00:10 is 10 s in, NOT 9 hours (design 2026-08-04 section 8).
        var svc = await MakeAsync(timestampsMode: "wallclock");
        var range = await svc.ResolveExcerptAsync("s1", "09:00:05", "09:00:20", default);
        Assert.Equal(5000, range.FromMs);
        Assert.Equal(20000, range.ToMs);
    }

    [Fact]
    public async Task Rejects_unparseable_input()
    {
        var svc = await MakeAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResolveExcerptAsync("s1", "banana", "", default));
        Assert.Contains("banana", ex.Message);
    }

    [Fact]
    public async Task Rejects_a_backwards_range()
    {
        var svc = await MakeAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResolveExcerptAsync("s1", "00:15", "00:05", default));
        Assert.Contains("before its end", ex.Message);
    }

    [Fact]
    public async Task Rejects_a_range_past_the_recording()
    {
        var svc = await MakeAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResolveExcerptAsync("s1", "00:00", "99:00", default));
        Assert.Contains("outside the recording", ex.Message);
    }

    [Fact]
    public async Task Rejects_a_range_with_no_transcript_content()
    {
        // An empty document is never written; the user is told instead. Turns stop at 24 s.
        var svc = await MakeAsync();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResolveExcerptAsync("s1", "10:00", "11:00", default));
        Assert.Contains("no transcript content", ex.Message);
    }

    [Fact]
    public async Task Rejects_a_range_that_selects_only_a_marker()
    {
        // Fix 3 (whole-branch review): the zero-row safeguard above must count only non-marker
        // rows. A range containing only a marker (e.g. "[Recording paused]") must be refused the
        // same way an empty range is - otherwise, exported with "Include system markers"
        // unticked, it writes a banner-stamped document with ZERO content: precisely the empty
        // document this safeguard exists to prevent. Self-contained fixture (own session "s2"):
        // MakeAsync's shared "s1" turns stop at 24 s and this needs a marker with no turn
        // anywhere near it.
        var paths = new StoragePaths(_root);
        var settings = new FakeSettingsService(new Settings { Timestamps = "relative" });
        var svc = new MaintenanceService(paths, settings, new FakeRecycleBin(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 3, 6, 0, 0, TimeSpan.Zero)));
        Directory.CreateDirectory(paths.SessionDir("s2"));
        await new SessionStore(paths.SessionJson("s2")).SaveAsync(new SessionRecord
        {
            Id = "s2", App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 9, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 3, 9, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, default);
        await new MetadataStore(paths.MetaJson("s2")).SaveAsync(
            new SessionMeta { Title = "Marker only" }, default);
        var store = new TranscriptStore(paths.TranscriptJsonl("s2"));
        await store.AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 4000, "one", "Me"), default);
        await store.AppendAsync(TranscriptLine.Marker(1, 100000, "Recording paused"), default);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResolveExcerptAsync("s2", "01:35", "01:45", default));
        Assert.Contains("no transcript content", ex.Message);
    }
}
