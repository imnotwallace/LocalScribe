// tests/LocalScribe.Core.Tests/SessionWriterTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class SessionWriterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 14, 32, 0, TimeSpan.Zero);

    private static async Task SeedAsync(StoragePaths paths, string id, DateTimeOffset? endedAtUtc)
    {
        Directory.CreateDirectory(paths.SessionDir(id));
        await new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = endedAtUtc,
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
            DurationMs = endedAtUtc is null ? 0 : 60000, Model = "small.en", Backend = "CUDA",
            Sources = new[] { SourceKind.Local, SourceKind.Remote },
        }, default);
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Doe intake", Medium = Medium.Webex, LocalCount = 1, RemoteCount = 1 }, default);
        var t = new TranscriptStore(paths.TranscriptJsonl(id));
        await t.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Hello there.", "Me"), default);
        await t.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Remote, 1000, 2000, "Hi.", "Them"), default);
    }

    [Fact]
    public async Task Regenerate_writes_the_three_readable_projections()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: T0.AddMinutes(1));
            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
            await writer.RegenerateProjectionsAsync("s1", default);

            Assert.True(File.Exists(paths.TranscriptMd("s1")));
            Assert.True(File.Exists(paths.TranscriptTxt("s1")));
            Assert.True(File.Exists(paths.SessionTxt("s1")));

            string md = await File.ReadAllTextAsync(paths.TranscriptMd("s1"));
            Assert.Contains("# Doe intake", md);
            Assert.Contains("Hello there.", md);
            // Local time from the STORED offset (14:32Z + 480 min), not the machine's zone.
            Assert.Contains("2026-07-02 22:32", md);
            Assert.False(File.Exists(paths.TranscriptMd("s1") + ".tmp"));   // atomic write cleaned up

            string sessionTxt = await File.ReadAllTextAsync(paths.SessionTxt("s1"));
            Assert.Contains("Doe intake", sessionTxt);
            Assert.Contains("Medium: Webex", sessionTxt);

            Assert.False(File.Exists(paths.SummaryMd("s1")));   // reserved, never generated
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Recovery_finalizes_marks_and_appends_marker()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: null);        // crashed: no endedAt
            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));

            Assert.True(await writer.RecoverIfNeededAsync("s1", default));

            var session = await new SessionStore(paths.SessionJson("s1")).ReadAsync(default);
            Assert.True(session!.Recovered);
            Assert.Equal(T0.AddMilliseconds(2000), session.EndedAtUtc);   // last segment endMs
            Assert.Equal(2000, session.DurationMs);
            Assert.Equal(1, session.MarkerCount);
            Assert.Equal(2, session.SegmentCount);

            var lines = await new TranscriptStore(paths.TranscriptJsonl("s1")).ReadAllAsync(default);
            Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.RecoveredSession);
            Assert.True(File.Exists(paths.TranscriptMd("s1")));           // regenerated

            Assert.False(await writer.RecoverIfNeededAsync("s1", default)); // idempotent
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Recovery_noop_on_already_finalized()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: T0.AddMinutes(1));
            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
            Assert.False(await writer.RecoverIfNeededAsync("s1", default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SessionTxt_date_line_uses_stored_offset_for_both_endpoints()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            // Start 14:32Z, end 14:33Z, stored offset +480 (Singapore). Both endpoints must render
            // via the STORED offset -> 22:32 - 22:33, deterministically on ANY machine zone. Before the
            // fix the end used the machine's zone (e.g. 14:33 on a UTC box), reading earlier than start.
            await SeedAsync(paths, "s1", endedAtUtc: T0.AddMinutes(1));
            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
            await writer.RegenerateProjectionsAsync("s1", default);

            string sessionTxt = await File.ReadAllTextAsync(paths.SessionTxt("s1"));
            Assert.Contains("Date: 2026-07-02 22:32 - 22:33 (1 min)", sessionTxt);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Regenerate_hides_phantom_bleed_echo_in_md_but_jsonl_keeps_both()
    {
        // Remote says it loud; the mic hears the speakers say the SAME text quieter and later
        // within the near-window: classic phantom bleed (design: speakers-instead-of-headphones).
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            // Create session folder and metadata (minimal seeding, no pre-transcribed lines).
            Directory.CreateDirectory(paths.SessionDir("s1"));
            await new SessionStore(paths.SessionJson("s1")).SaveAsync(new SessionRecord
            {
                Id = "s1", App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = T0.AddMinutes(1),
                TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
                DurationMs = 60000, Model = "small.en", Backend = "CUDA",
                Sources = new[] { SourceKind.Local, SourceKind.Remote },
            }, default);
            await new MetadataStore(paths.MetaJson("s1")).SaveAsync(
                new SessionMeta { Title = "Phantom test", Medium = Medium.Webex, LocalCount = 1, RemoteCount = 1 }, default);

            var store = new TranscriptStore(paths.TranscriptJsonl("s1"));
            await store.AppendAsync(TranscriptLine.Segment(
                seq: 0, TranscriptSource.Remote,
                startMs: 1000, endMs: 3000, "I pushed the auth changes last night.", "Them",
                lang: "en", noSpeechProb: 0.01, rmsDb: -20.0), CancellationToken.None);
            await store.AppendAsync(TranscriptLine.Segment(
                seq: 1, TranscriptSource.Local,
                startMs: 1200, endMs: 3100, "I pushed the auth changes last night.", "Me",
                lang: "en", noSpeechProb: 0.01, rmsDb: -31.0), CancellationToken.None);

            await new SessionWriter(paths, new Settings(), TimeProvider.System)
                .RegenerateProjectionsAsync("s1", CancellationToken.None);

            string md = await File.ReadAllTextAsync(paths.TranscriptMd("s1"));
            Assert.Single(SplitOccurrences(md, "I pushed the auth changes last night."));
            Assert.Contains("Them:", md);                            // the louder Remote line survives
            Assert.DoesNotContain("Me:", md);                        // the bleed echo is hidden

            var lines = await store.ReadAllAsync(CancellationToken.None);
            Assert.Equal(2, lines.Count);                            // JSONL keeps both (spec 1.1)
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static string[] SplitOccurrences(string haystack, string needle)
        => haystack.Split(needle).Skip(1).Select(_ => needle).ToArray();
}
