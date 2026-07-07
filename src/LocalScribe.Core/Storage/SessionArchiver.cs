using System.IO.Compression;
namespace LocalScribe.Core.Storage;

/// <summary>Adds the files of one session folder to a zip (Stage 6.3, design 3.2). Read-only: opens
/// each file for read and streams it into the archive; writes no temp files into the session folder.
/// Archives ONLY files that exist (audio may be absent; edits/speakers/summary are absent-until-used).
/// Audio is stored NoCompression (FLAC/WAV are already compressed); text/JSON use Optimal. Entry order
/// is Ordinal-sorted for determinism.</summary>
public static class SessionArchiver
{
    public static async Task AddSessionFolderAsync(ZipArchive zip, string sessionDir,
        string entryPrefix, CancellationToken ct)
    {
        if (!Directory.Exists(sessionDir)) return;
        foreach (string file in Directory.EnumerateFiles(sessionDir).OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string name = Path.GetFileName(file);
            var level = IsAudio(name) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            var entry = zip.CreateEntry(entryPrefix + name, level);
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var dst = entry.Open();
            await src.CopyToAsync(dst, ct);
        }
    }

    private static bool IsAudio(string name)
        => name.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
}
