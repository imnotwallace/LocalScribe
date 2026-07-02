using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public sealed class SessionCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private async Task WriteSessionAsync(string id, DateTimeOffset startedUtc,
        AppKind app = AppKind.Webex, int? utcOffsetMinutes = null)
        => await new SessionStore(Paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id,
            App = app,
            StartedAtUtc = startedUtc,
            EndedAtUtc = startedUtc.AddMinutes(30),
            UtcOffsetMinutes = utcOffsetMinutes,
            DurationMs = 1_800_000,
        }, default);

    [Fact]
    public async Task Lists_sessions_newest_first_with_meta()
    {
        await WriteSessionAsync("2026-06-30_0900_Webex_old", new DateTimeOffset(2026, 6, 30, 9, 0, 0, TimeSpan.Zero));
        await WriteSessionAsync("2026-07-01_1000_Webex_new", new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));
        await new MetadataStore(Paths.MetaJson("2026-07-01_1000_Webex_new"))
            .SaveAsync(new SessionMeta { Title = "Bail hearing prep" }, default);

        var result = await new SessionCatalog(Paths).ListAsync(default);

        Assert.Equal(0, result.UnreadableCount);
        Assert.Equal(2, result.Sessions.Count);
        Assert.Equal("2026-07-01_1000_Webex_new", result.Sessions[0].Id);   // newest first (StartedAtUtc desc)
        Assert.Equal("2026-06-30_0900_Webex_old", result.Sessions[1].Id);
        Assert.Equal("Bail hearing prep", result.Sessions[0].Meta.Title);
    }

    [Fact]
    public async Task Missing_meta_falls_back_to_CreateDefault_in_session_offset()
    {
        // UTC+8 offset makes the local display hour differ from UTC - proves the fallback
        // uses the session's stored offset exactly like SessionWriter (SessionWriter.cs:25-29).
        await WriteSessionAsync("2026-07-01_1800_Webex_x",
            new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero), utcOffsetMinutes: 480);

        var result = await new SessionCatalog(Paths).ListAsync(default);

        var item = Assert.Single(result.Sessions);
        Assert.Equal("Webex \u2014 2026-07-01 18:00", item.Meta.Title);
        Assert.Empty(item.Meta.Participants);                   // self: null - never fabricated
        Assert.False(File.Exists(Paths.MetaJson(item.Id)));     // fallback is in-memory, not written
    }

    [Fact]
    public async Task Unreadable_folders_are_counted_not_thrown()
    {
        await WriteSessionAsync("2026-07-01_1000_Webex_good", new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));
        Directory.CreateDirectory(Paths.SessionDir("no-session-json"));           // session.json absent
        Directory.CreateDirectory(Paths.SessionDir("corrupt"));
        await File.WriteAllTextAsync(Paths.SessionJson("corrupt"), "{ not json"); // parse throws
        Directory.CreateDirectory(Paths.SessionDir("future"));
        await File.WriteAllTextAsync(Paths.SessionJson("future"), "{\"schemaVersion\":99}"); // reject-higher throws

        var result = await new SessionCatalog(Paths).ListAsync(default);

        Assert.Equal(3, result.UnreadableCount);
        var item = Assert.Single(result.Sessions);
        Assert.Equal("2026-07-01_1000_Webex_good", item.Id);
    }

    [Fact]
    public async Task V2_folder_is_write_migrated_without_fabricated_self()
    {
        string id = "2026-06-01_1000_Teams_old";
        Directory.CreateDirectory(Paths.SessionDir(id));
        await File.WriteAllTextAsync(Paths.SessionJson(id), @"{
            ""schemaVersion"": 2,
            ""id"": ""2026-06-01_1000_Teams_old"",
            ""app"": ""Teams"",
            ""startedAtUtc"": ""2026-06-01T10:00:00Z"",
            ""endedAtUtc"": ""2026-06-01T10:30:00Z"",
            ""durationMs"": 1800000,
            ""sources"": [""Local"", ""Remote""],
            ""model"": ""small.en"",
            ""backend"": ""CPU"",
            ""language"": ""en"",
            ""retainedAudioSources"": [""Local"", ""Remote""],
            ""title"": ""Old session"",
            ""segmentCount"": 10,
            ""markerCount"": 1
        }");

        var result = await new SessionCatalog(Paths).ListAsync(default);

        Assert.Equal(0, result.UnreadableCount);
        var item = Assert.Single(result.Sessions);
        Assert.Equal(3, item.Session.SchemaVersion);                        // rewritten at v3

        // meta.json synthesized ON DISK, WITHOUT self (selfForMigration: null - design 3.1).
        var meta = await new MetadataStore(Paths.MetaJson(id)).LoadAsync(default);
        Assert.NotNull(meta);
        Assert.Equal("Old session", meta!.Title);
        Assert.Empty(meta.Participants);

        string rewritten = await File.ReadAllTextAsync(Paths.SessionJson(id));
        Assert.Contains("\"schemaVersion\": 3", rewritten);
        Assert.DoesNotContain("\"title\"", rewritten);                      // title relocated to meta.json
    }

    [Fact]
    public async Task Empty_or_missing_sessions_dir_yields_empty_result()
    {
        var result = await new SessionCatalog(Paths).ListAsync(default);    // root never created
        Assert.Empty(result.Sessions);
        Assert.Equal(0, result.UnreadableCount);

        Directory.CreateDirectory(Paths.SessionsDir);                        // exists but empty
        result = await new SessionCatalog(Paths).ListAsync(default);
        Assert.Empty(result.Sessions);
        Assert.Equal(0, result.UnreadableCount);
    }
}
