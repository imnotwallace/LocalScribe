using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

/// <summary>The three consumers that read transcript.jsonl LINES rather than projected DisplayRows
/// must never see a human-authored line: it carries no voice evidence to cluster or enrol from.
/// This is free while insertions stay out of transcript.jsonl - and this file is what fails if
/// someone later "helpfully" merges them in.</summary>
public sealed class InsertedLineIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 8, 11, 9, 0, 0, TimeSpan.Zero));
    public InsertedLineIsolationTests() => _paths = new StoragePaths(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static readonly DateTimeOffset Started = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A finalized session with session.json + meta.json but ZERO machine transcript
    /// content, plus ONE human-authored line.</summary>
    private async Task<string> SessionWithInsertionAsync(string text, long startMs)
    {
        const string id = "s-ins";
        await TestSessionSeeder.WriteInsertionOnlySession(_paths, id, "Insertion session", null,
            Started, "Webex", minDurationMs: startMs + 60_000);
        await TestSessionSeeder.InsertLineAsync(_paths, _time, id, text, startMs);
        return id;
    }

    /// <summary>A finalized session with `segments` machine segments (seq 0..segments-1) followed
    /// by `markers` marker lines, and session.json's SegmentCount/MarkerCount set to match - the
    /// baseline The_jsonl_line_count_is_unchanged_by_an_insertion needs before it can prove an
    /// insertion leaves both untouched.</summary>
    private async Task<string> SessionWithSegmentsAsync(int segments, int markers)
    {
        const string id = "s-mix";
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = Started,
            EndedAtUtc = Started.AddMinutes(10), DurationMs = 600_000,
            SegmentCount = segments, MarkerCount = markers,
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta { Title = "Mixed" }, default);

        var t = new TranscriptStore(_paths.TranscriptJsonl(id));
        int seq = 0;
        for (int i = 0; i < segments; i++)
            await t.AppendAsync(TranscriptLine.Segment(seq++, TranscriptSource.Local,
                i * 1000, (i + 1) * 1000, $"segment {i}", "Me"), default);
        for (int i = 0; i < markers; i++)
            await t.AppendAsync(TranscriptLine.Marker(seq++, (segments + i) * 1000,
                Markers.AudioDeviceChanged), default);
        return id;
    }

    private Task InsertLineAsync(string sessionId, string text, long startMs)
        => TestSessionSeeder.InsertLineAsync(_paths, _time, sessionId, text, startMs);

    [Fact]
    public async Task An_insertion_never_reaches_transcript_jsonl()
    {
        string sessionId = await SessionWithInsertionAsync("Off-mic answer.", startMs: 50_000);
        var lines = await new TranscriptStore(_paths.TranscriptJsonl(sessionId)).ReadAllAsync(default);

        Assert.DoesNotContain(lines, l => l.Text == "Off-mic answer.");
        Assert.All(lines, l => Assert.True(
            l.Kind is TranscriptKind.Segment or TranscriptKind.Marker));
    }

    [Fact]
    public async Task The_jsonl_line_count_is_unchanged_by_an_insertion()
    {
        // SegmentCount + MarkerCount arithmetic lives in SIX places; an overlay keeps all six true.
        string sessionId = await SessionWithSegmentsAsync(segments: 3, markers: 1);
        var before = await new TranscriptStore(_paths.TranscriptJsonl(sessionId)).ReadAllAsync(default);
        await InsertLineAsync(sessionId, "Off-mic answer.", 50_000);
        var after = await new TranscriptStore(_paths.TranscriptJsonl(sessionId)).ReadAllAsync(default);

        Assert.Equal(before.Count, after.Count);
        var session = await new SessionStore(_paths.SessionJson(sessionId)).ReadAsync(default);
        Assert.Equal(after.Count, session!.SegmentCount + session.MarkerCount);
    }
}
