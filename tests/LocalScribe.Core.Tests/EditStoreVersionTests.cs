using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public sealed class EditStoreVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    public EditStoreVersionTests() => _paths = new StoragePaths(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private const string Vid = "v2-base.en-2026-07-13";

    private async Task<string> SeedVersionedAsync()
    {
        string id = "2026-07-10_1000_Webex_seed";
        Directory.CreateDirectory(_paths.SessionDir(id));
        Directory.CreateDirectory(_paths.VersionDir(id, Vid));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 10, 2, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 10, 2, 1, 0, TimeSpan.Zero),
            DurationMs = 60000, Model = "small.en", Backend = "CPU", Language = "en",
            ActiveVersion = Vid,
            Versions = new[] { new TranscriptVersion { Id = Vid, Model = "base.en", Backend = "CPU", Language = "en" } },
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Seed", Medium = Medium.Webex }, default);
        // Root machine transcript has seq 0 ONLY; the version transcript has seq 0 AND 1 -
        // proving below which jsonl the seq validation reads.
        var rootT = new TranscriptStore(_paths.TranscriptJsonl(id));
        await rootT.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Root words.", "Me"), default);
        var vT = new TranscriptStore(_paths.TranscriptJsonl(id, Vid));
        await vT.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "V2 words.", "Me"), default);
        await vT.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 1000, 2000, "V2 more.", "Me"), default);
        return id;
    }

    [Fact]
    public async Task ContentDir_routes_edits_and_seq_validation_to_the_version_folder()
    {
        string id = await SeedVersionedAsync();
        var store = new EditStore(_paths.SessionDir(id), TimeProvider.System,
            contentDir: _paths.VersionDir(id, Vid));

        // seq 1 exists ONLY in the version transcript: validating against the root would throw.
        await store.ApplyTextCorrectionAsync(1, "V2 corrected.", default);

        Assert.True(File.Exists(_paths.EditsJson(id, Vid)));
        Assert.False(File.Exists(_paths.EditsJson(id)));            // root edits untouched
        var edits = await store.LoadAsync(default);
        Assert.Equal("V2 corrected.", edits!.Corrections["1"].Text);
        // The finalized gate still read the ROOT session.json (it lives nowhere else).
        var meta = await new MetadataStore(_paths.MetaJson(id)).LoadAsync(default);
        Assert.True(meta!.Edited);                                   // Edited flip is session-level
    }

    [Fact]
    public async Task Default_contentDir_is_the_session_root_v1()
    {
        string id = await SeedVersionedAsync();
        var store = new EditStore(_paths.SessionDir(id), TimeProvider.System);
        await store.ApplyTextCorrectionAsync(0, "Root corrected.", default);
        Assert.True(File.Exists(_paths.EditsJson(id)));
        Assert.False(File.Exists(_paths.EditsJson(id, Vid)));
    }
}
