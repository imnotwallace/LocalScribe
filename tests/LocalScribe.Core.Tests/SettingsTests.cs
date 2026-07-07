// tests/LocalScribe.Core.Tests/SettingsTests.cs
using System.Text.Json.Nodes;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class SettingsTests
{
    [Fact]
    public async Task Fresh_install_returns_keep_default_and_v3_shape()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            var s = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.Equal(3, s.SchemaVersion);
            Assert.Equal("keep", s.AudioRetention);
            Assert.Equal(AudioFormat.Flac, s.AudioFormat);
            Assert.False(s.AutoDetect.Enabled);
            Assert.True(s.Overlay.ExcludeFromCapture);
            Assert.True(s.Privacy.ExcludeWindowsFromCapture);   // v3 default
            Assert.Null(s.ConsentNotice);                       // absence = not yet acknowledged
            Assert.Equal("PRIVILEGED & CONFIDENTIAL", s.DocxFooterText);   // additive v3 default (no schema bump)
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Roundtrips_v3_with_spec_wire_values()
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
            Assert.Contains("\"excludeWindowsFromCapture\": true", json);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public void Migration_v1_to_v3_chain_preserves_retention_flips_autodetect_adds_sections()
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
        Assert.Equal(3, s.SchemaVersion);
        Assert.Equal("days:30", s.AudioRetention);      // preserved, NOT flipped to keep
        Assert.False(s.AutoDetect.Enabled);             // flipped by v1->v2
        Assert.Equal(AudioFormat.Flac, s.AudioFormat);  // v2 addition at default
        Assert.True(s.Overlay.ExcludeFromCapture);      // v2 addition at default
        Assert.Equal(RemoteMode.Auto, s.Remote.Mode);   // v2 addition at default
        Assert.True(s.Privacy.ExcludeWindowsFromCapture);   // v3 addition at default
        Assert.Null(s.ConsentNotice);                   // migration never fabricates consent
        Assert.Equal("Ctrl+Alt+R", s.Hotkeys.StartStop);    // v1 field survives the chain
    }

    [Fact]
    public async Task Store_migrates_v1_file_on_load_and_rewrites_v3()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"audioRetention\":\"never\"}");
            var s = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.Equal(3, s.SchemaVersion);
            Assert.Equal("never", s.AudioRetention);
            Assert.Contains("\"schemaVersion\": 3", await File.ReadAllTextAsync(path));
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Store_migrates_v2_file_adds_privacy_leaves_consent_absent_and_rewrites_v3()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path,
                "{\"schemaVersion\":2,\"audioRetention\":\"days:30\",\"autoDetect\":{\"enabled\":false}}");

            var s = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.Equal(3, s.SchemaVersion);
            Assert.Equal("days:30", s.AudioRetention);          // v2 content preserved
            Assert.True(s.Privacy.ExcludeWindowsFromCapture);   // additive default
            Assert.Null(s.ConsentNotice);                       // never synthesized

            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"schemaVersion\": 3", json);
            Assert.DoesNotContain("consentNotice", json);       // field-absence = unacknowledged
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task ConsentNotice_roundtrips_and_is_omitted_when_null()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var acknowledged = new Settings
            {
                ConsentNotice = new ConsentSetting
                {
                    AcknowledgedAtUtc = new DateTimeOffset(2026, 7, 3, 10, 0, 0, TimeSpan.Zero),
                    AppVersion = "0.4.0",
                },
            };
            await store.SaveAsync(acknowledged, default);
            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"acknowledgedAtUtc\": \"2026-07-03T10:00:00Z\"", json);
            Assert.Contains("\"appVersion\": \"0.4.0\"", json);

            var back = await store.LoadOrDefaultAsync(default);
            Assert.Equal(acknowledged.ConsentNotice, back.ConsentNotice);

            await store.SaveAsync(new Settings(), default);
            Assert.DoesNotContain("consentNotice", await File.ReadAllTextAsync(path));
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
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":4}");
            await Assert.ThrowsAsync<NotSupportedException>(
                () => new SettingsStore(path).LoadOrDefaultAsync(default));
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task SectionGapMs_defaults_to_5000_and_roundtrips()
    {
        Assert.Equal(5000, new Settings().SectionGapMs);

        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            await new SettingsStore(path).SaveAsync(new Settings { SectionGapMs = 7000 }, default);
            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"sectionGapMs\": 7000", json);
            var back = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.Equal(7000, back.SectionGapMs);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task DocxFooterText_defaults_and_roundtrips()
    {
        Assert.Equal("PRIVILEGED & CONFIDENTIAL", new Settings().DocxFooterText);

        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "settings.json");
        try
        {
            await new SettingsStore(path).SaveAsync(new Settings { DocxFooterText = "CONFIDENTIAL - Smith LLP" }, default);
            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"docxFooterText\": \"CONFIDENTIAL - Smith LLP\"", json);
            var back = await new SettingsStore(path).LoadOrDefaultAsync(default);
            Assert.Equal("CONFIDENTIAL - Smith LLP", back.DocxFooterText);
        }
        finally { CleanParent(path); }
    }

    private static void CleanParent(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
