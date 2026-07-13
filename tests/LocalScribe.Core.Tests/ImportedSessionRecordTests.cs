using System.Text.Json.Nodes;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

public sealed class ImportedSessionRecordTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-import-record-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Origin_and_ImportedSource_roundtrip_through_session_json()
    {
        var paths = new StoragePaths(_root);
        Directory.CreateDirectory(paths.SessionDir("s-imp"));
        var store = new SessionStore(paths.SessionJson("s-imp"));
        var record = new SessionRecord
        {
            Id = "s-imp", App = AppKind.Manual,
            StartedAtUtc = new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero),
            Origin = "imported",
            ImportedSource = new ImportedSourceInfo
            {
                FileName = "hearing.mp3", Sha256 = "abc123", FileSizeBytes = 12345,
                ContainerFormat = "mp3",
                FileCreatedUtc = new DateTimeOffset(2026, 3, 5, 4, 0, 0, TimeSpan.Zero),
                FileModifiedUtc = new DateTimeOffset(2026, 3, 5, 5, 0, 0, TimeSpan.Zero),
                MediaCreatedUtc = new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero),
                ClaimedDurationMs = 600_000, DecodedDurationMs = 599_000,
                DecodedSampleRate = 44100, DecodedChannels = 2,
                ChannelMapping = "split", DurationMismatch = false,
            },
        };
        await store.SaveAsync(record, CancellationToken.None);

        var read = await store.ReadAsync(CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal("imported", read!.Origin);
        Assert.Equal(record.ImportedSource, read.ImportedSource);   // record value-equality

        // Wire shape: camelCase keys, so downstream tooling reads "origin"/"importedSource".
        string json = await File.ReadAllTextAsync(paths.SessionJson("s-imp"));
        Assert.Contains("\"origin\": \"imported\"", json);
        Assert.Contains("\"importedSource\"", json);
        Assert.Contains("\"sha256\": \"abc123\"", json);
    }

    [Fact]
    public async Task Recorded_sessions_default_to_origin_recorded_and_omit_importedSource()
    {
        var paths = new StoragePaths(_root);
        Directory.CreateDirectory(paths.SessionDir("s-rec"));
        var store = new SessionStore(paths.SessionJson("s-rec"));
        await store.SaveAsync(new SessionRecord
        {
            Id = "s-rec", App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 3, 5, 4, 30, 0, TimeSpan.Zero),
        }, CancellationToken.None);

        string json = await File.ReadAllTextAsync(paths.SessionJson("s-rec"));
        Assert.DoesNotContain("importedSource", json);              // WhenWritingNull omits the null

        // Back-compat: a session.json written BEFORE this field existed (no "origin" key at the
        // CURRENT schema version) must read as the "recorded" default. Strip the key from a
        // freshly-written current-version file so this stays valid across future schema bumps.
        var node = JsonNode.Parse(json)!.AsObject();
        node.Remove("origin");
        await File.WriteAllTextAsync(paths.SessionJson("s-rec"), node.ToJsonString());
        var read = await store.ReadAsync(CancellationToken.None);
        Assert.Equal("recorded", read!.Origin);
        Assert.Null(read.ImportedSource);
    }

    [Fact]
    public void Source_paths_compose_under_the_session_folder()
    {
        var paths = new StoragePaths(_root);
        Assert.Equal(Path.Combine(paths.SessionDir("s-1"), "source"), paths.SourceDir("s-1"));
        Assert.Equal(Path.Combine(paths.SessionDir("s-1"), "source", "call.m4a"),
            paths.SourceFile("s-1", "call.m4a"));
    }

    [Fact]
    public void Import_marker_templates_format_cleanly()
    {
        Assert.Equal("imported audio duration mismatch: container claimed 10:00, decoded 5:00",
            string.Format(Markers.ImportedDurationMismatch, "10:00", "5:00"));
        Assert.Equal("imported audio downmixed to mono: source had 6 channels",
            string.Format(Markers.ImportedDownmixed, 6));
    }
}
