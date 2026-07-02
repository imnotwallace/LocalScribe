using System.Text.Json;
namespace LocalScribe.Core.Storage;

/// <summary>Atomic JSON file IO through the shared options (via AtomicFile).</summary>
public static class JsonFile
{
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return default;
        string text = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize<T>(text, LocalScribeJson.Options);
    }

    public static Task WriteAsync<T>(string path, T value, CancellationToken ct)
        => AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(value, LocalScribeJson.Options), ct);
}
