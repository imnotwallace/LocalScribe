using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class SessionStoreTests
{
    private static SessionRecord Sample() => new()
    {
        // Spec 1.2 example: 06:32Z at +480 (Singapore) => local 14:32, matching the id.
        Id = "2026-07-02_1432_Webex_doe-intake",
        App = AppKind.Webex,
        StartedAtUtc = new DateTimeOffset(2026, 7, 2, 6, 32, 5, TimeSpan.Zero),
        EndedAtUtc = new DateTimeOffset(2026, 7, 2, 7, 9, 11, TimeSpan.Zero),
        TimeZoneId = "Singapore Standard Time",
        UtcOffsetMinutes = 480,
        DurationMs = 2226000,
        Sources = new[] { SourceKind.Local, SourceKind.Remote },
        Model = "small.en",
        WeightsFile = "ggml-small.en-q8_0.bin",
        Backend = "CUDA",
        Language = "auto",
        RetainedAudioSources = new[] { SourceKind.Local, SourceKind.Remote },
        Diarised = false,
        SegmentCount = 312,
        MarkerCount = 6,
        Recovered = false,
        AppVersion = "0.1.0",
        Devices = new DeviceSnapshot
        {
            Mic = new MicSnapshot { Mode = MicMode.FollowDefault, Id = "{0.0.1.00000000}.{guid}", Name = "Shure MV7" },
            Remote = new RemoteSnapshot { Mode = RemoteMode.PerProcess, App = "CiscoCollabHost.exe", FellBackToSystemMix = false },
        },
    };

    [Fact]
    public async Task Roundtrips_all_fields_at_v3()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "session.json");
        try
        {
            var store = new SessionStore(path);
            await store.SaveAsync(Sample(), default);
            var back = await store.ReadAsync(default);

            Assert.Equal(3, back!.SchemaVersion);
            Assert.Equal(AppKind.Webex, back.App);
            Assert.Equal("CUDA", back.Backend);                       // free-string actual, preserved
            Assert.Equal("ggml-small.en-q8_0.bin", back.WeightsFile); // provenance: the file that ran
            Assert.Equal("Singapore Standard Time", back.TimeZoneId);
            Assert.Equal(480, back.UtcOffsetMinutes);
            Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, back.RetainedAudioSources);
            Assert.Equal("CiscoCollabHost.exe", back.Devices.Remote.App);
            Assert.False(back.Devices.Remote.FellBackToSystemMix);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Written_json_uses_spec_shape()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "session.json");
        try
        {
            await new SessionStore(path).SaveAsync(Sample(), default);
            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"schemaVersion\": 3", json);
            Assert.Contains("\"app\": \"Webex\"", json);
            Assert.Contains("\"startedAtUtc\": \"2026-07-02T06:32:05Z\"", json);
            Assert.Contains("\"timeZoneId\": \"Singapore Standard Time\"", json);
            Assert.Contains("\"utcOffsetMinutes\": 480", json);
            Assert.Contains("\"mode\": \"perProcess\"", json);
            Assert.DoesNotContain("\"title\"", json);                  // title lives in meta.json (spec 1.2)
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Rejects_newer_schema_version()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "session.json");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":4}");
            await Assert.ThrowsAsync<NotSupportedException>(() => new SessionStore(path).ReadAsync(default));
        }
        finally { CleanParent(path); }
    }

    private static void CleanParent(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
