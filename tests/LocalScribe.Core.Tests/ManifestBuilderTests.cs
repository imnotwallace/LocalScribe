using System.Text.Json.Nodes;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

/// <summary>manifest.json: the integrity seal over one transcript version's evidentiary files
/// (Tier 1 T1-7, spec 2026-08-05 :146-153). Hashing happens ONCE at finalize; the export path only
/// ever reads the stored value, so the 2026-08-04 ruling against hashing recorded audio AT EXPORT
/// TIME stands untouched.</summary>
public sealed class ManifestBuilderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-manifest-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public ManifestBuilderTests() { _paths = new StoragePaths(_root); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Manifest_round_trips_and_carries_the_schema_stamp_on_disk()
    {
        var store = new ManifestStore(_paths.ManifestJson("s1"));
        var manifest = new SessionManifest
        {
            SessionId = "s1",
            WrittenAtUtc = new DateTimeOffset(2026, 8, 5, 10, 22, 0, TimeSpan.Zero),
            Files =
            [
                new ManifestFile
                {
                    Name = "local.flac", Sha256 = "abc123", SizeBytes = 4096,
                    ModifiedUtc = new DateTimeOffset(2026, 8, 5, 10, 21, 0, TimeSpan.Zero),
                    SampleRate = 16000, FabricatedSilenceKnown = true,
                    FabricatedSilence =
                        [new FabricatedSpan { StartSample = 0, EndSample = 32000, Reason = "clock-gap" }],
                },
            ],
        };

        await store.SaveAsync(manifest, CancellationToken.None);

        // Field-by-field, NEVER Assert.Equal over the whole SessionManifest. Both it and
        // ManifestFile carry IReadOnlyList members, and the compiler-generated record Equals
        // compares those with EqualityComparer<IReadOnlyList<T>>.Default - REFERENCE equality on
        // the backing list. An in-memory collection expression and a freshly deserialized List can
        // never be reference-equal, so a whole-record assertion here is unreachable by
        // construction. Assert.Equal over an IEnumerable IS element-wise, which is why the two
        // list comparisons below are safe (FabricatedSpan has no collection members of its own).
        var read = await store.ReadAsync(CancellationToken.None);
        Assert.NotNull(read);
        Assert.Equal(ManifestStore.Version, read!.SchemaVersion);
        Assert.Equal("s1", read.SessionId);
        Assert.Equal(TranscriptVersions.Root, read.VersionId);
        Assert.Equal(manifest.WrittenAtUtc, read.WrittenAtUtc);
        var readFile = Assert.Single(read.Files);
        Assert.Equal("local.flac", readFile.Name);
        Assert.Equal("abc123", readFile.Sha256);
        Assert.Equal(4096, readFile.SizeBytes);
        Assert.Equal(manifest.Files[0].ModifiedUtc, readFile.ModifiedUtc);
        Assert.Equal(16000, readFile.SampleRate);
        Assert.True(readFile.FabricatedSilenceKnown);
        Assert.Equal(manifest.Files[0].FabricatedSilence, readFile.FabricatedSilence);

        var obj = JsonNode.Parse(File.ReadAllText(_paths.ManifestJson("s1")))!.AsObject();
        Assert.Equal(ManifestStore.Version, obj["schemaVersion"]!.GetValue<int>());
        Assert.Equal("v1", obj["versionId"]!.GetValue<string>());
        var file = obj["files"]!.AsArray()[0]!.AsObject();
        Assert.Equal("abc123", file["sha256"]!.GetValue<string>());
        Assert.Equal("clock-gap", file["fabricatedSilence"]!.AsArray()[0]!["reason"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_absent_manifest_reads_as_null_and_a_newer_schema_is_rejected_not_mangled()
    {
        // Absent is the normal state for every session recorded before this feature - it must read
        // as "unsealed", never as a crash and never as an empty seal that would report every file
        // as verified.
        var store = new ManifestStore(_paths.ManifestJson("s-absent"));
        Assert.Null(await store.ReadAsync(CancellationToken.None));

        Directory.CreateDirectory(_paths.SessionDir("s-newer"));
        File.WriteAllText(_paths.ManifestJson("s-newer"), "{\"schemaVersion\": 99, \"files\": []}");
        await Assert.ThrowsAsync<NotSupportedException>(
            () => new ManifestStore(_paths.ManifestJson("s-newer")).ReadAsync(CancellationToken.None));
    }

    /// <summary>A minimal on-disk session: the five text files the manifest seals plus one "audio"
    /// leg. The leg is not real FLAC - the builder only ever hashes BYTES, so any file with the
    /// right name exercises the same path and keeps the fixture fast.</summary>
    private void Seed(string id, string localAudio = "AAAA")
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        File.WriteAllText(_paths.SessionJson(id), "{\"schemaVersion\":4,\"id\":\"" + id + "\"}");
        File.WriteAllText(_paths.MetaJson(id), "{\"schemaVersion\":3}");
        File.WriteAllText(_paths.TranscriptJsonl(id), "{\"seq\":0}\n");
        File.WriteAllText(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac), localAudio);
    }

    [Fact]
    public async Task Seals_every_file_present_and_skips_the_ones_that_are_not()
    {
        // edits.json and speakers.json are absent-until-used, and an absent file must NOT appear as
        // an entry - a manifest naming a file that never existed would report MISSING forever.
        Seed("s1");

        var manifest = await ManifestBuilder.BuildAsync(_paths, "s1", TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero), fabricated: null,
            sealAudio: true, CancellationToken.None);

        Assert.Equal(new[] { "local.flac", "meta.json", "session.json", "transcript.jsonl" },
            manifest.Files.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal));
        var transcript = manifest.Files.Single(f => f.Name == "transcript.jsonl");
        Assert.Equal(64, transcript.Sha256.Length);                        // 32 bytes, lowercase hex
        Assert.Equal(transcript.Sha256, transcript.Sha256.ToLowerInvariant());
        Assert.Equal(new FileInfo(_paths.TranscriptJsonl("s1")).Length, transcript.SizeBytes);
    }

    [Fact]
    public async Task The_fabricated_ranges_the_writer_reported_are_sealed_with_the_audio()
    {
        // Tier 1 T1-7 (spec 2026-08-05 :148-153): the whole point. A hash over local.flac without
        // this list would certify machine-generated zeros as original recorded audio.
        Seed("s2");
        var spans = new[] { new FabricatedSpan { StartSample = 0, EndSample = 32000, Reason = "clock-gap" } };

        var manifest = await ManifestBuilder.BuildAsync(_paths, "s2", TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero),
            new Dictionary<SourceKind, FabricatedSilenceRecord>
            { [SourceKind.Local] = new(16000, spans) },
            sealAudio: true, CancellationToken.None);

        var leg = manifest.Files.Single(f => f.Name == "local.flac");
        Assert.True(leg.FabricatedSilenceKnown);
        Assert.Equal(16000, leg.SampleRate);
        Assert.Equal(spans, leg.FabricatedSilence);
    }

    [Fact]
    public async Task Audio_with_no_reported_ranges_is_sealed_as_UNKNOWN_not_as_clean()
    {
        // An imported or crash-recovered leg has no writer to report ranges. Recording it as an
        // empty list would be an assertion nobody made; FabricatedSilenceKnown=false says so.
        Seed("s3");

        var manifest = await ManifestBuilder.BuildAsync(_paths, "s3", TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero), fabricated: null,
            sealAudio: true, CancellationToken.None);

        var leg = manifest.Files.Single(f => f.Name == "local.flac");
        Assert.False(leg.FabricatedSilenceKnown);
        Assert.Empty(leg.FabricatedSilence);
    }

    [Fact]
    public async Task A_regenerate_over_an_unsealed_session_seals_the_text_and_never_opens_the_audio()
    {
        // Tier 1 T1-7 cost gate. RegenerateProjectionsAsync is reached from the LAUNCH-TIME
        // recovery scan (SessionWriter.RecoverIfNeededAsync, run by StartupOrchestrator) and from
        // "Regenerate all". Without the gate, the first run after this ships would stream a SHA-256
        // over every retained leg in the library - an unbounded, un-cancellable, unconsented
        // multi-hour read that the spec (:146-147) never asked for; it asks for a seal at FINALIZE,
        // refreshed after overlay writes.
        // The proof is mechanical: the leg is held open with FileShare.None, so ANY attempt by the
        // builder to read it would throw IOException rather than quietly succeed.
        Seed("s6");
        using (var _ = new FileStream(_paths.AudioFile("s6", SourceKind.Local, AudioFormat.Flac),
                   FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var manifest = await ManifestBuilder.BuildAsync(_paths, "s6", TranscriptVersions.Root,
                new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero), fabricated: null,
                sealAudio: false, CancellationToken.None);

            Assert.Equal(new[] { "meta.json", "session.json", "transcript.jsonl" },
                manifest.Files.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal));
        }
    }

    [Fact]
    public async Task A_refresh_carries_the_audio_hash_and_its_ranges_forward_unchanged()
    {
        // This is what keeps the 2026-08-04 no-hashing-at-export ruling honoured in spirit: an
        // overlay write must not re-hash gigabytes of FLAC, and it must not LOSE the fabricated
        // ranges either. Same size + same mtime => the bytes did not move, so reuse the entry.
        Seed("s4");
        await ManifestBuilder.WriteAsync(_paths, "s4", TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero),
            new Dictionary<SourceKind, FabricatedSilenceRecord>
            {
                [SourceKind.Local] = new(16000,
                    [new FabricatedSpan { StartSample = 0, EndSample = 32000, Reason = "end-pad" }]),
            },
            sealAudio: true, CancellationToken.None);
        var first = (await new ManifestStore(_paths.ManifestJson("s4")).ReadAsync(CancellationToken.None))!;

        // A later overlay write: edits.json appears, nothing else moves, and NO fabricated map.
        File.WriteAllText(_paths.EditsJson("s4"), "{\"schemaVersion\":1}");
        await ManifestBuilder.WriteAsync(_paths, "s4", TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 11, 0, 0, TimeSpan.Zero), fabricated: null,
            sealAudio: false, CancellationToken.None);
        var second = (await new ManifestStore(_paths.ManifestJson("s4")).ReadAsync(CancellationToken.None))!;

        // Field-by-field, NOT Assert.Equal over the two ManifestFile records: ManifestFile carries
        // an IReadOnlyList<FabricatedSpan>, and the compiler-generated Equals compares that with
        // EqualityComparer<IReadOnlyList<T>>.Default - i.e. REFERENCE equality on the backing list.
        // Both sides here are separate deserializations, so a whole-record assertion could never
        // pass. Assert.Equal over the two lists IS element-wise, so the ranges are still compared.
        var a = first.Files.Single(f => f.Name == "local.flac");
        var b = second.Files.Single(f => f.Name == "local.flac");
        Assert.Equal(a.Sha256, b.Sha256);                                // never re-hashed
        Assert.Equal(a.SizeBytes, b.SizeBytes);
        Assert.Equal(a.ModifiedUtc, b.ModifiedUtc);
        Assert.Equal(a.SampleRate, b.SampleRate);
        Assert.True(b.FabricatedSilenceKnown);
        Assert.Equal(a.FabricatedSilence, b.FabricatedSilence);          // the ranges survived
        Assert.Contains(second.Files, f => f.Name == "edits.json");      // the new overlay is sealed
        Assert.Equal(new DateTimeOffset(2026, 8, 5, 11, 0, 0, TimeSpan.Zero), second.WrittenAtUtc);
    }

    [Fact]
    public async Task Audio_that_actually_changed_on_disk_is_re_hashed()
    {
        // Carry-forward is keyed on size + mtime, never on presence alone: a re-transcription or a
        // repaired leg must produce a NEW hash, or the seal would certify bytes it never read.
        // The second write passes sealAudio:false ON PURPOSE - the cost gate must not suppress a
        // change to a leg that was ALREADY sealed, only the first hash of one that never was.
        Seed("s5", localAudio: "AAAA");
        await ManifestBuilder.WriteAsync(_paths, "s5", TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 10, 0, 0, TimeSpan.Zero), fabricated: null,
            sealAudio: true, CancellationToken.None);
        var before = (await new ManifestStore(_paths.ManifestJson("s5")).ReadAsync(CancellationToken.None))!
            .Files.Single(f => f.Name == "local.flac");

        File.WriteAllText(_paths.AudioFile("s5", SourceKind.Local, AudioFormat.Flac), "BBBBBBBB");
        await ManifestBuilder.WriteAsync(_paths, "s5", TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 11, 0, 0, TimeSpan.Zero), fabricated: null,
            sealAudio: false, CancellationToken.None);
        var after = (await new ManifestStore(_paths.ManifestJson("s5")).ReadAsync(CancellationToken.None))!
            .Files.Single(f => f.Name == "local.flac");

        Assert.NotEqual(before.Sha256, after.Sha256);
        Assert.Equal(8, after.SizeBytes);
    }
}
