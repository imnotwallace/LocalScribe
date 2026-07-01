using System.Text.Json.Nodes;
namespace LocalScribe.Core.Storage;

/// <summary>Shared schema-version reading + forward-incompatibility guard (spec section Schema-version policy).</summary>
public static class SchemaGuard
{
    public static async Task<JsonObject?> ReadObjectAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        string text = await File.ReadAllTextAsync(path, ct);
        return JsonNode.Parse(text)?.AsObject();
    }

    public static int ReadVersion(JsonObject obj)
        => obj.TryGetPropertyValue("schemaVersion", out JsonNode? v) && v is not null
            ? v.GetValue<int>()
            : 1;

    public static void RejectIfNewer(int fileVersion, int maxSupported, string fileKind)
    {
        if (fileVersion > maxSupported)
            throw new NotSupportedException(
                $"{fileKind} schemaVersion {fileVersion} is newer than supported ({maxSupported})");
    }
}
