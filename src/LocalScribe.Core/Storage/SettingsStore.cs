// src/LocalScribe.Core/Storage/SettingsStore.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes settings.json (spec section 7). Fresh install -> defaults (keep); migrates v1/v2;
/// rejects a newer schema.</summary>
public sealed class SettingsStore
{
    public const int Version = 3;
    private readonly string _path;
    public SettingsStore(string settingsJsonPath) => _path = settingsJsonPath;

    public Task SaveAsync(Settings settings, CancellationToken ct)
        => JsonFile.WriteAsync(_path, settings with { SchemaVersion = Version }, ct);

    public Task<Settings> LoadOrDefaultAsync(CancellationToken ct)
        => LoadOrDefaultAsync(persistMigration: true, ct);

    /// <summary>persistMigration:false computes the SAME in-memory migration (schema-current
    /// settings returned) but skips the SaveAsync write-migrate - the MCP read-only server's path
    /// (spec: structural read-only enforcement; the MCP process must never write-migrate settings.json,
    /// which could race a running App).</summary>
    public async Task<Settings> LoadOrDefaultAsync(bool persistMigration, CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return new Settings();                       // fresh install -> keep default

        int v = SchemaGuard.ReadVersion(obj);
        SchemaGuard.RejectIfNewer(v, Version, "settings.json");
        if (v < Version)
        {
            var migrated = SettingsMigrator.Migrate(obj);
            if (persistMigration)
                await SaveAsync(migrated, ct);
            return migrated;
        }
        return await JsonFile.ReadAsync<Settings>(_path, ct) ?? new Settings();
    }
}
