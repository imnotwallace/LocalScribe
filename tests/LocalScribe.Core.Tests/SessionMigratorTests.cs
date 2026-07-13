using System.Text.Json.Nodes;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class SessionMigratorTests
{
    private static JsonObject V1(bool audioRetained) => JsonNode.Parse($@"{{
        ""schemaVersion"": 1,
        ""id"": ""2026-06-01_1000_Teams_old"",
        ""app"": ""Teams"",
        ""startedAtUtc"": ""2026-06-01T10:00:00Z"",
        ""endedAtUtc"": ""2026-06-01T10:30:00Z"",
        ""durationMs"": 1800000,
        ""sources"": [""Local"", ""Remote""],
        ""model"": ""small.en"",
        ""backend"": ""CPU"",
        ""language"": ""en"",
        ""audioRetained"": {(audioRetained ? "true" : "false")},
        ""title"": ""Old session"",
        ""segmentCount"": 10,
        ""markerCount"": 1
    }}")!.AsObject();

    [Fact]
    public void V1_true_maps_retained_sources_to_all_sources()
    {
        var r = SessionMigrator.Migrate(V1(audioRetained: true), self: null);
        Assert.Equal(4, r.Session.SchemaVersion);
        Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, r.Session.RetainedAudioSources);
    }

    [Fact]
    public void V1_false_maps_retained_sources_to_empty()
    {
        var r = SessionMigrator.Migrate(V1(audioRetained: false), self: null);
        Assert.Empty(r.Session.RetainedAudioSources);
    }

    [Fact]
    public void V2_to_v3_moves_title_to_synthesized_meta_and_defaults_devices()
    {
        var v2 = V1(audioRetained: true);
        v2["schemaVersion"] = 2;
        v2.Remove("audioRetained");
        v2["retainedAudioSources"] = new JsonArray("Local", "Remote");

        var self = new SessionParticipant { Id = "p-self", Name = "Sam", Side = SourceKind.Local, IsSelf = true };
        var r = SessionMigrator.Migrate(v2, self);

        // session.json no longer carries title; meta.json does.
        Assert.Equal(4, r.Session.SchemaVersion);
        Assert.NotNull(r.SynthesizedMeta);
        Assert.Equal("Old session", r.SynthesizedMeta!.Title);
        Assert.Equal(Medium.Teams, r.SynthesizedMeta.Medium);          // medium defaulted from app
        Assert.Single(r.SynthesizedMeta.Participants);                 // self only
        Assert.True(r.SynthesizedMeta.Participants[0].IsSelf);
        Assert.Empty(r.SynthesizedMeta.MatterIds);

        // legacy device snapshot
        Assert.Equal("legacy", r.Session.Devices.Mic.Name);
        Assert.Equal(RemoteMode.SystemMix, r.Session.Devices.Remote.Mode);

        // legacy records carry no timezone capture (spec 1.2): stays null/omitted
        Assert.Null(r.Session.TimeZoneId);
        Assert.Null(r.Session.UtcOffsetMinutes);
    }

    [Fact]
    public void V2_to_v3_with_no_self_yields_empty_participants()
    {
        var v2 = V1(audioRetained: false);
        v2["schemaVersion"] = 2;
        v2.Remove("audioRetained");
        v2["retainedAudioSources"] = new JsonArray();
        var r = SessionMigrator.Migrate(v2, self: null);
        Assert.NotNull(r.SynthesizedMeta);
        Assert.Empty(r.SynthesizedMeta!.Participants);
    }

    [Fact]
    public void Already_v3_returns_no_synthesized_meta()
    {
        var v3 = JsonNode.Parse(@"{""schemaVersion"":3,""id"":""x"",""app"":""Webex"",
            ""startedAtUtc"":""2026-07-02T14:32:05Z"",""durationMs"":0,""sources"":[],
            ""model"":"""",""backend"":"""",""language"":""auto"",""retainedAudioSources"":[],
            ""appVersion"":""0.1.0""}")!.AsObject();
        var r = SessionMigrator.Migrate(v3, self: null);
        Assert.Equal(4, r.Session.SchemaVersion);
        Assert.Null(r.SynthesizedMeta);
    }

    [Fact]
    public void Rejects_future_version()
        => Assert.Throws<NotSupportedException>(() =>
               SessionMigrator.Migrate(JsonNode.Parse("{\"schemaVersion\":5}")!.AsObject(), self: null));

    [Fact]
    public void V3_to_v4_defaults_activeVersion_v1_and_empty_versions()
    {
        var v3 = JsonNode.Parse(@"{""schemaVersion"":3,""id"":""x"",""app"":""Webex"",
            ""startedAtUtc"":""2026-07-02T14:32:05Z"",""durationMs"":0,""sources"":[],
            ""model"":"""",""backend"":"""",""language"":""auto"",""retainedAudioSources"":[],
            ""appVersion"":""0.1.0""}")!.AsObject();
        var r = SessionMigrator.Migrate(v3, self: null);
        Assert.Equal(4, r.Session.SchemaVersion);
        Assert.Equal("v1", r.Session.ActiveVersion);
        Assert.Empty(r.Session.Versions);
        Assert.Null(r.SynthesizedMeta);          // v3 -> v4 synthesizes nothing (additive fields)
    }

    [Fact]
    public async Task Store_migrates_v2_folder_and_writes_meta_json()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        string sessionPath = Path.Combine(dir, "session.json");
        string metaPath = Path.Combine(dir, "meta.json");
        try
        {
            Directory.CreateDirectory(dir);
            var v2 = V1(audioRetained: true);
            v2["schemaVersion"] = 2;
            v2.Remove("audioRetained");
            v2["retainedAudioSources"] = new JsonArray("Local", "Remote");
            await File.WriteAllTextAsync(sessionPath, v2.ToJsonString());

            var self = new SessionParticipant { Id = "p-self", Name = "Sam", Side = SourceKind.Local, IsSelf = true };
            var migrated = await new SessionStore(sessionPath).ReadAsync(self, default);

            Assert.Equal(4, migrated!.SchemaVersion);
            Assert.True(File.Exists(metaPath));                              // meta.json synthesized on disk
            var meta = await new MetadataStore(metaPath).LoadAsync(default);
            Assert.Equal("Old session", meta!.Title);

            string rewritten = await File.ReadAllTextAsync(sessionPath);     // session.json rewritten at v4
            Assert.Contains("\"schemaVersion\": 4", rewritten);
            Assert.DoesNotContain("audioRetained", rewritten);
            Assert.DoesNotContain("\"title\"", rewritten);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
