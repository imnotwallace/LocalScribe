using System.Security.Cryptography;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Builds manifest.json for one transcript version (Tier 1 T1-7, spec 2026-08-05
/// :146-153): SHA-256 + size + mtime for session.json, meta.json and the version's
/// transcript.jsonl / edits.json / speakers.json, plus every retained audio leg on disk and the
/// sample ranges AlignedAudioWriter fabricated inside it.
///
/// This does NOT re-open the 2026-08-04 ruling that recorded audio is never hashed AT EXPORT TIME
/// (transcript-export-scope-dialog-design :78). Audio is hashed at FINALIZE, once; every later
/// refresh carries the value forward on a size+mtime match, and the export path only reads the
/// stored number. A reviewer seeing "SHA-256 over a FLAC" here should read this paragraph before
/// flagging it.
///
/// COST RULING (Tier 1 T1-7): the first hash of a leg that has never been sealed happens only when
/// the caller passes sealAudio:true - the live finalize. Every other caller (the launch-time
/// recovery scan, "Regenerate all", every overlay write) passes false, so opening the app after
/// this ships does NOT retro-hash the library. The spec (:146-147) asks for a seal at finalize
/// refreshed after overlay writes; it never asked for a retroactive whole-library hash, and such a
/// hash would be unbounded, un-cancellable and unconsented.</summary>
public static class ManifestBuilder
{
    /// <summary>Compose the manifest without writing it. nowUtc comes from the caller's injected
    /// TimeProvider - never DateTime.UtcNow. <paramref name="sealAudio"/> is the cost gate above:
    /// REQUIRED rather than defaulted, because a silent default is exactly how the recovery scan
    /// would end up hashing gigabytes nobody asked it to.</summary>
    public static async Task<SessionManifest> BuildAsync(StoragePaths paths, string sessionId,
        string versionId, DateTimeOffset nowUtc,
        IReadOnlyDictionary<SourceKind, FabricatedSilenceRecord>? fabricated, bool sealAudio,
        CancellationToken ct)
    {
        string sessionDir = paths.SessionDir(sessionId);
        var previous = await new ManifestStore(paths.ManifestJson(sessionId, versionId)).ReadAsync(ct);
        var previousByName = previous is null
            ? new Dictionary<string, ManifestFile>(StringComparer.Ordinal)
            : previous.Files.ToDictionary(f => f.Name, StringComparer.Ordinal);

        // Audio is SESSION-level: local.flac is the same bytes whichever version is being sealed.
        // A version created by re-transcription starts with no manifest of its own, so it INHERITS
        // the session-root seal's audio entry rather than re-hashing - REJECTED: hashing per
        // version, which multiplies the one affordable hash by the version count for zero new
        // information, and would leave a v2 export with no audio hashes at all under the cost gate.
        var rootByName = previousByName;
        if (versionId != TranscriptVersions.Root)
        {
            var root = await new ManifestStore(paths.ManifestJson(sessionId)).ReadAsync(ct);
            rootByName = root is null
                ? new Dictionary<string, ManifestFile>(StringComparer.Ordinal)
                : root.Files.ToDictionary(f => f.Name, StringComparer.Ordinal);
        }

        var files = new List<ManifestFile>();

        // Text truth: always re-hashed. These are kilobytes, and an overlay write is exactly the
        // event a stale hash would hide.
        foreach (string path in new[]
                 {
                     paths.SessionJson(sessionId), paths.MetaJson(sessionId),
                     paths.TranscriptJsonl(sessionId, versionId),
                     paths.EditsJson(sessionId, versionId),
                     paths.SpeakersJson(sessionId, versionId),
                 })
        {
            if (!File.Exists(path)) continue;   // edits/speakers are absent-until-used
            files.Add(await SealAsync(sessionDir, path, ct));
        }

        // Retained audio: considered whenever the FILE EXISTS, deliberately NOT gated on
        // SessionRecord.RetainedAudioSources. A leg on disk that no manifest mentions is precisely
        // the gap this feature closes. Whether it is HASHED is a separate question - see the cost
        // gate below.
        foreach (var kind in new[] { SourceKind.Local, SourceKind.Remote })
            foreach (var format in new[] { AudioFormat.Flac, AudioFormat.Wav })
            {
                string path = paths.AudioFile(sessionId, kind, format);
                if (!File.Exists(path)) continue;
                string name = Relative(sessionDir, path);
                var info = new FileInfo(path);
                if (!previousByName.TryGetValue(name, out var prior)) rootByName.TryGetValue(name, out prior);

                // Carry-forward: same size AND same mtime means the bytes did not move, so reuse
                // the whole entry - hash and fabricated ranges together. Not re-hashing a multi-GB
                // FLAC on every saved correction is what makes a per-overlay refresh affordable.
                bool unchanged = prior is not null
                    && prior.SizeBytes == info.Length
                    && prior.ModifiedUtc == new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);

                var silence = fabricated is not null && fabricated.TryGetValue(kind, out var rec)
                    ? rec
                    : null;

                // The cost gate (see the class doc). It bites ONLY on a leg that has never been
                // sealed: a leg whose seal exists but whose bytes MOVED is always re-hashed, because
                // that is precisely the event a seal exists to catch and it is rare. REJECTED:
                // sealing an unhashed leg with an empty or inherited hash, which would certify bytes
                // nobody read - the file is simply left out, and Verify integrity then makes no
                // claim about it rather than a false one.
                if (!unchanged && prior is null && !sealAudio) continue;

                if (unchanged && silence is null) { files.Add(prior!); continue; }

                var sealedFile = unchanged
                    ? prior! with { }                                  // reuse the hash we already have
                    : await SealAsync(sessionDir, path, ct);
                files.Add(silence is not null
                    ? sealedFile with
                    {
                        SampleRate = silence.SampleRate,
                        FabricatedSilenceKnown = true,
                        FabricatedSilence = silence.Spans,
                    }
                    // No writer reported ranges for this leg: carry the prior claim if there was
                    // one, otherwise say UNKNOWN. Never fabricate an empty list, which would read
                    // as "we checked and there is none".
                    : sealedFile with
                    {
                        SampleRate = prior?.SampleRate ?? 0,
                        FabricatedSilenceKnown = prior?.FabricatedSilenceKnown ?? false,
                        FabricatedSilence = prior?.FabricatedSilence ?? [],
                    });
            }

        return new SessionManifest
        {
            SessionId = sessionId,
            VersionId = versionId,
            WrittenAtUtc = nowUtc,
            Files = files.OrderBy(f => f.Name, StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>Build and persist atomically. Never throws for a missing session folder - a
    /// manifest over nothing is simply an empty Files list.</summary>
    public static async Task WriteAsync(StoragePaths paths, string sessionId, string versionId,
        DateTimeOffset nowUtc, IReadOnlyDictionary<SourceKind, FabricatedSilenceRecord>? fabricated,
        bool sealAudio, CancellationToken ct)
    {
        var manifest = await BuildAsync(paths, sessionId, versionId, nowUtc, fabricated, sealAudio, ct);
        await new ManifestStore(paths.ManifestJson(sessionId, versionId)).SaveAsync(manifest, ct);
    }

    private static async Task<ManifestFile> SealAsync(string sessionDir, string path,
        CancellationToken ct)
    {
        var info = new FileInfo(path);
        return new ManifestFile
        {
            Name = Relative(sessionDir, path),
            Sha256 = await HashAsync(path, ct),
            SizeBytes = info.Length,
            ModifiedUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
        };
    }

    /// <summary>'/'-separated session-folder-relative name, matching SessionArchiver's zip entries
    /// so "versions/v2-.../transcript.jsonl" reads identically in both artefacts.</summary>
    private static string Relative(string sessionDir, string path)
        => Path.GetRelativePath(sessionDir, path).Replace('\\', '/');

    /// <summary>Streaming SHA-256, the AudioImporter.CopyWithSha256Async idiom with the copy half
    /// dropped - lowercase hex via Convert.ToHexStringLower, 64 KiB buffer, so a multi-GB FLAC
    /// never lands in memory. FileShare.ReadWrite | Delete, NOT FileShare.Read: the importer's
    /// share mode is safe only because it reads a user file no LocalScribe process holds, whereas
    /// this reads inside a session folder whose capture pipeline may still hold local.flac and
    /// transcript.jsonl open for WRITING. That exact defect has been fixed twice in this repo
    /// (SessionArchiver); Delete additionally tolerates an AtomicFile replace mid-read.
    /// PUBLIC so IntegrityVerifier re-reads files EXACTLY as the sealer wrote them - one
    /// implementation, so a verifier bug can never disagree with the seal about how a file is
    /// read (there is no InternalsVisibleTo in this repo).</summary>
    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var src = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 16, useAsync: true);
        var buf = new byte[1 << 16];
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0) sha.AppendData(buf, 0, n);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
