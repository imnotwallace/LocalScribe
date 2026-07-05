// tests/LocalScribe.App.Tests/SplitSpeakersPickerTests.cs
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Stage 5.2 Task 9 (B2): the Split-speakers dialog's per-cluster name field becomes an
/// editable ComboBox offering that side's session participants (loaded.Meta.Participants) as
/// pick-able candidates, alongside free text for un-rostered speakers. Harness mirrors
/// SplitSpeakersViewModelTests.MakeFinalizedSession/MakeVm, parameterized on participants.</summary>
public sealed class SplitSpeakersPickerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_ssp_{Guid.NewGuid():N}");

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
        int remoteCount, IReadOnlyList<SourceKind> retained,
        IReadOnlyList<SessionParticipant>? participants = null, int localCount = 1)
    {
        var paths = new StoragePaths(_root);
        string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));

        new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            RetainedAudioSources = retained,
            Devices = new DeviceSnapshot { Remote = new RemoteSnapshot { Mode = RemoteMode.Auto } },
        }, default).GetAwaiter().GetResult();
        new MetadataStore(paths.MetaJson(id)).SaveAsync(
            new SessionMeta
            {
                LocalCount = localCount, RemoteCount = remoteCount,
                Participants = participants ?? [],
            }, default).GetAwaiter().GetResult();
        var jsonl = new TranscriptStore(paths.TranscriptJsonl(id));
        jsonl.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 0, 1000, "hi", "Me"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Local, 1000, 2000, "there", "Me"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(3, TranscriptSource.Remote, 0, 1000, "hello", "Them"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(4, TranscriptSource.Remote, 1000, 2000, "world", "Them"), default).GetAwaiter().GetResult();
        if (retained.Contains(SourceKind.Remote))
            File.WriteAllBytes(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac), [1, 2, 3]);
        if (retained.Contains(SourceKind.Local))
            File.WriteAllBytes(paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac), [1, 2, 3]);

        var settings = new FakeSettingsService(new Settings());
        var svc = new MaintenanceService(paths, settings, new FakeRecycleBin(), TimeProvider.System);
        var engine = new FakeEngine();
        return (svc, paths, id, engine);
    }

    private static SplitSpeakersViewModel MakeVm(MaintenanceService svc, StoragePaths paths, FakeEngine engine) =>
        new(engine, svc, paths, new FakeSettingsService(new Settings()), new FakeUiErrorReporter(),
            a => a(), TimeProvider.System, fileName => fileName);

    [Fact]
    public async Task Cluster_rows_offer_the_sessions_participants_as_name_candidates()
    {
        // Local=1 participant ("Me"), Remote=2 participants ("Colleague", "Barrister") - both
        // sides splittable (declared counts > 1) so both a Local and a Remote cluster row exist,
        // proving candidates thread PER SIDE, not all-to-all.
        var participants = new SessionParticipant[]
        {
            new() { Id = "p-me", Name = "Me", Side = SourceKind.Local },
            new() { Id = "p-colleague", Name = "Colleague", Side = SourceKind.Remote },
            new() { Id = "p-barrister", Name = "Barrister", Side = SourceKind.Remote },
        };
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 2, retained: [SourceKind.Local, SourceKind.Remote],
            participants: participants, localCount: 2);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;
        vm.Sources[1].Selected = true;

        await vm.RunCommand.ExecuteAsync(null);

        var remoteCluster = vm.Clusters.First(c => c.Source == SourceKind.Remote);
        Assert.Contains("Barrister", remoteCluster.NameCandidates);
        Assert.Contains("Colleague", remoteCluster.NameCandidates);

        var localCluster = vm.Clusters.First(c => c.Source == SourceKind.Local);
        Assert.Contains("Me", localCluster.NameCandidates);
        Assert.DoesNotContain("Barrister", localCluster.NameCandidates);   // per-side, not all-to-all
    }

    [Fact]
    public async Task Picking_a_candidate_name_is_what_confirm_persists()
    {
        var participants = new SessionParticipant[]
        {
            new() { Id = "p-barrister", Name = "Barrister", Side = SourceKind.Remote },
        };
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 2, retained: [SourceKind.Remote], participants: participants);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;

        await vm.RunCommand.ExecuteAsync(null);

        var cluster = vm.Clusters.First(c => c.Source == SourceKind.Remote);
        Assert.Contains("Barrister", cluster.NameCandidates);
        cluster.Name = "Barrister";   // what the editable ComboBox writes via Text -> Name binding

        await vm.ConfirmCommand.ExecuteAsync(null);

        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Barrister", speakers!.Names[cluster.ClusterKey]);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
