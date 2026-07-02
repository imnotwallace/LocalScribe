// src/LocalScribe.Core/Storage/SettingsStore.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Reads/writes settings.json (spec section 7). Fresh install -> defaults (keep); migrates v1;
/// rejects a newer schema.</summary>
public sealed class SettingsStore
{
    public const int Version = 2;
    private readonly string _path;
    public SettingsStore(string settingsJsonPath) => _path = settingsJsonPath;

    public Task SaveAsync(Settings settings, CancellationToken ct)
        => JsonFile.WriteAsync(_path, settings with { SchemaVersion = Version }, ct);

    public async Task<Settings> LoadOrDefaultAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(_path, ct);
        if (obj is null) return new Settings();                       // fresh install -> keep default

        int v = SchemaGuard.ReadVersion(obj);
        SchemaGuard.RejectIfNewer(v, Version, "settings.json");
        if (v < Version)
        {
            var migrated = SettingsMigrator.Migrate(obj);
            await SaveAsync(migrated, ct);
            return migrated;
        }
        return await JsonFile.ReadAsync<Settings>(_path, ct) ?? new Settings();
    }
}
