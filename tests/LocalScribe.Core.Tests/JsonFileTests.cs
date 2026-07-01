// tests/LocalScribe.Core.Tests/JsonFileTests.cs
using System.Text.Json.Nodes;
using LocalScribe.Core.Storage;

public class JsonFileTests
{
    private sealed record Doc { public int SchemaVersion { get; init; } public string Name { get; init; } = ""; }

    [Fact]
    public async Task Read_of_absent_file_returns_default()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "missing.json");
        Assert.Null(await JsonFile.ReadAsync<Doc>(path, default));
    }

    [Fact]
    public async Task Write_then_read_roundtrips_and_creates_directory()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "doc.json");
        try
        {
            await JsonFile.WriteAsync(path, new Doc { SchemaVersion = 3, Name = "x" }, default);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));           // temp cleaned up by the move
            var back = await JsonFile.ReadAsync<Doc>(path, default);
            Assert.Equal(3, back!.SchemaVersion);
            Assert.Equal("x", back.Name);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadVersion_defaults_to_1_when_absent()
    {
        Assert.Equal(1, SchemaGuard.ReadVersion(JsonNode.Parse("{}")!.AsObject()));
        Assert.Equal(3, SchemaGuard.ReadVersion(JsonNode.Parse("{\"schemaVersion\":3}")!.AsObject()));
    }

    [Fact]
    public void RejectIfNewer_throws_only_when_version_exceeds_supported()
    {
        SchemaGuard.RejectIfNewer(3, 3, "session.json");   // no throw
        var ex = Assert.Throws<NotSupportedException>(() => SchemaGuard.RejectIfNewer(4, 3, "session.json"));
        Assert.Contains("newer than supported", ex.Message);
    }
}
