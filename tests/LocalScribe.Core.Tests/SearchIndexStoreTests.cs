using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;

public sealed class SearchIndexStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    public SearchIndexStoreTests() => _paths = new StoragePaths(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static SearchSessionEntry Entry(string id) => new()
    {
        SessionId = id, Title = "Client call", MatterIds = new[] { "M-1" },
        StartedAtUtc = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
        UtcOffsetMinutes = 480, App = "Webex", Participants = new[] { "Sam", "Jane" },
        VersionId = "v1",
        Stamps = new SearchFreshnessStamps
        { TranscriptTicks = 111, EditsTicks = 222, SpeakersTicks = 0, MetaTicks = 333 },
        Lines = new[] { new SearchLine(0, 0, 1500, "we spoke to ACME Corp", "we spoke to acme", "Sam") },
    };

    [Fact]
    public async Task Save_then_load_round_trips_under_the_index_folder()
    {
        var store = new SearchIndexStore(_paths);
        await store.SaveAsync(new SearchIndexCache { Sessions = new[] { Entry("s-1") } }, CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "index", "search-index.json"), _paths.SearchIndexJson);
        Assert.True(File.Exists(_paths.SearchIndexJson));

        var loaded = await store.LoadAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        var entry = Assert.Single(loaded!.Sessions);
        Assert.Equal("s-1", entry.SessionId);
        Assert.Equal("Client call", entry.Title);
        Assert.Equal(new[] { "M-1" }, entry.MatterIds);
        Assert.Equal(480, entry.UtcOffsetMinutes);
        Assert.Equal("Webex", entry.App);
        Assert.Equal(new[] { "Sam", "Jane" }, entry.Participants);
        Assert.Equal("v1", entry.VersionId);
        Assert.Equal(111L, entry.Stamps.TranscriptTicks);
        Assert.Equal(0L, entry.Stamps.SpeakersTicks);
        var line = Assert.Single(entry.Lines);
        Assert.Equal(0, line.Seq);
        Assert.Equal(1500L, line.StartMs);
        Assert.Equal("we spoke to ACME Corp", line.Text);
        Assert.Equal("we spoke to acme", line.OriginalText);
        Assert.Equal("Sam", line.Speaker);
    }

    [Fact]
    public async Task Missing_corrupt_and_newer_schema_caches_all_load_as_null()
    {
        var store = new SearchIndexStore(_paths);
        Assert.Null(await store.LoadAsync(CancellationToken.None));                 // missing

        Directory.CreateDirectory(Path.GetDirectoryName(_paths.SearchIndexJson)!);
        await File.WriteAllTextAsync(_paths.SearchIndexJson, "{ not json !!!");
        Assert.Null(await store.LoadAsync(CancellationToken.None));                 // corrupt -> silent rebuild

        await File.WriteAllTextAsync(_paths.SearchIndexJson, "{\"schemaVersion\": 99, \"sessions\": []}");
        Assert.Null(await store.LoadAsync(CancellationToken.None));                 // newer schema -> rebuild ours
    }
}
