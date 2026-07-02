// tests/LocalScribe.Core.Tests/JsonConventionsTests.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class JsonConventionsTests
{
    private sealed record Probe
    {
        public RemoteMode Remote { get; init; }
        public MicMode Mic { get; init; }
        public Medium Medium { get; init; }
        public Backend Backend { get; init; }
        public SourceKind Side { get; init; }
        public AudioFormat AudioFormat { get; init; }
        public DateTimeOffset When { get; init; }
        public string? Optional { get; init; }
    }

    [Fact]
    public void Enums_serialize_to_spec_wire_strings()
    {
        var p = new Probe
        {
            Remote = RemoteMode.PerProcess,
            Mic = MicMode.FollowDefault,
            Medium = Medium.InPerson,
            Backend = Backend.Cuda,
            Side = SourceKind.Remote,
            AudioFormat = AudioFormat.Flac,
            When = new DateTimeOffset(2026, 7, 2, 14, 32, 5, TimeSpan.Zero),
        };
        string json = JsonSerializer.Serialize(p, LocalScribeJson.Options);

        Assert.Contains("\"remote\": \"perProcess\"", json);
        Assert.Contains("\"mic\": \"followDefault\"", json);
        Assert.Contains("\"medium\": \"In-person\"", json);
        Assert.Contains("\"backend\": \"cuda\"", json);
        Assert.Contains("\"side\": \"Remote\"", json);
        Assert.Contains("\"audioFormat\": \"flac\"", json);
    }

    [Fact]
    public void DateTimeOffset_writes_utc_z_and_roundtrips()
    {
        var p = new Probe { When = new DateTimeOffset(2026, 7, 2, 14, 32, 5, TimeSpan.Zero) };
        string json = JsonSerializer.Serialize(p, LocalScribeJson.Options);
        Assert.Contains("\"when\": \"2026-07-02T14:32:05Z\"", json);

        var back = JsonSerializer.Deserialize<Probe>(json, LocalScribeJson.Options)!;
        Assert.Equal(p.When, back.When);
    }

    [Fact]
    public void Null_optional_is_omitted_and_property_names_are_camelCase()
    {
        var p = new Probe { Optional = null };
        string json = JsonSerializer.Serialize(p, LocalScribeJson.Options);
        Assert.DoesNotContain("optional", json);
        Assert.Contains("\"remote\":", json);   // camelCase property name present
        Assert.DoesNotContain("\"Remote\":", json);
    }

    [Fact]
    public void Unknown_fields_are_ignored_on_read()
    {
        string json = "{\"remote\":\"auto\",\"unknownField\":123}";
        var back = JsonSerializer.Deserialize<Probe>(json, LocalScribeJson.Options)!;
        Assert.Equal(RemoteMode.Auto, back.Remote);
    }
}
