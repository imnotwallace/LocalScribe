using System.IO.Compression;
namespace LocalScribe.Core.Storage;

/// <summary>Adds the files of one session folder to a zip (Stage 6.3, design 3.2). Read-only: opens
/// each file for read and streams it into the archive; writes no temp files into the session folder.
/// Archives ONLY files that exist (audio may be absent; edits/speakers/summary are absent-until-used).
/// Audio is stored NoCompression (FLAC/WAV are already compressed); text/JSON use Optimal. Entry order
/// is Ordinal-sorted for determinism.
///
/// ONE exclusion (voiceprint design 2026-07-25; final whole-branch review finding M2):
/// <c>embeddings.json</c>, at any depth. It holds raw per-cluster biometric vectors, it is DERIVED
/// data with no evidentiary role, and an export .zip is the one session artefact that routinely
/// leaves this machine - a copy riding along in every export would quietly outlive the voiceprint
/// purge that is supposed to be able to delete it. Nothing evidentiary is affected: audio,
/// transcripts, speaker names and every other file still ride along exactly as before.</summary>
public static class SessionArchiver
{
    public static async Task AddSessionFolderAsync(ZipArchive zip, string sessionDir,
        string entryPrefix, CancellationToken ct)
    {
        if (!Directory.Exists(sessionDir)) return;
        // Versioned re-transcription (design 2026-07-13 section 3.3): archive the WHOLE folder
        // tree so versions\vN-...\ rides along. Entry names are '/'-relative paths (zip
        // convention); a top-level file's relative path IS its file name, so pre-versioning
        // archives are byte-identical in shape.
        foreach (string file in Directory.EnumerateFiles(sessionDir, "*", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (IsExcludedVoiceData(file)) continue;
            string name = Path.GetRelativePath(sessionDir, file).Replace('\\', '/');
            var level = IsAudio(name) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
            var entry = zip.CreateEntry(entryPrefix + name, level);
            // FileShare.ReadWrite, NOT FileShare.Read (design 2026-08-03 section 11, same defect
            // Task 11 fixed in TranscriptStore): during a live recording the capture pipeline holds
            // audio.flac and transcript.jsonl open for writing, and FileShare.Read locks writers
            // out, so a .zip export mid-recording threw IOException. The archive this produces is a
            // point-in-time snapshot of a still-growing file - later bytes an in-flight writer adds
            // after this stream reads past them simply aren't in this entry, same as any other
            // snapshot of a live file. That's a completeness tradeoff, not a correctness one: it
            // never throws, and it never emits a corrupt/truncated-looking entry for bytes it did
            // read. .docx/.md exports already work mid-recording, so .zip does not get gated off
            // for in-progress sessions either - that would be inconsistent for no safety benefit.
            using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dst = entry.Open();
            await src.CopyToAsync(dst, ct);
        }
    }

    /// <summary>Raw per-cluster biometric vectors (see the class doc). Matched by file NAME, not by
    /// path, so every transcript version's own copy under <c>versions\</c> is excluded too.</summary>
    private static bool IsExcludedVoiceData(string filePath)
        => string.Equals(Path.GetFileName(filePath), "embeddings.json", StringComparison.OrdinalIgnoreCase);

    private static bool IsAudio(string name)
        => name.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
}
