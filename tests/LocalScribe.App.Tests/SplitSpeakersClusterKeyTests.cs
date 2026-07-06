// tests/LocalScribe.App.Tests/SplitSpeakersClusterKeyTests.cs
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Stage 5.4 C2 Task 2: on Confirm, a cluster whose chosen name is one of that side's
/// identity-carrying candidates writes that participant's ClusterKey (through the ONE gated
/// SaveDiarisationAsync write path, with SpeakersMerge's collision remap applied); free-text
/// names keep today's speakers.Names-only path. Harness mirrors SplitSpeakersPickerTests.</summary>
public sealed class SplitSpeakersClusterKeyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_sck_{Guid.NewGuid():N}");

    private sealed class FakeEngine : IDiarisationEngine
    {
        public DiarisationResult Next { get; set; } =
            new([new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)], 2, "fake");

        public Task<DiarisationResult> DiariseAsync(DiarisationRequest r, IProgress<double> p, CancellationToken ct)
        {
            p.Report(1.0);
            return Task.FromResult(Next);
        }
    }

    private (MaintenanceService svc, StoragePaths paths, string id, FakeEngine engine) MakeFinalizedSession(
        IReadOnlyList<SessionParticipant> participants, int remoteCount = 2)
    {
        var paths = new StoragePaths(_root);
        string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));

        new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            RetainedAudioSources = [SourceKind.Remote],
            Devices = new DeviceSnapshot { Remote = new RemoteSnapshot { Mode = RemoteMode.Auto } },
        }, default).GetAwaiter().GetResult();
        new MetadataStore(paths.MetaJson(id)).SaveAsync(
            new SessionMeta { LocalCount = 1, RemoteCount = remoteCount, Participants = participants },
            default).GetAwaiter().GetResult();
        var jsonl = new TranscriptStore(paths.TranscriptJsonl(id));
        jsonl.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 0, 1000, "hi", "Me"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Local, 1000, 2000, "there", "Me"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(3, TranscriptSource.Remote, 0, 1000, "hello", "Them"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(4, TranscriptSource.Remote, 1000, 2000, "world", "Them"), default).GetAwaiter().GetResult();
        File.WriteAllBytes(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac), [1, 2, 3]);

        var settings = new FakeSettingsService(new Settings());
        var svc = new MaintenanceService(paths, settings, new FakeRecycleBin(), TimeProvider.System);
        return (svc, paths, id, new FakeEngine());
    }

    private static SplitSpeakersViewModel MakeVm(MaintenanceService svc, StoragePaths paths, FakeEngine engine) =>
        new(engine, svc, paths, new FakeSettingsService(new Settings()), new FakeUiErrorReporter(),
            a => a(), TimeProvider.System, fileName => fileName);

    private static async Task RunSelectedAsync(SplitSpeakersViewModel vm)
    {
        vm.Sources[0].Selected = true;
        await vm.RunCommand.ExecuteAsync(null);
    }

    [Fact]
    public async Task Confirm_with_picked_candidate_writes_that_participants_cluster_key()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(
        [
            new SessionParticipant { Id = "p-barrister", Name = "Barrister", Side = SourceKind.Remote },
            new SessionParticipant { Id = "p-colleague", Name = "Colleague", Side = SourceKind.Remote },
        ]);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        await RunSelectedAsync(vm);

        vm.Clusters[0].Name = "Barrister";   // picked candidate; Clusters[1] stays default label

        await vm.ConfirmCommand.ExecuteAsync(null);

        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal(vm.Clusters[0].ClusterKey,
            meta!.Participants.Single(p => p.Id == "p-barrister").ClusterKey);        // "Remote:0"
        Assert.Null(meta.Participants.Single(p => p.Id == "p-colleague").ClusterKey); // not picked

        // Names still written exactly as today alongside the ownership.
        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Barrister", speakers!.Names[vm.Clusters[0].ClusterKey]);
    }

    [Fact]
    public async Task Confirm_with_free_text_keeps_names_only_and_leaves_participants_untouched()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(
            [new SessionParticipant { Id = "p-barrister", Name = "Barrister", Side = SourceKind.Remote }]);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        await RunSelectedAsync(vm);

        vm.Clusters[0].Name = "Some Stranger";   // matches no slot -> exactly today's path

        await vm.ConfirmCommand.ExecuteAsync(null);

        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Null(meta!.Participants.Single().ClusterKey);

        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Some Stranger", speakers!.Names["Remote:0"]);
        Assert.Equal("Remote Speaker 2", speakers.Names["Remote:1"]);   // untouched default
    }

    [Fact]
    public async Task Rediarise_clears_stale_ownership_that_is_not_reasserted()
    {
        // Cluster ids restart at 0 each run: ownership left over from run 1 must not silently
        // attach the slot's name to whatever voice run 2 calls "Remote:0".
        var (svc, paths, id, engine) = MakeFinalizedSession(
            [new SessionParticipant { Id = "p-barrister", Name = "Barrister", Side = SourceKind.Remote }]);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        await RunSelectedAsync(vm);
        vm.Clusters[0].Name = "Barrister";
        await vm.ConfirmCommand.ExecuteAsync(null);   // run 1: ownership lands

        await vm.RunCommand.ExecuteAsync(null);       // run 2: fresh clusters, defaults kept
        await vm.ConfirmCommand.ExecuteAsync(null);   // nothing re-asserted

        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Null(meta!.Participants.Single().ClusterKey);
    }

    [Fact]
    public async Task Ownership_uses_the_collision_remapped_key_when_a_pin_survives_rediarise()
    {
        // Group B's SpeakersMerge remap: seq "3" is pinned to run-1's "Remote:0", so run-2's fresh
        // "Remote:0" is remapped (max id among pinned {0} + fresh {0,1} is 1 -> "Remote:2").
        // The picked participant must own the REMAPPED key - the raw key now names the pinned
        // cluster, a DIFFERENT voice (the exact mislabel class Stage 5's review fixed).
        var (svc, paths, id, engine) = MakeFinalizedSession(
            [new SessionParticipant { Id = "p-barrister", Name = "Barrister", Side = SourceKind.Remote }]);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        await RunSelectedAsync(vm);
        await vm.ConfirmCommand.ExecuteAsync(null);   // run 1: default labels, no ownership

        var store = new SpeakersStore(paths.SpeakersJson(id));
        var afterFirst = await store.LoadAsync(default);
        await store.SaveAsync(afterFirst! with
        {
            Pinned = new Dictionary<string, List<string>> { ["Remote"] = ["3"] },
        }, default);

        await vm.RunCommand.ExecuteAsync(null);       // run 2
        var raw = vm.Clusters.Single(c => c.ClusterKey == "Remote:0");
        raw.Name = "Barrister";
        await vm.ConfirmCommand.ExecuteAsync(null);

        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        Assert.Equal("Remote:2", meta!.Participants.Single().ClusterKey);

        var speakers = await store.LoadAsync(default);
        Assert.Equal("Barrister", speakers!.Names["Remote:2"]);          // remapped fresh cluster
        Assert.Equal("Remote Speaker 1", speakers.Names["Remote:0"]);    // pin-preserved run-1 name
    }

    [Fact]
    public async Task Same_candidate_picked_for_two_clusters_last_cluster_wins_no_duplicate_claim()
    {
        // Self-review probe (not one of the brief's 4, added for evidentiary rigor): what happens
        // when the SAME candidate name is typed/picked for TWO different clusters in one confirm?
        // owned[] (ConfirmAsync) is keyed by participantId, so the second cluster's entry
        // silently overwrites the first - the participant ends up owning exactly the LAST cluster
        // processed, never both (there is only one ClusterKey field on the slot to hold it). This
        // also demonstrates the write path cannot produce two DIFFERENT participants claiming the
        // same ClusterKey: a given ClusterKey value can only ever be attached to the one
        // participant whose candidate name matched that specific cluster row.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            [new SessionParticipant { Id = "p-barrister", Name = "Barrister", Side = SourceKind.Remote }]);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        await RunSelectedAsync(vm);

        vm.Clusters[0].Name = "Barrister";
        vm.Clusters[1].Name = "Barrister";   // same candidate picked for both clusters

        await vm.ConfirmCommand.ExecuteAsync(null);

        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        var participant = meta!.Participants.Single();
        Assert.Equal(vm.Clusters[1].ClusterKey, participant.ClusterKey);   // last cluster wins
        Assert.NotEqual(vm.Clusters[0].ClusterKey, participant.ClusterKey); // never both

        // Both clusters still display "Barrister" (owned-participant tier for Clusters[1],
        // speakers.Names tier for Clusters[0]) even though only one carries durable ownership.
        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Barrister", speakers!.Names[vm.Clusters[0].ClusterKey]);
        Assert.Equal("Barrister", speakers.Names[vm.Clusters[1].ClusterKey]);
    }

    [Fact]
    public async Task Ownership_write_branch_never_flips_edited_last_edited_or_summary_fields()
    {
        // Fix wave 1 (review gap): the earlier evidentiary-fields citation covered only the 3-arg
        // null branch, which SKIPS the meta write entirely. This test drives the actual 4-arg
        // WRITE branch (picked candidate -> participants rewrite executes -> meta.json saved) with
        // Edited/LastEditedAtUtc/Summary* all populated, and proves on a fresh disk read that ONLY
        // ClusterKey moved. Would fail if the write path ever constructed a fresh SessionMeta
        // (defaulting Edited to false / dropping Summary*) instead of `meta with { Participants }`.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            [new SessionParticipant { Id = "p-barrister", Name = "Barrister", Side = SourceKind.Remote }]);

        var editedAt = DateTimeOffset.UnixEpoch.AddHours(3);
        var summaryAt = DateTimeOffset.UnixEpoch.AddHours(4);
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            LocalCount = 1, RemoteCount = 2,
            Participants = [new SessionParticipant { Id = "p-barrister", Name = "Barrister", Side = SourceKind.Remote }],
            Edited = true, LastEditedAtUtc = editedAt,
            SummaryRef = "summary.md", SummaryGeneratedAtUtc = summaryAt, SummaryModel = "test-model",
        }, default);

        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        await RunSelectedAsync(vm);

        vm.Clusters[0].Name = "Barrister";   // picked candidate -> write branch executes

        await vm.ConfirmCommand.ExecuteAsync(null);

        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        // Proof the write branch actually ran: ownership landed on disk.
        Assert.Equal(vm.Clusters[0].ClusterKey, meta!.Participants.Single().ClusterKey);
        // The evidentiary fields survived the SAME write byte-for-byte.
        Assert.True(meta.Edited);
        Assert.Equal(editedAt, meta.LastEditedAtUtc);
        Assert.Equal("summary.md", meta.SummaryRef);
        Assert.Equal(summaryAt, meta.SummaryGeneratedAtUtc);
        Assert.Equal("test-model", meta.SummaryModel);
    }

    [Fact]
    public async Task Two_participants_picked_on_different_clusters_each_own_only_their_own_key()
    {
        // Fix wave 1 (review minor): the single-participant duplicate-claim test proved last-wins;
        // this proves the by-construction claim for DIFFERENT participants - each picked on its
        // own cluster ends up owning exactly that cluster's key on disk, and no key is claimed by
        // both (distinct ClusterKey values across the two slots).
        var (svc, paths, id, engine) = MakeFinalizedSession(
        [
            new SessionParticipant { Id = "p-barrister", Name = "Barrister", Side = SourceKind.Remote },
            new SessionParticipant { Id = "p-colleague", Name = "Colleague", Side = SourceKind.Remote },
        ]);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        await RunSelectedAsync(vm);

        vm.Clusters[0].Name = "Barrister";
        vm.Clusters[1].Name = "Colleague";

        await vm.ConfirmCommand.ExecuteAsync(null);

        var meta = await new MetadataStore(paths.MetaJson(id)).LoadAsync(default);
        string? barristerKey = meta!.Participants.Single(p => p.Id == "p-barrister").ClusterKey;
        string? colleagueKey = meta.Participants.Single(p => p.Id == "p-colleague").ClusterKey;
        Assert.Equal(vm.Clusters[0].ClusterKey, barristerKey);   // "Remote:0"
        Assert.Equal(vm.Clusters[1].ClusterKey, colleagueKey);   // "Remote:1"
        Assert.NotEqual(barristerKey, colleagueKey);             // no key claimed by both
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
