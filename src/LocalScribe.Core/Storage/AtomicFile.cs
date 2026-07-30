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

    /// <summary>Read a whole text file with a share mode that tolerates a concurrent atomic
    /// replace. <see cref="WriteAllTextAsync"/>'s File.Move(overwrite) holds a DELETE-granted
    /// handle on the destination for the instant it renames the ".tmp" into place; a plain
    /// File.ReadAllTextAsync opens with FileShare.Read only, so its open is rejected with
    /// "used by another process" the moment it overlaps that move (only under real concurrency -
    /// e.g. a fire-and-forget load racing a save on a busy machine). FileShare.ReadWrite | Delete
    /// lets the read coexist: the rename is atomic, so it snapshots either the whole old file or
    /// the whole new one, never a torn read. Mirrors McpConsentStore.ReadCurrentAsync and
    /// TranscriptStore, which already read this way; the writer side is already covered by the
    /// File.Move retry above.</summary>
    public static async Task<string> ReadAllTextSharedAsync(string path, CancellationToken ct)
    {
        await using var s = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        using var r = new StreamReader(s);
        return await r.ReadToEndAsync(ct);
    }

    public static async Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct);
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
