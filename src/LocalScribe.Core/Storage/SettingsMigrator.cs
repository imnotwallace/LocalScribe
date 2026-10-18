// src/LocalScribe.Core/Storage/SettingsMigrator.cs
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>settings.json migration chain (spec section 7).
/// v1 -> v2: adds new sections at defaults, disables auto-detect, preserves an explicitly
/// stored audioRetention. v2 -> v3 (Stage 4): adds privacy at defaults; consentNotice is
/// deliberately NOT synthesized - field absence means "not yet acknowledged" (design 6.3).
/// v3/v4/v5 -> v6 (remote inference, design 2026-10-18): the inference section is additive and
/// its record defaults are local, so nothing is synthesized (the SectionGapMs precedent); v4 and
/// v5 were pre-release builds of the same feature. The one real step: an "apiKey" stored in
/// PLAINTEXT by a v5 build is encrypted into "apiKeyProtected" and the plaintext member removed,
/// so a re-save can never write it back.
/// Deserializes into the typed model so the shared options apply on re-save
/// (migration re-serialization rule).</summary>
public static class SettingsMigrator
{
    /// <param name="protectKey">The v5 -> v6 key encryption; null = DPAPI. A seam so the
    /// migration logic is testable without a Windows user profile behind it.</param>
    public static Settings Migrate(JsonObject raw, Func<string?, string?>? protectKey = null)
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
        if (v <= 2)
        {
            raw["privacy"] ??= new JsonObject { ["excludeWindowsFromCapture"] = true };
            // consentNotice intentionally absent: migration must not fabricate an acknowledgment.
            raw["schemaVersion"] = 3;
        }
        if (v <= 5)
        {
            if (raw["inference"] is JsonObject inference)
                foreach (string section in new[] { "assistant", "transcription" })
                    if (inference[section] is JsonObject endpoint)
                        ProtectPlaintextKey(endpoint, protectKey ?? ApiKeyProtection.Protect);
            raw["schemaVersion"] = 6;
        }
        return raw.Deserialize<Settings>(LocalScribeJson.Options)!;
    }

    private static void ProtectPlaintextKey(JsonObject endpoint, Func<string?, string?> protect)
    {
        string? plaintext = endpoint["apiKey"] is JsonValue v && v.TryGetValue(out string? k) ? k : null;
        endpoint.Remove("apiKey");
        if (string.IsNullOrWhiteSpace(plaintext)) return;
        // If encryption itself fails the key is DROPPED - re-entering it in Settings is the cost.
        // Keeping the plaintext, or failing the whole settings load (and with it app startup),
        // would both be worse.
        try { endpoint["apiKeyProtected"] ??= protect(plaintext); }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or PlatformNotSupportedException) { }
    }
}
