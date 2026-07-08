// tests/LocalScribe.Core.Tests/MicSnapshotTests.cs
using System.Text.Json;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.Core.Tests;

public class MicSnapshotTests
{
    [Fact]
    public void FellBackToDefault_DefaultsFalse_AndRoundTrips()
    {
        Assert.False(new MicSnapshot().FellBackToDefault);

        var snap = new MicSnapshot
        { Mode = MicMode.FollowDefault, Name = "Default Mic", FellBackToDefault = true };
        string json = JsonSerializer.Serialize(snap, LocalScribeJson.Options);
        var back = JsonSerializer.Deserialize<MicSnapshot>(json, LocalScribeJson.Options)!;
        Assert.True(back.FellBackToDefault);
        Assert.Equal("Default Mic", back.Name);
    }

    [Fact]
    public void PreExistingJson_WithoutFlag_LoadsAsFalse()
    {
        // A v3 session.json written before this field existed must still load (additive field).
        const string legacy = """{"mode":"pinned","id":"id-1","name":"Studio Mic"}""";
        var back = JsonSerializer.Deserialize<MicSnapshot>(legacy, LocalScribeJson.Options)!;
        Assert.Equal(MicMode.Pinned, back.Mode);
        Assert.False(back.FellBackToDefault);
    }
}
