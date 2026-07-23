using System.Text.Json.Nodes;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

public sealed class SummaryStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-sumstore-").FullName;
    private readonly StoragePaths _paths;
    private readonly SummaryStore _store;

    public SummaryStoreTests()
    {
        _paths = new StoragePaths(_root);
        _store = new SummaryStore(_paths);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static SummaryVersion V(string id, bool stale = false) => new(id,
        new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.Zero), "v1",
        new AssistantModelRef("q4b.gguf", new string('a', 64), "cuda"),
        AssistantPrompts.PromptVersion, "## Summary\nBody.", stale);

    [Fact]
    public void Assistant_paths_live_under_the_session_folder()
    {
        Assert.Equal(Path.Combine(_paths.SessionDir("s1"), "assistant"), _paths.AssistantDir("s1"));
        Assert.Equal(Path.Combine(_paths.AssistantDir("s1"), "summaries.json"), _paths.SummariesJson("s1"));
    }

    [Fact]
    public async Task Load_of_a_session_without_summaries_is_empty()
        => Assert.Empty(await _store.LoadAsync("s1", CancellationToken.None));

    [Fact]
    public async Task Append_is_append_only_and_round_trips_the_locked_shape()
    {
        await _store.AppendAsync("s1", V("s1-a"), CancellationToken.None);
        await _store.AppendAsync("s1", V("s2-b"), CancellationToken.None);

        var loaded = await _store.LoadAsync("s1", CancellationToken.None);
        Assert.Equal(new[] { "s1-a", "s2-b" }, loaded.Select(v => v.Id));   // order preserved
        Assert.Equal(V("s1-a"), loaded[0]);                                  // full record round-trip

        // The sidecar carries the schema stamp + camelCase design-7.3 shape on disk.
        var obj = JsonNode.Parse(File.ReadAllText(_paths.SummariesJson("s1")))!.AsObject();
        Assert.Equal(SummaryStore.Version, obj["schemaVersion"]!.GetValue<int>());
        var first = obj["versions"]!.AsArray()[0]!.AsObject();
        Assert.Equal("v1", first["sourceTranscriptVersion"]!.GetValue<string>());
        Assert.Equal("cuda", first["model"]!.AsObject()["backend"]!.GetValue<string>());
        Assert.Equal(1, first["promptVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task MarkAllStale_flips_flags_but_never_content_and_noops_when_all_stale()
    {
        await _store.AppendAsync("s1", V("s1-a"), CancellationToken.None);
        await _store.AppendAsync("s1", V("s2-b", stale: true), CancellationToken.None);

        await _store.MarkAllStaleAsync("s1", CancellationToken.None);
        var loaded = await _store.LoadAsync("s1", CancellationToken.None);
        Assert.All(loaded, v => Assert.True(v.Stale));
        Assert.Equal("## Summary\nBody.", loaded[0].ContentMarkdown);   // content untouched

        // No-op discipline: already-all-stale must not rewrite the file (mtime unchanged).
        var before = File.GetLastWriteTimeUtc(_paths.SummariesJson("s1"));
        await Task.Delay(30);
        await _store.MarkAllStaleAsync("s1", CancellationToken.None);
        Assert.Equal(before, File.GetLastWriteTimeUtc(_paths.SummariesJson("s1")));
        // And a session with no summaries file at all is a clean no-op, not a crash or a write.
        await _store.MarkAllStaleAsync("never-summarized", CancellationToken.None);
        Assert.False(File.Exists(_paths.SummariesJson("never-summarized")));
    }

    [Fact]
    public async Task Sidecars_written_before_the_fall_field_existed_still_load_as_not_fallen()
    {
        // CudaFellToCpu is a 2026-07-23 ADDITIVE field on a LOCKED contract: a summaries.json
        // written by an older build (no "cudaFellToCpu" member at all) must still load, at the
        // same schemaVersion, with the flag false - never a crash, never a false positive.
        Directory.CreateDirectory(_paths.AssistantDir("s1"));
        File.WriteAllText(_paths.SummariesJson("s1"), """
            {"schemaVersion":1,"versions":[{"id":"s1-a","createdAt":"2026-07-19T10:00:00+00:00",
            "sourceTranscriptVersion":"v1","model":{"file":"q4b.gguf","sha256":"aaaa","backend":"cuda"},
            "promptVersion":1,"contentMarkdown":"## Summary\nBody.","stale":false}]}
            """);

        var loaded = Assert.Single(await _store.LoadAsync("s1", CancellationToken.None));
        Assert.Equal("s1-a", loaded.Id);
        Assert.False(loaded.CudaFellToCpu);
    }

    [Fact]
    public async Task Newer_schema_is_rejected_not_mangled()
    {
        Directory.CreateDirectory(_paths.AssistantDir("s1"));
        File.WriteAllText(_paths.SummariesJson("s1"), "{\"schemaVersion\": 99, \"versions\": []}");
        await Assert.ThrowsAsync<NotSupportedException>(() => _store.LoadAsync("s1", CancellationToken.None));
    }
}
