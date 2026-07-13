using System.Text.Json;
using System.Text.Json.Nodes;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Pure session.json migration v1 -> v2 -> v3 -> v4 (spec Schema-version policy). v2 -> v3
/// splits the user-owned fields into a synthesized meta.json. Finishes by deserializing the
/// mutated node into the typed model so the shared options (naming/null-omit) apply on re-save.</summary>
public static class SessionMigrator
{
    public sealed record MigrationResult(SessionRecord Session, SessionMeta? SynthesizedMeta);

    public static MigrationResult Migrate(JsonObject raw, SessionParticipant? self)
    {
        int version = SchemaGuard.ReadVersion(raw);
        SchemaGuard.RejectIfNewer(version, SessionStore.Version, "session.json");

        SessionMeta? synthesized = null;

        if (version <= 1)
        {
            MigrateV1ToV2(raw);
            version = 2;
        }
        if (version == 2)
        {
            synthesized = MigrateV2ToV3(raw, self);
            version = 3;
        }
        if (version == 3)
        {
            MigrateV3ToV4(raw);
            version = 4;
        }

        raw["schemaVersion"] = 4;
        var session = raw.Deserialize<SessionRecord>(LocalScribeJson.Options)!;
        return new MigrationResult(session, synthesized);
    }

    private static void MigrateV1ToV2(JsonObject o)
    {
        bool retained = o.TryGetPropertyValue("audioRetained", out JsonNode? ar) && ar is not null && ar.GetValue<bool>();
        var sources = o["sources"]?.AsArray() ?? new JsonArray();
        var retainedArr = new JsonArray();
        if (retained)
            foreach (JsonNode? s in sources)
                retainedArr.Add(s!.GetValue<string>());
        o["retainedAudioSources"] = retainedArr;
        o.Remove("audioRetained");
    }

    private static SessionMeta MigrateV2ToV3(JsonObject o, SessionParticipant? self)
    {
        string title = o.TryGetPropertyValue("title", out JsonNode? t) && t is not null ? t.GetValue<string>() : "";
        string appName = o.TryGetPropertyValue("app", out JsonNode? a) && a is not null ? a.GetValue<string>() : "Manual";
        Medium medium = Enum.TryParse(appName, out Medium m) ? m : Medium.Other;

        o.Remove("title");                                    // title relocates to meta.json
        o["devices"] = new JsonObject                          // unknown/legacy snapshot
        {
            ["mic"] = new JsonObject { ["mode"] = "followDefault", ["name"] = "legacy" },
            ["remote"] = new JsonObject { ["mode"] = "systemMix", ["fellBackToSystemMix"] = false },
        };

        return new SessionMeta
        {
            Title = title,
            Description = "",
            Medium = medium,
            MatterIds = [],
            Participants = self is null ? [] : [self],
            SummaryRef = null,
        };
    }

    /// <summary>v3 -> v4 (design 2026-07-13 section 3.1): versioned re-transcription. Old
    /// sessions read as activeVersion "v1" (the session root) with no recorded versions -
    /// exactly the typed defaults, written explicitly so a v4 file is self-describing.</summary>
    private static void MigrateV3ToV4(JsonObject o)
    {
        o["activeVersion"] = "v1";
        o["versions"] = new JsonArray();
    }
}
