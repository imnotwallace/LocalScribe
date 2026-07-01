// src/LocalScribe.Core/Storage/SettingsMigrator.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>settings.json v1 -> v2 (spec section 7). Adds new sections at defaults, disables auto-detect,
/// preserves an explicitly stored audioRetention. Deserializes into the typed model so the shared
/// options apply on re-save (migration re-serialization rule).</summary>
public static class SettingsMigrator
{
    public static Settings Migrate(JsonObject raw)
    {
        int v = SchemaGuard.ReadVersion(raw);
        SchemaGuard.RejectIfNewer(v, SettingsStore.Version, "settings.json");

        if (v <= 1)
        {
            raw["audioFormat"] ??= "flac";
            raw["self"] ??= new JsonObject { ["name"] = "" };
            raw["remote"] ??= new JsonObject { ["mode"] = "auto" };
            raw["mic"] ??= new JsonObject { ["mode"] = "followDefault" };
            raw["overlay"] ??= new JsonObject
            {
                ["enabled"] = true, ["showSessionName"] = false,
                ["showLevelMeter"] = true, ["excludeFromCapture"] = true,
            };
            raw["vocabulary"] ??= new JsonObject { ["terms"] = new JsonArray(), ["corrections"] = new JsonObject() };

            if (raw["autoDetect"] is JsonObject ad) ad["enabled"] = false;
            else raw["autoDetect"] = new JsonObject { ["enabled"] = false, ["apps"] = new JsonArray("Teams", "Zoom", "Webex") };

            // audioRetention preserved verbatim (fresh installs never reach the migrator).
            raw["schemaVersion"] = 2;
        }
        return raw.Deserialize<Settings>(LocalScribeJson.Options)!;
    }
}
