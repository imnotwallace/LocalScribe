using System.Text.Json;
namespace LocalScribe.Core.Storage;

/// <summary>Atomic JSON file IO through the shared options (via AtomicFile).</summary>
public static class JsonFile
{
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return default;
        // Shared read: tolerate a concurrent AtomicFile replace of this file (see
        // AtomicFile.ReadAllTextSharedAsync) instead of throwing a spurious sharing violation.
        string text = await AtomicFile.ReadAllTextSharedAsync(path, ct);
        return JsonSerializer.Deserialize<T>(text, LocalScribeJson.Options);
    }

    public static Task WriteAsync<T>(string path, T value, CancellationToken ct)
        => AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(value, LocalScribeJson.Options), ct);
}
