using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class ClusterEmbeddingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lsembtests").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    private string PathFor() => Path.Combine(_dir, "embeddings.json");

    [Fact]
    public async Task Round_trips_entries_and_method()
    {
        var store = new ClusterEmbeddingsStore(PathFor());
        await store.SaveAsync(new ClusterEmbeddings
        {
            Method = "campplus-zh-en",
            ExtractedAtUtc = DateTimeOffset.UnixEpoch,
            Entries = new Dictionary<string, float[]> { ["Remote:0"] = [1f, 2f] },
        }, default);
        var back = await store.LoadAsync(default);
        Assert.Equal(2f, back!.Entries["Remote:0"][1]);
        Assert.Equal("campplus-zh-en", back.Method);
    }

    [Fact]
    public async Task Absent_file_loads_null()
        => Assert.Null(await new ClusterEmbeddingsStore(PathFor()).LoadAsync(default));

    [Fact]
    public async Task Corrupt_file_loads_null_not_throw()
    {
        await File.WriteAllTextAsync(PathFor(), "{not json");
        Assert.Null(await new ClusterEmbeddingsStore(PathFor()).LoadAsync(default));
    }

    [Fact]
    public async Task Newer_schema_loads_null_not_throw()
    {
        await File.WriteAllTextAsync(PathFor(), "{\"schemaVersion\":99}");
        Assert.Null(await new ClusterEmbeddingsStore(PathFor()).LoadAsync(default));
    }

    [Fact]
    public async Task Delete_removes_file_and_is_idempotent()
    {
        var store = new ClusterEmbeddingsStore(PathFor());
        await store.SaveAsync(new ClusterEmbeddings(), default);
        store.Delete();
        Assert.False(File.Exists(PathFor()));
        store.Delete();   // no throw
    }

    [Fact]
    public async Task Non_object_json_root_loads_null_not_throw()
    {
        await File.WriteAllTextAsync(PathFor(), "[]");
        Assert.Null(await new ClusterEmbeddingsStore(PathFor()).LoadAsync(default));
    }

    [Fact]
    public async Task Non_numeric_schema_version_loads_null_not_throw()
    {
        await File.WriteAllTextAsync(PathFor(), "{\"schemaVersion\":\"x\"}");
        Assert.Null(await new ClusterEmbeddingsStore(PathFor()).LoadAsync(default));
    }

    [Fact]
    public void StoragePaths_layout()
    {
        var p = new StoragePaths(Path.Combine(_dir, "root"));
        Assert.EndsWith(Path.Combine("sessions", "s1", "embeddings.json"), p.EmbeddingsJson("s1"));
        Assert.EndsWith(Path.Combine("s1", "versions", "v2", "embeddings.json"), p.EmbeddingsJson("s1", "v2"));
        Assert.Equal(p.EmbeddingsJson("s1"), p.EmbeddingsJson("s1", LocalScribe.Core.Model.TranscriptVersions.Root));
        Assert.EndsWith(Path.Combine("people", "people.json"), p.PeopleJson);
    }
}
