using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

/// <summary>"Verify integrity" (Tier 1 T1-7, spec 2026-08-05 :143). Re-hashes what the manifest
/// sealed and reports per file. The central product claim - that this is a faithful local record -
/// is unfalsifiable in BOTH directions without this: nothing could prove tampering, and nothing
/// could prove its absence either.</summary>
public sealed class IntegrityVerifierTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-integrity-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 10, 22, 0, TimeSpan.Zero);

    public IntegrityVerifierTests() { _paths = new StoragePaths(_root); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private async Task SealAsync(string id)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        File.WriteAllText(_paths.SessionJson(id), "{\"schemaVersion\":4,\"id\":\"" + id + "\"}");
        File.WriteAllText(_paths.MetaJson(id), "{\"schemaVersion\":3}");
        File.WriteAllText(_paths.TranscriptJsonl(id), "{\"seq\":0}\n");
        File.WriteAllText(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac), "AAAA");
        // sealAudio:true - this fixture stands in for the FINALIZE path, the only caller allowed to
        // take a leg's first hash (ManifestBuilder's cost gate). With false, local.flac would not be
        // in the manifest at all and the file counts below would be 3, not 4.
        await ManifestBuilder.WriteAsync(_paths, id, TranscriptVersions.Root, Now,
            fabricated: null, sealAudio: true, CancellationToken.None);
    }

    [Fact]
    public async Task An_untouched_session_passes_with_every_file_ok()
    {
        await SealAsync("s-clean");

        var report = await IntegrityVerifier.VerifyAsync(_paths, "s-clean", TranscriptVersions.Root,
            CancellationToken.None);

        Assert.True(report.Sealed);
        Assert.True(report.Passed);
        Assert.Equal(Now, report.SealedAtUtc);
        Assert.All(report.Checks, c => Assert.Equal(IntegrityStatus.Ok, c.Status));
        Assert.Equal(4, report.Checks.Count);
        Assert.Equal("Integrity check passed for \"Doe intake\": 4 files match the seal written 2026-08-05 10:22.",
            report.Summarize("Doe intake"));
    }

    [Fact]
    public async Task An_edited_file_reads_CHANGED_and_a_deleted_one_reads_MISSING()
    {
        await SealAsync("s-tampered");
        File.WriteAllText(_paths.TranscriptJsonl("s-tampered"), "{\"seq\":0,\"text\":\"different\"}\n");
        File.Delete(_paths.AudioFile("s-tampered", SourceKind.Local, AudioFormat.Flac));

        var report = await IntegrityVerifier.VerifyAsync(_paths, "s-tampered",
            TranscriptVersions.Root, CancellationToken.None);

        Assert.False(report.Passed);
        Assert.Equal(IntegrityStatus.Changed,
            report.Checks.Single(c => c.Name == "transcript.jsonl").Status);
        Assert.Equal(IntegrityStatus.Missing,
            report.Checks.Single(c => c.Name == "local.flac").Status);
        Assert.Equal(
            "Integrity check FAILED for \"Doe intake\": local.flac MISSING; transcript.jsonl CHANGED. "
            + "2 of 4 files match the seal written 2026-08-05 10:22.",
            report.Summarize("Doe intake"));
    }

    [Fact]
    public async Task An_unsealed_session_says_so_instead_of_reporting_a_pass()
    {
        // Every session recorded before this feature is unsealed. Reporting "0 files, all OK" would
        // be a false assurance, which is the one outcome an integrity command must never produce.
        Directory.CreateDirectory(_paths.SessionDir("s-old"));
        File.WriteAllText(_paths.SessionJson("s-old"), "{\"schemaVersion\":4}");

        var report = await IntegrityVerifier.VerifyAsync(_paths, "s-old", TranscriptVersions.Root,
            CancellationToken.None);

        Assert.False(report.Sealed);
        Assert.False(report.Passed);
        Assert.Empty(report.Checks);
        Assert.Equal(
            "\"Doe intake\" has no integrity seal - it was recorded before integrity manifests "
            + "existed, or its manifest.json was deleted. Nothing can be verified.",
            report.Summarize("Doe intake"));
    }

    [Fact]
    public async Task A_session_json_rewrite_followed_by_a_reseal_still_PASSES_on_every_version()
    {
        // The end-to-end shape of the reseal fix: session.json is sealed by every version's
        // manifest, so a version switch (which rewrites session.json and skips the projection
        // regen) must reseal or Verify reports CHANGED on an untampered session. This asserts the
        // PASS the whole reseal exists to preserve.
        const string vid = "v2-tiny.en-2026-08-05";
        await SealAsync("s-switch");
        Directory.CreateDirectory(_paths.VersionDir("s-switch", vid));
        File.WriteAllText(_paths.TranscriptJsonl("s-switch", vid), "{\"seq\":0}\n");
        var store = new SessionStore(_paths.SessionJson("s-switch"));
        var session = (await store.ReadAsync(CancellationToken.None))! with
        {
            Versions = new[]
            {
                new TranscriptVersion { Id = vid, Model = "tiny.en", Backend = "CPU", Language = "en" },
            },
        };
        var writer = new SessionWriter(_paths, new Settings(), new ManualUtcTimeProvider(Now));
        await store.SaveAsync(session, CancellationToken.None);
        await writer.ResealAsync("s-switch", session, CancellationToken.None);

        var switched = session with { ActiveVersion = vid };
        await store.SaveAsync(switched, CancellationToken.None);
        await writer.ResealAsync("s-switch", switched, CancellationToken.None);

        foreach (string v in new[] { TranscriptVersions.Root, vid })
        {
            var report = await IntegrityVerifier.VerifyAsync(_paths, "s-switch", v,
                CancellationToken.None);
            Assert.True(report.Sealed);
            Assert.True(report.Passed, v + ": " + report.Summarize("Doe intake"));
        }
    }
}
