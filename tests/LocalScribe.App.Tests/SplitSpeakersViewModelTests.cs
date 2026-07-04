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
        public Task<DiarisationResult> DiariseAsync(DiarisationRequest r, IProgress<double> p, CancellationToken ct)
        {
            LastForced = r.ForcedClusterCount;
            p.Report(1.0);
            return Task.FromResult(Next);
        }
    }

    // Mirrors MaintenanceServiceDiarisationTests.MakeFinalizedSession (Task 7) but parameterized
    // on RemoteCount / retained sources / system-mix, and returns a fresh FakeEngine per session.
    private (MaintenanceService svc, StoragePaths paths, string id, FakeEngine engine) MakeFinalizedSession(
        int remoteCount, IReadOnlyList<SourceKind> retained, bool systemMix = false)
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
            new SessionMeta { RemoteCount = remoteCount }, default).GetAwaiter().GetResult();
        var jsonl = new TranscriptStore(paths.TranscriptJsonl(id));
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

        await vm.ConfirmCommand.ExecuteAsync(null);

        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Contains(SourceKind.Remote, speakers!.DiarisedSources);
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
