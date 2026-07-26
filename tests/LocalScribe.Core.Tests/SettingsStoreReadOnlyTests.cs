// tests/LocalScribe.Core.Tests/SettingsStoreReadOnlyTests.cs
using LocalScribe.Core.Storage;

/// <summary>Fix pass 1 (mcp-server review): pins the persistMigration:false path on SettingsStore
/// so the MCP server never write-migrates the user's settings.json (could race a running App).
/// Same temp-root pattern as ReadOnlyProjectionTests; asserts on file BYTES so a regression that
/// brought the write back would actually fail this.</summary>
public sealed class SettingsStoreReadOnlyTests
{
    private const string LegacyV2Json =
        "{\"schemaVersion\":2,\"audioRetention\":\"days:30\",\"autoDetect\":{\"enabled\":false}}";

    [Fact]
    public async Task Legacy_settings_are_migrated_in_memory_without_writing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, LegacyV2Json);
            byte[] before = await File.ReadAllBytesAsync(path);

            var s = await new SettingsStore(path).LoadOrDefaultAsync(persistMigration: false, default);

            Assert.Equal(SettingsStore.Version, s.SchemaVersion);
            Assert.Equal("days:30", s.AudioRetention);          // v2 content preserved through migration
            Assert.True(s.Privacy.ExcludeWindowsFromCapture);   // v3 addition at default
            Assert.Equal(before, await File.ReadAllBytesAsync(path));   // NOT rewritten
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Legacy_settings_are_still_write_migrated_by_default()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, LegacyV2Json);
            byte[] before = await File.ReadAllBytesAsync(path);

            var s = await new SettingsStore(path).LoadOrDefaultAsync(default);   // existing single-arg overload

            Assert.Equal(SettingsStore.Version, s.SchemaVersion);
            byte[] after = await File.ReadAllBytesAsync(path);
            Assert.NotEqual(before, after);                                     // rewritten
            Assert.Contains("\"schemaVersion\": 3",
                System.Text.Encoding.UTF8.GetString(after));
        }
        finally { CleanParent(path); }
    }

    private static void CleanParent(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
