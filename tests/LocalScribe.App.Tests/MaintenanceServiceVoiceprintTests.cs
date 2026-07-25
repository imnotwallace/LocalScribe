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

        int touched = await svc.PurgeVoiceprintDataAsync(default);

        Assert.Equal(1, touched);
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

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
