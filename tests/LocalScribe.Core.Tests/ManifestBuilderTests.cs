using System.Text.Json.Nodes;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

/// <summary>manifest.json: the integrity seal over one transcript version's evidentiary files
/// (Tier 1 T1-7, spec 2026-08-05 :146-153). Hashing happens ONCE at finalize; the export path only
/// ever reads the stored value, so the 2026-08-04 ruling against hashing recorded audio AT EXPORT
/// TIME stands untouched.</summary>
public sealed class ManifestBuilderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-manifest-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public ManifestBuilderTests() { _paths = new StoragePaths(_root); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Manifest_round_trips_and_carries_the_schema_stamp_on_disk()
    {
        var store = new ManifestStore(_paths.ManifestJson("s1"));
        var manifest = new SessionManifest
        {
            SessionId = "s1",
            WrittenAtUtc = new DateTimeOffset(2026, 8, 5, 10, 22, 0, TimeSpan.Zero),
            Files =
            [
                new ManifestFile
                {
                    Name = "local.flac", Sha256 = "abc123", SizeBytes = 4096,
                    ModifiedUtc = new DateTimeOffset(2026, 8, 5, 10, 21, 0, TimeSpan.Zero),
                    SampleRate = 16000, FabricatedSilenceKnown = true,
                    FabricatedSilence =
                        [new FabricatedSpan { StartSample = 0, EndSample = 32000, Reason = "clock-gap" }],
                },
            ],
        };

        await store.SaveAsync(manifest, CancellationToken.None);

        // Field-by-field, NEVER Assert.Equal over the whole SessionManifest. Both it and
        // ManifestFile carry IReadOnlyList members, and the compiler-generated record Equals
        // compares those with EqualityComparer<IReadOnlyList<T>>.Default - REFERENCE equality on
        // the backing list. An in-memory collection expression and a freshly deserialized List can
        // never be reference-equal, so a whole-record assertion here is unreachable by
        // construction. Assert.Equal over an IEnumerable IS element-wise, which is why the two
        // list comparisons below are safe (FabricatedSpan has no collection members of its own).
        var read = await store.ReadAsync(CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(ManifestStore.Version, read!.SchemaVersion);
        Assert.Equal("s1", read.SessionId);
        Assert.Equal(TranscriptVersions.Root, read.VersionId);
        Assert.Equal(manifest.WrittenAtUtc, read.WrittenAtUtc);
        var readFile = Assert.Single(read.Files);
        Assert.Equal("local.flac", readFile.Name);
        Assert.Equal("abc123", readFile.Sha256);
        Assert.Equal(4096, readFile.SizeBytes);
        Assert.Equal(manifest.Files[0].ModifiedUtc, readFile.ModifiedUtc);
        Assert.Equal(16000, readFile.SampleRate);
        Assert.True(readFile.FabricatedSilenceKnown);
        Assert.Equal(manifest.Files[0].FabricatedSilence, readFile.FabricatedSilence);

        var obj = JsonNode.Parse(File.ReadAllText(_paths.ManifestJson("s1")))!.AsObject();
        Assert.Equal(ManifestStore.Version, obj["schemaVersion"]!.GetValue<int>());
        Assert.Equal("v1", obj["versionId"]!.GetValue<string>());
        var file = obj["files"]!.AsArray()[0]!.AsObject();
        Assert.Equal("abc123", file["sha256"]!.GetValue<string>());
        Assert.Equal("clock-gap", file["fabricatedSilence"]!.AsArray()[0]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_absent_manifest_reads_as_null_and_a_newer_schema_is_rejected_not_mangled()
    {
        // Absent is the normal state for every session recorded before this feature - it must read
        // as "unsealed", never as a crash and never as an empty seal that would report every file
        // as verified.
        var store = new ManifestStore(_paths.ManifestJson("s-absent"));
        Assert.Null(await store.ReadAsync(CancellationToken.None));

        Directory.CreateDirectory(_paths.SessionDir("s-newer"));
        File.WriteAllText(_paths.ManifestJson("s-newer"), "{\"schemaVersion\": 99, \"files\": []}");
        await Assert.ThrowsAsync<NotSupportedException>(
            () => new ManifestStore(_paths.ManifestJson("s-newer")).ReadAsync(CancellationToken.None));
    }
}
