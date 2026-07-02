namespace LocalScribe.Core.Storage;

/// <summary>The one atomic-write primitive: write a sibling ".tmp" then move into place, so a
/// crash never leaves a half-written file. Every whole-file write (JSON truth AND readable
/// projections) goes through here.</summary>
public static class AtomicFile
{
    public static async Task WriteAllTextAsync(string path, string text, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, text, ct);
        File.Move(tmp, path, overwrite: true);
    }
}
