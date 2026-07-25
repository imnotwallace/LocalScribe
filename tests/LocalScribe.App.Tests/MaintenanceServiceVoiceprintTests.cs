using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.People;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Task 9 (voiceprint design 2026-07-25): embeddings.json persistence in
/// SaveDiarisationAsync (remap-applied, other-sources-preserved, untouched-on-no-embeddings) and
/// the global voiceprint purge (derived-data-only firewall). Mirrors
/// MaintenanceServiceDiarisationTests's fixture/seed style.</summary>
public sealed class MaintenanceServiceVoiceprintTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_voiceprint_{Guid.NewGuid():N}");

    private (MaintenanceService svc, StoragePaths paths, string id) MakeFinalizedSession()
    {
        var paths = new StoragePaths(_root);
        string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));

        // Finalized session with a retained Remote leg + two remote segments (mirrors
        // MaintenanceServiceDiarisationTests.MakeFinalizedSession).
        new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            RetainedAudioSources = [SourceKind.Remote],
        }, default).GetAwaiter().GetResult();
        new MetadataStore(paths.MetaJson(id)).SaveAsync(
            new SessionMeta { RemoteCount = 2 }, default).GetAwaiter().GetResult();
        var jsonl = new TranscriptStore(paths.TranscriptJsonl(id));
        jsonl.AppendAsync(TranscriptLine.Segment(3, TranscriptSource.Remote, 0, 1000, "hello", "Them"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(4, TranscriptSource.Remote, 1000, 2000, "world", "Them"), default).GetAwaiter().GetResult();

        var settings = new FakeSettingsService(new Settings());
        var svc = new MaintenanceService(paths, settings, new FakeRecycleBin(), TimeProvider.System);
        return (svc, paths, id);
    }

    // Second session helper for the multi-session purge tests below: same style as
    // MakeFinalizedSession but shares the SAME paths root, so both live under one
    // PurgeVoiceprintDataAsync sweep of paths.SessionsDir.
    private static void SeedSession(StoragePaths paths, string id)
    {
        Directory.CreateDirectory(paths.SessionDir(id));
        new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            RetainedAudioSources = [SourceKind.Remote],
        }, default).GetAwaiter().GetResult();
        new MetadataStore(paths.MetaJson(id)).SaveAsync(
            new SessionMeta { RemoteCount = 2 }, default).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Save_writes_embeddings_json_with_remapped_keys()
    {
        var (svc, paths, id) = MakeFinalizedSession();
        // A named slot durably owns "Remote:0" (Stage 5.4 identity<->voice link) - the SAME
        // collision-protection mechanism MaintenanceServiceDiarisationTests uses to force a remap.
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            RemoteCount = 2,
            Participants = [new SessionParticipant
            { Id = "p-bob", Name = "Bob Barrister", Side = SourceKind.Remote, ClusterKey = "Remote:0" }],
        }, default);

        // The fresh run has a SINGLE cluster "0" for Remote: protected {0}, fresh {0} -> the
        // colliding fresh key remaps to the next unused id, "Remote:1".
        var commit = new DiarisationCommit(
            [SourceKind.Remote],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            { ["Remote"] = new Dictionary<string, string> { ["3"] = "Remote:0" } },
            new Dictionary<string, string> { ["Remote:0"] = "Remote Speaker 1" },
            "sherpa", DateTimeOffset.UnixEpoch);

        var resultsBySource = new Dictionary<string, DiarisationResult>
        {
            ["Remote"] = new DiarisationResult(
                [], 1, "sherpa",
                new Dictionary<string, float[]> { ["0"] = [1f, 2f] },
                "campplus-zh-en"),
        };

        var remap = await svc.SaveDiarisationAsync(id, commit, "v1",
            participantClusterKeys: null, resultsBySource, default);

        Assert.Equal("Remote:1", remap["Remote:0"]);   // sanity: the same remap the merge computed

        var emb = await new ClusterEmbeddingsStore(paths.EmbeddingsJson(id, "v1")).LoadAsync(default);
        Assert.NotNull(emb);
        Assert.True(emb!.Entries.ContainsKey("Remote:1"));
        Assert.Equal(new float[] { 1f, 2f }, emb.Entries["Remote:1"]);
        Assert.DoesNotContain("Remote:0", emb.Entries.Keys);   // the PRE-remap key must never land
        Assert.Equal("campplus-zh-en", emb.Method);
    }

    [Fact]
    public async Task Save_preserves_other_sources_embeddings()
    {
        var (svc, paths, id) = MakeFinalizedSession();
        // NOTE: seeded/read via the version-less paths.EmbeddingsJson(id) overload while the save
        // below is authored against versionId "v1" - this only works because
        // TranscriptVersions.Root == "v1" collapses VersionDir to the session dir itself; a future
        // reader should not "fix" this mismatch away.
        await new ClusterEmbeddingsStore(paths.EmbeddingsJson(id)).SaveAsync(new ClusterEmbeddings
        {
            Method = "campplus-zh-en",
            ExtractedAtUtc = DateTimeOffset.UnixEpoch,
            Entries = new Dictionary<string, float[]> { ["Local:0"] = [9f] },
        }, default);

        var commit = new DiarisationCommit(
            [SourceKind.Remote],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            { ["Remote"] = new Dictionary<string, string> { ["3"] = "Remote:0", ["4"] = "Remote:1" } },
            new Dictionary<string, string> { ["Remote:0"] = "Remote Speaker 1", ["Remote:1"] = "Remote Speaker 2" },
            "sherpa", DateTimeOffset.UnixEpoch);
        var resultsBySource = new Dictionary<string, DiarisationResult>
        {
            ["Remote"] = new DiarisationResult(
                [], 2, "sherpa",
                new Dictionary<string, float[]> { ["0"] = [5f] },
                "campplus-zh-en"),
        };

        await svc.SaveDiarisationAsync(id, commit, "v1", participantClusterKeys: null, resultsBySource, default);

        var emb = await new ClusterEmbeddingsStore(paths.EmbeddingsJson(id)).LoadAsync(default);
        Assert.NotNull(emb);
        Assert.Equal(new float[] { 9f }, emb!.Entries["Local:0"]);     // untouched by the Remote re-diarise
        Assert.Equal(new float[] { 5f }, emb.Entries["Remote:0"]);     // freshly written
    }

    [Fact]
    public async Task Save_without_results_leaves_embeddings_untouched()
    {
        var (svc, paths, id) = MakeFinalizedSession();
        // NOTE: same version-less-overload-vs-"v1" mismatch as Save_preserves_other_sources_embeddings
        // above - works only because TranscriptVersions.Root == "v1" collapses to the session dir.
        await new ClusterEmbeddingsStore(paths.EmbeddingsJson(id)).SaveAsync(new ClusterEmbeddings
        {
            Method = "campplus-zh-en",
            ExtractedAtUtc = DateTimeOffset.UnixEpoch,
            Entries = new Dictionary<string, float[]> { ["Local:0"] = [9f] },
        }, default);
        byte[] before = await File.ReadAllBytesAsync(paths.EmbeddingsJson(id));

        var commit = new DiarisationCommit(
            [SourceKind.Remote],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            { ["Remote"] = new Dictionary<string, string> { ["3"] = "Remote:0", ["4"] = "Remote:1" } },
            new Dictionary<string, string> { ["Remote:0"] = "Remote Speaker 1", ["Remote:1"] = "Remote Speaker 2" },
            "sherpa", DateTimeOffset.UnixEpoch);

        // Old-helper degrade path: resultsBySource is null (the 5-arg overload's own default).
        await svc.SaveDiarisationAsync(id, commit, "v1", participantClusterKeys: null, resultsBySource: null,
            default);

        byte[] after = await File.ReadAllBytesAsync(paths.EmbeddingsJson(id));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Save_ignores_results_for_source_outside_commit()
    {
        // Finding 1 (Task 9 fix round 1): a source that RAN (present in resultsBySource) but was
        // deselected before Confirm (absent from commit.Sources) has no FreshKeyRemap entry - it
        // must be ignored entirely, never written under raw pre-remap keys that would otherwise
        // land AFTER, and overwrite, the just-preserved correct entries for that same source.
        var (svc, paths, id) = MakeFinalizedSession();
        await new ClusterEmbeddingsStore(paths.EmbeddingsJson(id)).SaveAsync(new ClusterEmbeddings
        {
            Method = "campplus-zh-en",
            ExtractedAtUtc = DateTimeOffset.UnixEpoch,
            Entries = new Dictionary<string, float[]> { ["Local:0"] = [9f] },
        }, default);

        // commit.Sources is Remote ONLY, but resultsBySource carries a result for "Local" too
        // (it ran in this pass but was deselected before Confirm).
        var commit = new DiarisationCommit(
            [SourceKind.Remote],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            { ["Remote"] = new Dictionary<string, string> { ["3"] = "Remote:0" } },
            new Dictionary<string, string> { ["Remote:0"] = "Remote Speaker 1" },
            "sherpa", DateTimeOffset.UnixEpoch);
        var resultsBySource = new Dictionary<string, DiarisationResult>
        {
            ["Remote"] = new DiarisationResult(
                [], 1, "sherpa",
                new Dictionary<string, float[]> { ["0"] = [5f] },
                "campplus-zh-en"),
            ["Local"] = new DiarisationResult(
                [], 1, "sherpa",
                new Dictionary<string, float[]> { ["0"] = [99f] },   // raw key would be "Local:0"
                "campplus-zh-en"),
        };

        await svc.SaveDiarisationAsync(id, commit, "v1", participantClusterKeys: null, resultsBySource, default);

        var emb = await new ClusterEmbeddingsStore(paths.EmbeddingsJson(id)).LoadAsync(default);
        Assert.NotNull(emb);
        Assert.Equal(new float[] { 9f }, emb!.Entries["Local:0"]);    // pre-existing value untouched
        Assert.Equal(new float[] { 5f }, emb.Entries["Remote:0"]);    // Remote's fresh write still lands
    }

    [Fact]
    public async Task Save_with_results_but_no_embeddings_drops_stale_entries_for_rediarised_source()
    {
        // Finding 3 (Task 9 fix round 1): resultsBySource NON-NULL but carrying no embeddings for
        // commit.Sources still means a run demonstrably re-asserted identity for those sources -
        // any surviving entry from a PREVIOUS run now names a different voice than speakers.json
        // does. This is NOT the legacy (resultsBySource == null) degrade path, which stays untouched.
        var (svc, paths, id) = MakeFinalizedSession();
        await new ClusterEmbeddingsStore(paths.EmbeddingsJson(id)).SaveAsync(new ClusterEmbeddings
        {
            Method = "campplus-zh-en",
            ExtractedAtUtc = DateTimeOffset.UnixEpoch,
            Entries = new Dictionary<string, float[]>
            { ["Remote:0"] = [1f], ["Local:0"] = [9f] },
        }, default);

        var commit = new DiarisationCommit(
            [SourceKind.Remote],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            { ["Remote"] = new Dictionary<string, string> { ["3"] = "Remote:0" } },
            new Dictionary<string, string> { ["Remote:0"] = "Remote Speaker 1" },
            "sherpa", DateTimeOffset.UnixEpoch);
        // resultsBySource is non-null but carries NO ClusterEmbeddings for Remote (e.g. the
        // embedding model failed to extract vectors this run, while diarisation itself succeeded).
        var resultsBySource = new Dictionary<string, DiarisationResult>
        {
            ["Remote"] = new DiarisationResult([], 1, "sherpa", ClusterEmbeddings: null, EmbeddingMethod: null),
        };

        await svc.SaveDiarisationAsync(id, commit, "v1", participantClusterKeys: null, resultsBySource, default);

        var emb = await new ClusterEmbeddingsStore(paths.EmbeddingsJson(id)).LoadAsync(default);
        Assert.NotNull(emb);
        Assert.Equal(new float[] { 9f }, emb!.Entries["Local:0"]);       // another source's entry preserved
        Assert.DoesNotContain("Remote:0", emb.Entries.Keys);             // stale re-diarised entry dropped
    }

    [Fact]
    public async Task Purge_deletes_embeddings_provenance_and_enrollments_only()
    {
        var (svc, paths, id) = MakeFinalizedSession();

        // embeddings.json in the root version AND a versions\v2 copy.
        var rootEmb = new ClusterEmbeddings
        {
            Method = "campplus-zh-en", ExtractedAtUtc = DateTimeOffset.UnixEpoch,
            Entries = new Dictionary<string, float[]> { ["Remote:0"] = [1f] },
        };
        await new ClusterEmbeddingsStore(paths.EmbeddingsJson(id)).SaveAsync(rootEmb, default);
        Directory.CreateDirectory(paths.VersionDir(id, "v2"));
        await new ClusterEmbeddingsStore(paths.EmbeddingsJson(id, "v2")).SaveAsync(rootEmb, default);

        // speakers.json: one Name + one SuggestionProvenance entry.
        var speakers = new Speakers
        {
            Names = new Dictionary<string, string> { ["Remote:0"] = "Bob Barrister" },
            SuggestionProvenance = new Dictionary<string, SuggestionProvenanceEntry>
            { ["Remote:0"] = new SuggestionProvenanceEntry("p-bob", 0.91, DateTimeOffset.UnixEpoch) },
        };
        await new SpeakersStore(paths.SpeakersJson(id)).SaveAsync(speakers, default);

        // people.json: one enrolled person.
        var person = new Person
        {
            Id = "p-bob", Name = "Bob Barrister", CreatedUtc = DateTimeOffset.UnixEpoch,
            Voiceprint = [new VoiceprintEnrollment
            {
                Id = "e1", Embedding = [1f, 2f], Method = "campplus-zh-en",
                SourceSessionId = id, SourceClusterKey = "Remote:0", EnrolledAtUtc = DateTimeOffset.UnixEpoch,
            }],
        };
        await new PeopleStore(paths.PeopleJson).SaveAsync(new PeopleRegistry { People = [person] }, default);

        byte[] transcriptBefore = await File.ReadAllBytesAsync(paths.TranscriptJsonl(id));

        var result = await svc.PurgeVoiceprintDataAsync(default);

        Assert.Equal(1, result.SessionsTouched);
        Assert.Empty(result.Failures);
        Assert.False(File.Exists(paths.EmbeddingsJson(id)));
        Assert.False(File.Exists(paths.EmbeddingsJson(id, "v2")));

        var speakersAfter = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Bob Barrister", speakersAfter!.Names["Remote:0"]);   // Names UNCHANGED
        Assert.Empty(speakersAfter.SuggestionProvenance);

        var peopleAfter = await new PeopleStore(paths.PeopleJson).LoadAsync(default);
        var bobAfter = Assert.Single(peopleAfter!.People);
        Assert.Equal("Bob Barrister", bobAfter.Name);   // person + name survive
        Assert.Empty(bobAfter.Voiceprint);              // only the voiceprint is stripped

        byte[] transcriptAfter = await File.ReadAllBytesAsync(paths.TranscriptJsonl(id));
        Assert.Equal(transcriptBefore, transcriptAfter);   // audio/transcript firewall
    }

    [Fact]
    public async Task Purge_clears_versioned_speakers_provenance()
    {
        // The existing "only" test seeds embeddings.json in versions\v2 but SuggestionProvenance
        // only in the ROOT speakers.json, so the versioned speakers.json branch of the per-version
        // loop was never actually exercised. This pins it directly.
        var (svc, paths, id) = MakeFinalizedSession();
        Directory.CreateDirectory(paths.VersionDir(id, "v2"));
        var v2Speakers = new Speakers
        {
            Names = new Dictionary<string, string> { ["Remote:0"] = "Bob Barrister" },
            SuggestionProvenance = new Dictionary<string, SuggestionProvenanceEntry>
            { ["Remote:0"] = new SuggestionProvenanceEntry("p-bob", 0.91, DateTimeOffset.UnixEpoch) },
        };
        await new SpeakersStore(paths.SpeakersJson(id, "v2")).SaveAsync(v2Speakers, default);

        var result = await svc.PurgeVoiceprintDataAsync(default);

        Assert.Equal(1, result.SessionsTouched);
        var v2After = await new SpeakersStore(paths.SpeakersJson(id, "v2")).LoadAsync(default);
        Assert.Equal("Bob Barrister", v2After!.Names["Remote:0"]);   // Names UNCHANGED
        Assert.Empty(v2After.SuggestionProvenance);
    }

    [Fact]
    public async Task Purge_across_multiple_sessions_accumulates_touched_count_and_purges_each()
    {
        var (svc, paths, id1) = MakeFinalizedSession();
        string id2 = "s2";
        SeedSession(paths, id2);

        var emb = new ClusterEmbeddings
        {
            Method = "campplus-zh-en", ExtractedAtUtc = DateTimeOffset.UnixEpoch,
            Entries = new Dictionary<string, float[]> { ["Remote:0"] = [1f] },
        };
        await new ClusterEmbeddingsStore(paths.EmbeddingsJson(id1)).SaveAsync(emb, default);
        await new ClusterEmbeddingsStore(paths.EmbeddingsJson(id2)).SaveAsync(emb, default);

        var result = await svc.PurgeVoiceprintDataAsync(default);

        Assert.Equal(2, result.SessionsTouched);
        Assert.Empty(result.Failures);
        Assert.False(File.Exists(paths.EmbeddingsJson(id1)));
        Assert.False(File.Exists(paths.EmbeddingsJson(id2)));
    }

    [Fact]
    public async Task Purge_collects_corrupt_session_failure_and_still_purges_and_strips_people()
    {
        // Finding 2 (Task 9 fix round 1): a malformed speakers.json throws JsonException out of
        // SpeakersStore.LoadAsync. That must not abort the OTHER sessions' purge, and the People
        // enrollment strip - sequenced after the per-session loop - must still run.
        var (svc, paths, goodId) = MakeFinalizedSession();
        string badId = "s-bad";
        SeedSession(paths, badId);
        File.WriteAllText(paths.SpeakersJson(badId), "{ not valid json");   // malformed -> JsonException

        var goodEmb = new ClusterEmbeddings
        {
            Method = "campplus-zh-en", ExtractedAtUtc = DateTimeOffset.UnixEpoch,
            Entries = new Dictionary<string, float[]> { ["Remote:0"] = [1f] },
        };
        await new ClusterEmbeddingsStore(paths.EmbeddingsJson(goodId)).SaveAsync(goodEmb, default);

        var person = new Person
        {
            Id = "p-bob", Name = "Bob Barrister", CreatedUtc = DateTimeOffset.UnixEpoch,
            Voiceprint = [new VoiceprintEnrollment
            {
                Id = "e1", Embedding = [1f, 2f], Method = "campplus-zh-en",
                SourceSessionId = goodId, SourceClusterKey = "Remote:0", EnrolledAtUtc = DateTimeOffset.UnixEpoch,
            }],
        };
        await new PeopleStore(paths.PeopleJson).SaveAsync(new PeopleRegistry { People = [person] }, default);

        var result = await svc.PurgeVoiceprintDataAsync(default);

        Assert.Equal(1, result.SessionsTouched);              // only the good session counted
        var failure = Assert.Single(result.Failures);
        Assert.Equal(badId, failure.Id);

        Assert.False(File.Exists(paths.EmbeddingsJson(goodId)));   // good session still purged

        var peopleAfter = await new PeopleStore(paths.PeopleJson).LoadAsync(default);
        var bobAfter = Assert.Single(peopleAfter!.People);
        Assert.Empty(bobAfter.Voiceprint);   // People strip STILL ran despite the corrupt session
    }

    [Fact]
    public async Task Purge_when_people_json_absent_does_not_throw()
    {
        var (svc, _, _) = MakeFinalizedSession();
        var result = await svc.PurgeVoiceprintDataAsync(default);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task Purge_when_people_json_has_zero_enrollments_does_not_throw()
    {
        var (svc, paths, _) = MakeFinalizedSession();
        await new PeopleStore(paths.PeopleJson).SaveAsync(new PeopleRegistry { People = [] }, default);

        var result = await svc.PurgeVoiceprintDataAsync(default);

        Assert.Empty(result.Failures);
        var peopleAfter = await new PeopleStore(paths.PeopleJson).LoadAsync(default);
        Assert.Empty(peopleAfter!.People);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
