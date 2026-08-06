using System.Globalization;
namespace LocalScribe.Core.Storage;

/// <summary>One file's verdict against the seal (Tier 1 T1-7). Missing outranks Changed in the
/// summary ordering below because a deleted evidentiary file is the graver finding.</summary>
public enum IntegrityStatus { Ok, Changed, Missing }

public sealed record IntegrityCheck(string Name, IntegrityStatus Status);

/// <summary>The outcome of "Verify integrity" for one transcript version (Tier 1 T1-7, spec
/// 2026-08-05 :143). SealedAtUtc null means there is NO manifest - reported as its own outcome and
/// never as a pass, because "nothing to check" and "everything checks out" are opposite claims and
/// a false assurance is the one thing this command must not produce.</summary>
public sealed record IntegrityReport(string SessionId, DateTimeOffset? SealedAtUtc,
    IReadOnlyList<IntegrityCheck> Checks)
{
    public bool Sealed => SealedAtUtc is not null;

    /// <summary>An unsealed session never passes - see the record doc.</summary>
    public bool Passed => Sealed && Checks.All(c => c.Status == IntegrityStatus.Ok);

    /// <summary>One InfoBar line. Failures are listed by NAME (Missing first, then Changed, each
    /// Ordinal-sorted) rather than counted, because "2 files changed" tells a solicitor nothing
    /// about whether the transcript or a stray projection moved. Invariant culture, like every
    /// other evidentiary string in this codebase.</summary>
    public string Summarize(string sessionTitle)
    {
        if (!Sealed)
            return string.Create(CultureInfo.InvariantCulture,
                $"\"{sessionTitle}\" has no integrity seal - it was recorded before integrity manifests existed, or its manifest.json was deleted. Nothing can be verified.");

        string stamp = SealedAtUtc!.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        if (Passed)
            return string.Create(CultureInfo.InvariantCulture,
                $"Integrity check passed for \"{sessionTitle}\": {Checks.Count} files match the seal written {stamp}.");

        var bad = Checks.Where(c => c.Status != IntegrityStatus.Ok)
            .OrderBy(c => c.Status == IntegrityStatus.Missing ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => c.Name + " " + c.Status.ToString().ToUpperInvariant());
        int ok = Checks.Count(c => c.Status == IntegrityStatus.Ok);
        return string.Create(CultureInfo.InvariantCulture,
            $"Integrity check FAILED for \"{sessionTitle}\": {string.Join("; ", bad)}. {ok} of {Checks.Count} files match the seal written {stamp}.");
    }
}

/// <summary>Re-hashes what manifest.json sealed and compares (Tier 1 T1-7, spec 2026-08-05 :143).
/// Walks the SEALED list and re-reads each named file through ManifestBuilder.HashAsync - one
/// hashing implementation, so a verifier bug can never disagree with the sealer about how a file is
/// read. REJECTED: calling ManifestBuilder.BuildAsync and diffing the two manifests, which would
/// CARRY FORWARD any audio entry whose size+mtime still match and hand back the sealed hash without
/// re-reading a byte - a verifier that trusts the seal it is checking verifies nothing. Takes no
/// clock: the report states when the SEAL was written, and the moment the check ran is not
/// persisted anywhere.</summary>
public static class IntegrityVerifier
{
    public static async Task<IntegrityReport> VerifyAsync(StoragePaths paths, string sessionId,
        string versionId, CancellationToken ct)
    {
        var sealedManifest = await new ManifestStore(paths.ManifestJson(sessionId, versionId)).ReadAsync(ct);
        if (sealedManifest is null) return new IntegrityReport(sessionId, null, []);

        var checks = new List<IntegrityCheck>();
        string sessionDir = paths.SessionDir(sessionId);
        foreach (var file in sealedManifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            string path = Path.Combine(sessionDir, file.Name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { checks.Add(new IntegrityCheck(file.Name, IntegrityStatus.Missing)); continue; }
            var info = new FileInfo(path);
            // Size first: a cheap, certain CHANGED verdict that skips hashing a multi-GB leg whose
            // length already disagrees with the seal.
            if (info.Length != file.SizeBytes)
            { checks.Add(new IntegrityCheck(file.Name, IntegrityStatus.Changed)); continue; }
            string actual = await ManifestBuilder.HashAsync(path, ct);
            checks.Add(new IntegrityCheck(file.Name,
                string.Equals(actual, file.Sha256, StringComparison.Ordinal)
                    ? IntegrityStatus.Ok : IntegrityStatus.Changed));
        }
        return new IntegrityReport(sessionId, sealedManifest.WrittenAtUtc, checks);
    }
}
