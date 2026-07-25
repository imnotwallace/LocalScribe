using LocalScribe.Core.Search;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;

public sealed class SemanticIndexStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private readonly StoragePaths _paths;
    private readonly SemanticIndexStore _store;
    public SemanticIndexStoreTests()
    { _paths = new StoragePaths(_root); _store = new SemanticIndexStore(_paths); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static SemanticSidecar Sidecar() => new(
        Method: "m@2", VersionId: "v1",
        Stamps: new SearchFreshnessStamps { TranscriptTicks = 11, EditsTicks = 22, SpeakersTicks = 33, MetaTicks = 44 },
        Dim: 2,
        Chunks: [new SemanticChunk(0, 0, 0, 1, 1900, "Alice: hello\nBob: hi"),
                 new SemanticChunk(1, 0, 1000, 2, 2900, "Bob: hi\nAlice: settle at 350k")],
        Vectors: [[0.6f, 0.8f], [1f, 0f]]);

    [Fact]
    public async Task Round_trips_every_field()
    {
        await _store.SaveAsync("s-1", Sidecar(), CancellationToken.None);
        var loaded = await _store.LoadAsync("s-1", CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal("m@2", loaded.Method);
        Assert.Equal(2, loaded.Dim);
        Assert.Equal(Sidecar().Chunks[0], loaded.Chunks[0]);   // SemanticChunk is a value record
        Assert.Equal(2, loaded.Chunks.Count);
        Assert.Equal("Bob: hi\nAlice: settle at 350k", loaded.Chunks[1].Text);
        Assert.Equal(new[] { 0.6f, 0.8f }, loaded.Vectors[0]);
        Assert.Equal(new[] { 1f, 0f }, loaded.Vectors[1]);
        Assert.Equal(11, loaded.Stamps.TranscriptTicks);
        Assert.Equal("v1", loaded.VersionId);
    }

    [Fact]
    public async Task Missing_file_loads_null()
        => Assert.Null(await _store.LoadAsync("nope", CancellationToken.None));

    [Fact]
    public async Task Truncated_file_loads_null_never_throws()
    {
        await _store.SaveAsync("s-1", Sidecar(), CancellationToken.None);
        string path = _paths.SemanticSidecarFile("s-1");
        byte[] bytes = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, bytes[..(bytes.Length / 2)]);
        Assert.Null(await _store.LoadAsync("s-1", CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_magic_or_newer_version_loads_null()
    {
        string path = _paths.SemanticSidecarFile("s-1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, [9, 9, 9, 9, 9, 9, 9, 9]);
        Assert.Null(await _store.LoadAsync("s-1", CancellationToken.None));

        // newer schema: valid magic, version+1
        await _store.SaveAsync("s-2", Sidecar(), CancellationToken.None);
        byte[] bytes = await File.ReadAllBytesAsync(_paths.SemanticSidecarFile("s-2"));
        bytes[4] = (byte)(SemanticIndexStore.Version + 1);      // version int little-endian low byte
        await File.WriteAllBytesAsync(_paths.SemanticSidecarFile("s-2"), bytes);
        Assert.Null(await _store.LoadAsync("s-2", CancellationToken.None));
    }

    [Fact]
    public async Task Delete_is_idempotent_and_list_enumerates_saved_ids()
    {
        Assert.Empty(_store.ListSessionIds());                  // dir absent: empty, no throw
        await _store.SaveAsync("s-1", Sidecar(), CancellationToken.None);
        await _store.SaveAsync("s-2", Sidecar(), CancellationToken.None);
        Assert.Equal(["s-1", "s-2"], _store.ListSessionIds().OrderBy(x => x, StringComparer.Ordinal));
        _store.Delete("s-1");
        _store.Delete("s-1");                                   // second delete: no throw
        Assert.Equal(["s-2"], _store.ListSessionIds());
    }
}
