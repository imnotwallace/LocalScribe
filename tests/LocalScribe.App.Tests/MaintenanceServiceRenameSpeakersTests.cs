using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>RenameSpeakersAsync (design 2026-07-28 task 4): the names-only write path used when a
/// user renames an ALREADY-committed diarisation, so reopening Split Speakers to type a name never
/// re-runs the diariser.
///
/// It deliberately does NOT go through SaveDiarisationAsync/SpeakersMerge. Merge's job is to protect
/// pinned/owned keys from a FRESH run by remapping colliding fresh keys; on a rename the "fresh"
/// keys ARE the existing keys, so a pinned key present in the commit would collide with itself and
/// be remapped away - duplicating the cluster. A rename also must not restamp Method/DiarisedAtUtc,
/// re-derive embeddings.json, or flip Diarised.</summary>
public sealed class MaintenanceServiceRenameSpeakersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_rename_{Guid.NewGuid():N}");

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private (MaintenanceService svc, StoragePaths paths, string id) MakeDiarisedSession(
        IReadOnlyList<SessionParticipant>? participants = null)
    {
        var paths = new StoragePaths(_root);
        string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));

        new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id,
            StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            RetainedAudioSources = [SourceKind.Local],
            Diarised = true,
        }, default).GetAwaiter().GetResult();

        new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            LocalCount = 2,
            Participants = participants ?? [],
        }, default).GetAwaiter().GetResult();

        var jsonl = new TranscriptStore(paths.TranscriptJsonl(id));
        jsonl.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 0, 1000, "hi", "Me"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Local, 1000, 2000, "there", "Me"), default).GetAwaiter().GetResult();

        // An already-committed diarisation: two clusters, default labels, one pinned seq.
        new SpeakersStore(paths.SpeakersJson(id)).SaveAsync(new Speakers
        {
            Names = new Dictionary<string, string>
            { ["Local:0"] = "Local Speaker 1", ["Local:1"] = "Local Speaker 2" },
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Local"] = new() { ["1"] = "Local:0", ["2"] = "Local:1" } },
            Pinned = new Dictionary<string, List<string>> { ["Local"] = ["1"] },
            DiarisedSources = [SourceKind.Local],
            Method = "sherpa",
            DiarisedAtUtc = DateTimeOffset.UnixEpoch,
        }, default).GetAwaiter().GetResult();

        var svc = new MaintenanceService(paths, new FakeSettingsService(new Settings()),
            new FakeRecycleBin(), TimeProvider.System);
        return (svc, paths, id);
    }

    [Fact]
    public async Task Renames_without_disturbing_assignments_pins_or_the_diarisation_stamp()
    {
        var (svc, paths, id) = MakeDiarisedSession();

        bool wrote = await svc.RenameSpeakersAsync(id, "v1",
            new Dictionary<string, string> { ["Local:0"] = "Sarah Chen", ["Local:1"] = "Tom Ridge" },
            participantClusterKeys: null, provenance: null, default);

        Assert.True(wrote);
        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);

        Assert.Equal("Sarah Chen", s!.Names["Local:0"]);
        Assert.Equal("Tom Ridge", s.Names["Local:1"]);

        // Everything a rename must NOT touch:
        Assert.Equal("Local:0", s.Assignments["Local"]["1"]);
        Assert.Equal("Local:1", s.Assignments["Local"]["2"]);
        Assert.Equal(["1"], s.Pinned["Local"]);
        Assert.Equal("sherpa", s.Method);
        Assert.Equal(DateTimeOffset.UnixEpoch, s.DiarisedAtUtc);
        Assert.Contains(SourceKind.Local, s.DiarisedSources);
    }

    [Fact]
    public async Task A_pinned_cluster_key_is_never_remapped_or_duplicated()
    {
        // THE regression this method exists for: routing a rename through
        // SaveDiarisationAsync/SpeakersMerge would see "Local:0" as a fresh key colliding with the
        // pinned "Local:0" and remap it to an unused id, leaving two rows for one voice.
        var (svc, paths, id) = MakeDiarisedSession();

        await svc.RenameSpeakersAsync(id, "v1",
            new Dictionary<string, string> { ["Local:0"] = "Sarah Chen", ["Local:1"] = "Tom Ridge" },
            participantClusterKeys: null, provenance: null, default);

        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal(2, s!.Names.Count);
        Assert.DoesNotContain("Local:2", s.Names.Keys);
    }

    [Fact]
    public async Task Leaves_the_embeddings_sidecar_completely_alone()
    {
        var (svc, paths, id) = MakeDiarisedSession();
        var embPath = paths.EmbeddingsJson(id, "v1");
        Directory.CreateDirectory(Path.GetDirectoryName(embPath)!);
        await new ClusterEmbeddingsStore(embPath).SaveAsync(new ClusterEmbeddings
        {
            Method = "campplus",
            ExtractedAtUtc = DateTimeOffset.UnixEpoch,
            Entries = new Dictionary<string, float[]> { ["Local:0"] = [1f, 0f], ["Local:1"] = [0f, 1f] },
        }, default);
        var before = await File.ReadAllTextAsync(embPath);

        await svc.RenameSpeakersAsync(id, "v1",
            new Dictionary<string, string> { ["Local:0"] = "Sarah Chen" },
            participantClusterKeys: null, provenance: null, default);

        Assert.Equal(before, await File.ReadAllTextAsync(embPath));
    }

    [Fact]
    public async Task Persists_participant_ownership_without_flipping_the_edited_flag()
    {
        var (svc, paths, id) = MakeDiarisedSession(
            [new SessionParticipant { Id = "p1", Name = "Sarah Chen", Side = SourceKind.Local, Kind = ParticipantKind.Named }]);

        await svc.RenameSpeakersAsync(id, "v1",
            new Dictionary<string, string> { ["Local:0"] = "Sarah Chen" },
            new Dictionary<string, string> { ["p1"] = "Local:0" },
            provenance: null, default);

        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal("Local:0", meta!.Participants.Single(p => p.Id == "p1").ClusterKey);
        // Edited/LastEditedAtUtc are reserved for manual transcript corrections.
        Assert.False(meta.Edited);
        Assert.Null(meta.LastEditedAtUtc);
    }

    [Fact]
    public async Task Records_accepted_suggestion_provenance()
    {
        var (svc, paths, id) = MakeDiarisedSession();

        await svc.RenameSpeakersAsync(id, "v1",
            new Dictionary<string, string> { ["Local:0"] = "Sarah Chen" },
            participantClusterKeys: null,
            new Dictionary<string, SuggestionProvenanceEntry>
            { ["Local:0"] = new("person-1", 0.87, DateTimeOffset.UnixEpoch) },
            default);

        var s = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("person-1", s!.SuggestionProvenance["Local:0"].PersonId);
    }

    [Fact]
    public async Task Regenerates_projections_with_the_new_names()
    {
        var (svc, paths, id) = MakeDiarisedSession();

        await svc.RenameSpeakersAsync(id, "v1",
            new Dictionary<string, string> { ["Local:0"] = "Sarah Chen", ["Local:1"] = "Tom Ridge" },
            participantClusterKeys: null, provenance: null, default);

        string txt = await File.ReadAllTextAsync(paths.TranscriptTxt(id));
        Assert.Contains("Sarah Chen", txt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Returns_false_when_the_session_has_no_speakers_overlay_to_rename()
    {
        var paths = new StoragePaths(_root);
        string id = "empty";
        Directory.CreateDirectory(paths.SessionDir(id));
        await new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
        }, default);
        var svc = new MaintenanceService(paths, new FakeSettingsService(new Settings()),
            new FakeRecycleBin(), TimeProvider.System);

        Assert.False(await svc.RenameSpeakersAsync(id, "v1",
            new Dictionary<string, string> { ["Local:0"] = "Nobody" },
            participantClusterKeys: null, provenance: null, default));
    }

    [Fact]
    public async Task Rejects_a_version_the_session_never_recorded()
    {
        var (svc, _, id) = MakeDiarisedSession();

        await Assert.ThrowsAsync<ArgumentException>(() => svc.RenameSpeakersAsync(
            id, "v99", new Dictionary<string, string> { ["Local:0"] = "X" },
            participantClusterKeys: null, provenance: null, default));
    }
}
