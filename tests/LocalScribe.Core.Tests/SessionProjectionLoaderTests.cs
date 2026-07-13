// tests/LocalScribe.Core.Tests/SessionProjectionLoaderTests.cs
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class SessionProjectionLoaderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 14, 32, 0, TimeSpan.Zero);

    private static async Task SeedAsync(StoragePaths paths, string id)
    {
        Directory.CreateDirectory(paths.SessionDir(id));
        await new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = T0.AddMinutes(1),
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
            DurationMs = 60000, Model = "small.en", Backend = "CUDA",
            Sources = new[] { SourceKind.Local, SourceKind.Remote },
        }, default);
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            Title = "Doe intake", Medium = Medium.Webex, LocalCount = 1, RemoteCount = 1,
            MatterIds = new[] { "M-1" },
            Participants = new[]
            {
                new SessionParticipant { Id = "p1", Name = "Sam", Side = SourceKind.Local },
                new SessionParticipant { Id = "p2", Name = "Bob", Side = SourceKind.Remote },
            },
        }, default);
        await new MatterStore(paths.MattersDir).SaveAsync(
            new Matter { Id = "M-1", Name = "Acme", Reference = "2026-014" }, default);
        var t = new TranscriptStore(paths.TranscriptJsonl(id));
        await t.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Hello there.", "Me"), default);
        await t.AppendAsync(TranscriptLine.Marker(1, 500, "audio device changed"), default);
        await t.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Remote, 1000, 2000, "Hi.", "Them"), default);
    }

    [Fact]
    public async Task Writer_renders_golden_projections()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1");
            await new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0))
                .RegenerateProjectionsAsync("s1", default);

            Assert.Equal(
                "# Doe intake\n" +
                "Webex · 2026-07-02 22:32 · 1 min · small.en/CUDA\n" +
                "\n" +
                "**[00:00] Sam:** Hello there.\n" +
                "\n" +
                "_[audio device changed]_\n" +
                "\n" +
                "**[00:01] Bob:** Hi.\n",
                await File.ReadAllTextAsync(paths.TranscriptMd("s1")));
            Assert.Equal(
                "Doe intake\n" +
                "Webex · 2026-07-02 22:32 · 1 min · small.en/CUDA\n" +
                "\n" +
                "[00:00] Sam: Hello there.\n" +
                "\n" +
                "[audio device changed]\n" +
                "\n" +
                "[00:01] Bob: Hi.\n",
                await File.ReadAllTextAsync(paths.TranscriptTxt("s1")));
            Assert.Equal(
                "Doe intake\n" +
                "\n" +
                "Matter(s): Acme (2026-014)\n" +
                "Participants: Sam (Local), Bob (Remote)\n" +
                "Date: 2026-07-02 22:32 - 22:33 (1 min)\n" +
                "Medium: Webex\n" +
                "Description: (none)\n" +
                "Summary: (none)\n",
                await File.ReadAllTextAsync(paths.SessionTxt("s1")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LoadAsync_returns_resolved_rows_header_and_textview()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1");
            var loaded = await SessionProjectionLoader.LoadAsync(
                paths, new Settings(), new ManualUtcTimeProvider(T0), "s1", default);

            Assert.Equal("Doe intake", loaded.Header.Title);
            Assert.Equal("Webex", loaded.Header.App);
            Assert.Equal(new[] { "Acme (2026-014)" }, loaded.TextView.Matters);
            Assert.Equal(new[] { "Sam (Local)", "Bob (Remote)" }, loaded.TextView.Participants);
            Assert.Equal(3, loaded.Rows.Count);
            Assert.Equal("Sam", loaded.Rows[0].DisplayName);
            Assert.True(loaded.Rows[1].IsMarker);
            Assert.Equal("Bob", loaded.Rows[2].DisplayName);
            Assert.True(loaded.MattersById.ContainsKey("M-1"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private const string Vid = "v2-tiny.en-2026-07-13";

    /// <summary>Layers a completed v2 on top of SeedAsync's session: its own jsonl (different
    /// text), empty edits, a Versions entry, and ActiveVersion flipped - the exact on-disk shape
    /// RetranscriptionRunner commits.</summary>
    private static async Task SeedVersionAsync(StoragePaths paths, string id)
    {
        Directory.CreateDirectory(paths.VersionDir(id, Vid));
        var t = new TranscriptStore(paths.TranscriptJsonl(id, Vid));
        await t.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Second pass hello.", "Me"), default);
        await JsonFile.WriteAsync(paths.EditsJson(id, Vid), new Edits(), default);
        var store = new SessionStore(paths.SessionJson(id));
        var session = (await store.ReadAsync(default))!;
        await store.SaveAsync(session with
        {
            ActiveVersion = Vid,
            Versions = new[] { new TranscriptVersion { Id = Vid, Model = "tiny.en", Backend = "CPU", Language = "en" } },
        }, default);
    }

    [Fact]
    public async Task LoadAsync_follows_activeVersion_and_the_explicit_overload_pins_a_version()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1");
            await SeedVersionAsync(paths, "s1");
            var time = new ManualUtcTimeProvider(T0);

            var active = await SessionProjectionLoader.LoadAsync(paths, new Settings(), time, "s1", default);
            Assert.Equal(Vid, active.VersionId);
            Assert.Equal("tiny.en", active.Header.Model);            // the VERSION's actuals
            Assert.Equal("CPU", active.Header.Backend);
            Assert.Single(active.Rows);
            Assert.Equal("Second pass hello.", active.Rows[0].Text);

            var original = await SessionProjectionLoader.LoadAsync(paths, new Settings(), time, "s1", "v1", default);
            Assert.Equal("v1", original.VersionId);
            Assert.Equal("small.en", original.Header.Model);         // root truth untouched
            Assert.Equal(3, original.Rows.Count);
            Assert.Equal("Hello there.", original.Rows[0].Text);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SessionProjectionLoader.LoadAsync(paths, new Settings(), time, "s1", "v9-nope-2026-01-01", default));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Writer_renders_into_the_active_versions_folder_and_never_touches_root_projections()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        var paths = new StoragePaths(root);
        try
        {
            await SeedAsync(paths, "s1");
            var writer = new SessionWriter(paths, new Settings(), new ManualUtcTimeProvider(T0));
            await writer.RegenerateProjectionsAsync("s1", default);          // v1 render (baseline)
            byte[] rootMd = await File.ReadAllBytesAsync(paths.TranscriptMd("s1"));

            await SeedVersionAsync(paths, "s1");
            await writer.RegenerateProjectionsAsync("s1", default);          // now active = v2

            Assert.True(File.Exists(paths.TranscriptMd("s1", Vid)));
            Assert.True(File.Exists(paths.TranscriptTxt("s1", Vid)));
            string vMd = await File.ReadAllTextAsync(paths.TranscriptMd("s1", Vid));
            Assert.Contains("Second pass hello.", vMd);
            Assert.Contains("tiny.en/CPU", vMd);                              // version header actuals
            // v1 (the immutable original): its rendered projection is bit-for-bit untouched.
            Assert.Equal(rootMd, await File.ReadAllBytesAsync(paths.TranscriptMd("s1")));
            Assert.True(File.Exists(paths.SessionTxt("s1")));                 // session.txt stays root
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
