// tests/LocalScribe.Core.Tests/SettingsTests.cs
using System.Text.Json.Nodes;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class SettingsTests
{
    [Fact]
    public async Task Fresh_install_returns_keep_default_and_v2_shape()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            var s = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.Equal(2, s.SchemaVersion);
            Assert.Equal("keep", s.AudioRetention);
            Assert.Equal(AudioFormat.Flac, s.AudioFormat);
            Assert.False(s.AutoDetect.Enabled);
            Assert.True(s.Overlay.ExcludeFromCapture);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Roundtrips_v2_with_spec_wire_values()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            await new SettingsStore(path).SaveAsync(new Settings(), default);
            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"audioRetention\": \"keep\"", json);
            Assert.Contains("\"audioFormat\": \"flac\"", json);
            Assert.Contains("\"backend\": \"auto\"", json);
            Assert.Contains("\"mode\": \"followDefault\"", json);   // mic
            Assert.Contains("\"startStop\": \"Ctrl+Alt+R\"", json);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public void Migration_v1_to_v2_preserves_retention_flips_autodetect_adds_sections()
    {
        var v1 = JsonNode.Parse(@"{
            ""schemaVersion"": 1,
            ""storageRoot"": ""%USERPROFILE%/LocalScribe"",
            ""audioRetention"": ""days:30"",
            ""model"": ""auto"", ""backend"": ""auto"", ""language"": ""auto"",
            ""autoDetect"": { ""enabled"": true, ""apps"": [""Teams"",""Zoom"",""Webex""] },
            ""hotkeys"": { ""startStop"": ""Ctrl+Alt+R"", ""pause"": ""Ctrl+Alt+P"" },
            ""timestamps"": ""relative"", ""recordingIndicator"": true, ""launchAtLogin"": true,
            ""logging"": { ""level"": ""info"", ""includeTranscriptText"": false }
        }")!.AsObject();

        var s = SettingsMigrator.Migrate(v1);
        Assert.Equal(2, s.SchemaVersion);
        Assert.Equal("days:30", s.AudioRetention);      // preserved, NOT flipped to keep
        Assert.False(s.AutoDetect.Enabled);             // flipped
        Assert.Equal(AudioFormat.Flac, s.AudioFormat);  // added at default
        Assert.True(s.Overlay.ExcludeFromCapture);      // added at default
        Assert.Equal(RemoteMode.Auto, s.Remote.Mode);   // added at default
    }

    [Fact]
    public async Task Store_migrates_v1_file_on_load_and_rewrites_v2()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"audioRetention\":\"never\"}");
            var s = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.Equal(2, s.SchemaVersion);
            Assert.Equal("never", s.AudioRetention);
            Assert.Contains("\"schemaVersion\": 2", await File.ReadAllTextAsync(path));
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Rejects_newer_settings_version()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":3}");
            await Assert.ThrowsAsync<NotSupportedException>(
                () => new SettingsStore(path).LoadOrDefaultAsync(default));
        }
        finally { CleanParent(path); }
    }

    private static void CleanParent(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
