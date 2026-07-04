using System.Collections.ObjectModel;
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SplitSpeakersViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_svm_{Guid.NewGuid():N}");

    private sealed class FakeEngine : IDiarisationEngine
    {
        public int? LastForced { get; private set; }
        public DiarisationResult Next { get; set; } =
            new([new DiarisedSegment(0, 2000, 0)], 1, "fake");

        // Lets a test simulate a mid-loop RunAsync failure on a specific source (e.g. the 2nd
        // selected source), so the loop throws after an earlier source already "succeeded".
        public SourceKind? FailSource { get; set; }

        public Task<DiarisationResult> DiariseAsync(DiarisationRequest r, IProgress<double> p, CancellationToken ct)
        {
            LastForced = r.ForcedClusterCount;
            if (r.Source == FailSource) throw new InvalidOperationException("simulated engine failure");
            p.Report(1.0);
            return Task.FromResult(Next);
        }
    }

    // Mirrors MaintenanceServiceDiarisationTests.MakeFinalizedSession (Task 7) but parameterized
    // on RemoteCount / retained sources / system-mix, and returns a fresh FakeEngine per session.
    private (MaintenanceService svc, StoragePaths paths, string id, FakeEngine engine) MakeFinalizedSession(
        int remoteCount, IReadOnlyList<SourceKind> retained, bool systemMix = false, int localCount = 1)
    {
        var paths = new StoragePaths(_root);
        string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));

        new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            RetainedAudioSources = retained,
            Devices = new DeviceSnapshot
            {
                Remote = new RemoteSnapshot { Mode = systemMix ? RemoteMode.SystemMix : RemoteMode.Auto },
            },
        }, default).GetAwaiter().GetResult();
        new MetadataStore(paths.MetaJson(id)).SaveAsync(
            new SessionMeta { LocalCount = localCount, RemoteCount = remoteCount }, default).GetAwaiter().GetResult();
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
    public async Task Only_offers_sources_with_count_gt_1_and_a_retained_leg()
    {
        // RemoteCount=2 retained; LocalCount=1 -> only Remote splittable.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);

        Assert.Single(vm.Sources);
        Assert.Equal(SourceKind.Remote, vm.Sources[0].Source);
    }

    [Fact]
    public async Task Run_auto_then_forceN_passes_declared_count()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 3, retained: [SourceKind.Remote]);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;

        engine.Next = new DiarisationResult(new[] { new DiarisedSegment(0, 1000, 0) }, 1, "fake"); // auto found 1
        await vm.RunCommand.ExecuteAsync(null);
        Assert.Null(engine.LastForced);                 // first pass is auto
        Assert.True(vm.CountMismatch);                  // 1 != declared 3

        await vm.ForceCountCommand.ExecuteAsync(null);  // "Use 3 speakers"
        Assert.Equal(3, engine.LastForced);
    }

    [Fact]
    public async Task Confirm_writes_diarisation_with_default_labels()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;
        engine.Next = new DiarisationResult(new[]
        {
            new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)
        }, 2, "fake");
        await vm.RunCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Clusters.Count);
        Assert.Equal("Remote Speaker 1", vm.Clusters[0].Name);   // default label

        // Type a custom name on the SECOND cluster only, leaving the first at its untouched
        // default - end-to-end proof (final-review test-hardening) that ConfirmAsync's
        // commit-assembly ("typed-name-wins, else DefaultName" + seq->clusterKey mapping) is
        // exercised through the REAL MaintenanceService.SaveDiarisationAsync/SpeakersMerge path,
        // not only unit-tested against ConfirmAsync's internals in isolation.
        vm.Clusters[1].Name = "Custom Name";

        await vm.ConfirmCommand.ExecuteAsync(null);

        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Contains(SourceKind.Remote, speakers!.DiarisedSources);

        // seq -> clusterKey mapping persisted exactly as ClusterAssigner produced it (transcript
        // seq "3"/"4" are the two Remote lines seeded by MakeFinalizedSession).
        Assert.Equal("Remote:0", speakers.Assignments["Remote"]["3"]);
        Assert.Equal("Remote:1", speakers.Assignments["Remote"]["4"]);

        // Names: the default label persists for the cluster the user never retyped, and the
        // typed name wins - and persists - for the one the user did.
        Assert.Equal("Remote Speaker 1", speakers.Names["Remote:0"]);
        Assert.Equal("Custom Name", speakers.Names["Remote:1"]);
    }

    [Fact]
    public async Task Confirm_does_not_persist_when_a_selected_source_was_never_run()
    {
        // Select a splittable source but never RunAsync it (design gap: Confirm's only guard was
        // "selected.Count == 0", so a selected-but-unrun source used to sail through with an empty
        // assignment/method, persisting a corrupt "diarised" commit). Confirm must refuse to save.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;

        await vm.ConfirmCommand.ExecuteAsync(null);

        var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
        Assert.False(session!.Diarised);
        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Null(speakers);
    }

    [Fact]
    public async Task Run_that_fails_partway_does_not_durably_commit_the_earlier_succeeded_source()
    {
        // Atomic-run property (Task 8 review fix): RunAsync must accumulate per-source results in
        // LOCAL collections and only replace the VM's committed _resultBySource/_assignmentBySource
        // once every selected source has finished. Select two sources (Local runs first, per
        // Sources' load order) and make the engine throw on the 2nd (Remote). Before the fix, Local's
        // entry would already be written into the live dictionaries inside the loop, so a later
        // Confirm of Local alone would wrongly succeed even though the overall run never completed.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 2, retained: [SourceKind.Local, SourceKind.Remote], localCount: 2);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        Assert.Equal(SourceKind.Local, vm.Sources[0].Source);
        Assert.Equal(SourceKind.Remote, vm.Sources[1].Source);
        vm.Sources[0].Selected = true;
        vm.Sources[1].Selected = true;
        engine.FailSource = SourceKind.Remote;   // 2nd selected source throws

        await vm.RunCommand.ExecuteAsync(null);   // caught internally; nothing dispatched

        Assert.Empty(vm.Clusters);   // final dispatch never ran

        // Select ONLY the source that "succeeded" before the failure and try to confirm it.
        vm.Sources[1].Selected = false;
        await vm.ConfirmCommand.ExecuteAsync(null);

        var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
        Assert.False(session!.Diarised);
        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Null(speakers);
    }

    [Fact]
    public async Task ForceN_suppressed_and_banner_shown_for_system_mix_leg()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 3, retained: [SourceKind.Remote], systemMix: true);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        Assert.True(vm.SystemMixWarning);

        vm.Sources[0].Selected = true;
        engine.Next = new DiarisationResult(new[] { new DiarisedSegment(0, 1000, 0) }, 1, "fake");
        await vm.RunCommand.ExecuteAsync(null);
        Assert.False(vm.CanForceCount);   // force-N disabled for a system-mix leg
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
