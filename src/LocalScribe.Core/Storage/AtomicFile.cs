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
        // File.Move(overwrite) can transiently fail with UnauthorizedAccessException/IOException when
        // another process briefly holds the temp or destination file - typically Windows Defender or the
        // Search indexer scanning a just-written file. That becomes likely under heavy concurrent writes
        // (Fix 2026-07-08: several sessions can now finalize at once on background tasks). A transient
        // scanner lock must never fail a whole write, so retry with a short backoff before giving up; a
        // genuine, persistent access error still surfaces after the last attempt.
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException && attempt < 9)
            {
                await Task.Delay(20 * (attempt + 1), ct);
            }
        }
    }
}
