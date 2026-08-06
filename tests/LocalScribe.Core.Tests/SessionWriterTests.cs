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

    /// <summary>Writes a real, header-valid retained leg of <paramref name="ms"/> silence through
    /// the PRODUCTION sink (so the FLAC STREAMINFO total-samples field the probe reads is written
    /// exactly as a clean finalize would write it). Synchronous on purpose: the sinks are, and a
    /// fixture writer that has to be awaited would not compose with the non-async seeds below.</summary>
    private static void WriteLeg(StoragePaths paths, string id, SourceKind kind,
        AudioFormat format, int ms)
    {
        Directory.CreateDirectory(paths.SessionDir(id));
        using var sink = AudioSinkFactory.Create(paths.AudioFile(id, kind, format), format);
        sink.Write(new float[ms * WavSink.SampleRate / 1000]);   // silence, 16 kHz mono
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
            // A crashed session has FLACs on disk and RetainedAudioSources == [] in session.json
            // (SessionBootstrap never writes the field; only PersistFinalAsync does, and a crash
            // never reaches it). 1500 ms is deliberately SHORTER than the 2000 ms transcript end,
            // so this test pins the retained re-derive WITHOUT also asserting Task 2's duration
            // re-derive - the audio does not outlast the transcript here.
            WriteLeg(paths, "s1", SourceKind.Local, AudioFormat.Flac, 1500);
            WriteLeg(paths, "s1", SourceKind.Remote, AudioFormat.Flac, 1500);
            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));

            Assert.True(await writer.RecoverIfNeededAsync("s1", default));

            var session = await new SessionStore(paths.SessionJson("s1")).ReadAsync(default);
            Assert.True(session!.Recovered);
            Assert.Equal(T0.AddMilliseconds(2000), session.EndedAtUtc);   // last segment endMs
            Assert.Equal(2000, session.DurationMs);
            Assert.Equal(1, session.MarkerCount);
            Assert.Equal(2, session.SegmentCount);

            // THE ASSERTION THIS TEST HAS ALWAYS BEEN MISSING (spec 2026-08-05, T1-2). Recovery
            // asserted four rewritten fields and never this one, so recovery leaving the field at
            // its `[]` default stayed green for the entire life of the product - while playback,
            // re-transcription, Split Speakers and import-time speaker detection all silently
            // refused the session because every one of them gates on retained.Contains(kind)
            // BEFORE any File.Exists.
            Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, session.RetainedAudioSources);

            var lines = await new TranscriptStore(paths.TranscriptJsonl("s1")).ReadAllAsync(default);
            Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.RecoveredSession);
            Assert.True(File.Exists(paths.TranscriptMd("s1")));           // regenerated

            Assert.False(await writer.RecoverIfNeededAsync("s1", default)); // idempotent
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Recovery_unions_retained_audio_and_never_narrows_an_existing_list()
    {
        // Union, never replace: a partially-written record (or an imported session) can already
        // carry a non-empty list. A momentarily unreadable leg must never DELETE a source from
        // evidentiary truth - the no-shrink rule that governs every store in this codebase.
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: null);
            var store = new SessionStore(paths.SessionJson("s1"));
            var seeded = await store.ReadAsync(default);
            await store.SaveAsync(seeded! with { RetainedAudioSources = new[] { SourceKind.Remote } }, default);
            WriteLeg(paths, "s1", SourceKind.Local, AudioFormat.Flac, 1000);   // ONLY local on disk

            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
            Assert.True(await writer.RecoverIfNeededAsync("s1", default));

            var session = await store.ReadAsync(default);
            Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, session!.RetainedAudioSources);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Recovery_of_a_never_retained_session_invents_no_audio_sources()
    {
        // AudioRetention == "never" creates no AlignedAudioWriters at all (SessionController), so
        // there are legitimately no legs. The probe is existence-based, so it must find nothing
        // and the union must stay empty - never a fabricated source the UI would then offer.
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1", endedAtUtc: null);
            var writer = new SessionWriter(paths, new Settings { AudioRetention = "never" },
                new ManualUtcTimeProvider(T0));

            Assert.True(await writer.RecoverIfNeededAsync("s1", default));

            var session = await new SessionStore(paths.SessionJson("s1")).ReadAsync(default);
            Assert.Empty(session!.RetainedAudioSources);
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
