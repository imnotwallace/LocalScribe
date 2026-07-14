using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;

public sealed class SearchIndexBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
    public SearchIndexBuilderTests() => _paths = new StoragePaths(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static readonly DateTimeOffset Started = new(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Finalized Webex session at UTC+8: two Local segments (seq 1 corrected via the
    /// EditStore overlay), one Remote segment, one marker; named participants Sam/Jane.</summary>
    private async Task SeedAsync(string id)
    {
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = Started, EndedAtUtc = Started.AddMinutes(10),
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480, DurationMs = 600_000,
            Model = "small.en", Backend = "cuda", Language = "en",
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            Title = "Client call", MatterIds = new[] { "M-2026-001" },
            Participants = new[]
            {
                new SessionParticipant { Id = "p1", Name = "Sam", Side = SourceKind.Local, IsSelf = true },
                new SessionParticipant { Id = "p2", Name = "Jane", Side = SourceKind.Remote },
            },
        }, default);
        var t = new TranscriptStore(_paths.TranscriptJsonl(id));
        await t.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1500,
            "we spoke to the client this morning", "Me"), default);
        await t.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 1600, 3000,
            "the orignal words", "Me"), default);
        await t.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Remote, 3200, 4200,
            "sounds good", "Them"), default);
        await t.AppendAsync(TranscriptLine.Marker(3, 4200, Markers.AudioDeviceChanged), default);
        await new EditStore(_paths.SessionDir(id), _time)
            .ApplyTextCorrectionAsync(1, "the corrected words", default);
    }

    [Fact]
    public async Task Entry_derives_corrected_text_original_only_where_corrected_and_skips_markers()
    {
        await SeedAsync("s-1");
        var entry = await SearchIndexBuilder.BuildEntryAsync(_paths, new Settings(), _time, "s-1", default);

        Assert.Equal("s-1", entry.SessionId);
        Assert.Equal("Client call", entry.Title);
        Assert.Equal(new[] { "M-2026-001" }, entry.MatterIds);
        Assert.Equal(Started, entry.StartedAtUtc);
        Assert.Equal(480, entry.UtcOffsetMinutes);
        Assert.Equal("Webex", entry.App);
        Assert.Equal(new[] { "Sam", "Jane" }, entry.Participants);
        Assert.Equal("v1", entry.VersionId);

        Assert.Equal(3, entry.Lines.Count);                              // marker (seq 3) excluded
        var l0 = entry.Lines[0];
        Assert.Equal(0, l0.Seq);
        Assert.Equal("we spoke to the client this morning", l0.Text);
        Assert.Null(l0.OriginalText);                                    // uncorrected -> no original stored
        Assert.Equal("Sam", l0.Speaker);                                 // lone named Local participant
        var l1 = entry.Lines[1];
        Assert.Equal(1, l1.Seq);
        Assert.Equal("the corrected words", l1.Text);
        Assert.Equal("the orignal words", l1.OriginalText);              // stored only where a correction differs
        var l2 = entry.Lines[2];
        Assert.Equal("Jane", l2.Speaker);
        Assert.Equal(3200L, l2.StartMs);
    }

    [Fact]
    public async Task Stamps_are_last_write_ticks_of_the_stamped_files_with_zero_for_absent()
    {
        await SeedAsync("s-2");
        var entry = await SearchIndexBuilder.BuildEntryAsync(_paths, new Settings(), _time, "s-2", default);
        Assert.Equal(File.GetLastWriteTimeUtc(_paths.TranscriptJsonl("s-2")).Ticks, entry.Stamps.TranscriptTicks);
        Assert.Equal(File.GetLastWriteTimeUtc(_paths.EditsJson("s-2")).Ticks, entry.Stamps.EditsTicks);
        Assert.Equal(0L, entry.Stamps.SpeakersTicks);                    // speakers.json absent -> 0
        Assert.Equal(File.GetLastWriteTimeUtc(_paths.MetaJson("s-2")).Ticks, entry.Stamps.MetaTicks);
        Assert.Equal(entry.Stamps, SearchIndexBuilder.ComputeStamps(_paths, "s-2", "v1"));
    }

    [Fact]
    public async Task Split_children_index_as_separate_lines_and_are_not_labelled_corrections()
    {
        await SeedAsync("s-3");
        await new EditStore(_paths.SessionDir("s-3"), _time).ApplySplitAsync(0, TranscriptSource.Local,
            new[]
            {
                new SplitPart { Text = "we spoke to the client", StartMs = 0 },
                new SplitPart { Text = "this morning", StartMs = 700, DerivedStart = true },
            }, default);
        var entry = await SearchIndexBuilder.BuildEntryAsync(_paths, new Settings(), _time, "s-3", default);
        var parts = entry.Lines.Where(l => l.Seq == 0).ToList();
        Assert.Equal(2, parts.Count);
        Assert.Equal(0, parts[0].PartIndex);
        Assert.Equal("we spoke to the client", parts[0].Text);
        Assert.Equal(1, parts[1].PartIndex);
        Assert.Equal(700L, parts[1].StartMs);
        Assert.Equal("this morning", parts[1].Text);
        Assert.All(parts, p => Assert.Null(p.OriginalText));             // a split is not a correction
    }

    [Fact]
    public async Task Entry_follows_the_active_version()
    {
        // Cross-branch surface (feat/retranscription-versions): ActiveVersion + versioned paths.
        // If that branch's version-id format differs from the "v2-<model>-<date>" folder id used
        // here, substitute ITS id format - the assertions only require that the ACTIVE version's
        // transcript is what gets indexed and stamped.
        await SeedAsync("s-4");
        const string v2 = "v2-small.en-2026-07-13";
        var store = new SessionStore(_paths.SessionJson("s-4"));
        var record = await store.ReadAsync(default);
        await store.SaveAsync(record! with
        {
            ActiveVersion = v2,
            Versions = new[]
            {
                new TranscriptVersion
                {
                    Id = v2, Model = "small.en", Backend = "cuda", Language = "en",
                    CreatedAtUtc = Started.AddDays(1),
                },
            },
        }, default);
        var t2 = new TranscriptStore(_paths.TranscriptJsonl("s-4", v2));
        await t2.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 900,
            "completely retranscribed text", "Me"), default);

        var entry = await SearchIndexBuilder.BuildEntryAsync(_paths, new Settings(), _time, "s-4", default);
        Assert.Equal(v2, entry.VersionId);
        var line = Assert.Single(entry.Lines);
        Assert.Equal("completely retranscribed text", line.Text);
        Assert.Equal(File.GetLastWriteTimeUtc(_paths.TranscriptJsonl("s-4", v2)).Ticks,
            entry.Stamps.TranscriptTicks);
        Assert.Equal(0L, entry.Stamps.EditsTicks);                       // v2 starts with no edits overlay
    }
}
