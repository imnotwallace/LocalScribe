// tests/LocalScribe.App.Tests/SplitSpeakersSourceGateTests.cs
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.People;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Design 2026-07-28 task 6: LoadAsync used to gate each side's offer on
/// meta.LocalCount/RemoteCount > 1. SessionMeta.LocalCount/RemoteCount default to 1
/// (SessionMeta.cs:21,24) and AudioImporter never raises them (AudioImporter.cs:108-110), so the
/// old gate made Split Speakers open EMPTY - Run disabled, nothing to do - on every freshly
/// imported session. A source is now offered whenever its leg is retained AND probes present on
/// disk, regardless of the declared count; the count stays meaningful only as what the force-N
/// button forces (see CanForceRun).
///
/// Per-file harness copy (house convention - see SplitSpeakersViewModelTests' own comment): this
/// file copies MakeFinalizedSession/MakeVm from SplitSpeakersViewModelTests.cs verbatim rather
/// than sharing them.</summary>
public sealed class SplitSpeakersSourceGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_ssg_{Guid.NewGuid():N}");

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

    // Voiceprint seams (Task 11) are inert here: an empty PeopleStore on the same temp root and no
    // matters means no suggestions and no enrollments.
    private static SplitSpeakersViewModel MakeVm(MaintenanceService svc, StoragePaths paths, FakeEngine engine) =>
        new(engine, svc, paths, new FakeSettingsService(new Settings()),
            a => a(), TimeProvider.System, fileName => fileName,
            new PeopleStore(paths.PeopleJson),
            (_, _) => Task.FromResult<IReadOnlyList<Matter>>([]),
            new VoiceprintEnrollmentService(paths, TimeProvider.System, () => Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task A_leg_with_a_declared_count_of_one_is_still_offered()
    {
        // THE import blocker (design 2026-07-28 task 6): SessionMeta.LocalCount/RemoteCount default
        // to 1 (SessionMeta.cs:21,24) and AudioImporter never raises them
        // (AudioImporter.cs:108-110), so the old `> 1` gate made Split Speakers open EMPTY on every
        // freshly imported session - Run disabled, nothing to do.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 1, retained: [SourceKind.Remote], localCount: 1);
        var vm = MakeVm(svc, paths, engine);

        await vm.LoadAsync(id, default);

        var only = Assert.Single(vm.Sources);
        Assert.Equal(SourceKind.Remote, only.Source);
        Assert.Equal(1, only.DeclaredCount);   // the declared count is retained, just not a gate
    }

    [Fact]
    public async Task Both_legs_are_offered_when_both_are_retained()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 1, retained: [SourceKind.Local, SourceKind.Remote], localCount: 1);
        var vm = MakeVm(svc, paths, engine);

        await vm.LoadAsync(id, default);

        Assert.Equal(2, vm.Sources.Count);
    }

    [Fact]
    public async Task A_leg_with_no_audio_on_disk_is_still_not_offered()
    {
        // The relaxation is about the DECLARED COUNT only. `retained: []` means AudioLegProbe.Resolve
        // short-circuits on its `retained.Contains(kind)` check (AudioLegProbe.cs:20) before it ever
        // reaches the on-disk File.Exists fallback - this test exercises the retained-list branch
        // specifically, not the on-disk-fallback branch, which stays uncovered here.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 3, retained: [], localCount: 3);
        var vm = MakeVm(svc, paths, engine);

        await vm.LoadAsync(id, default);

        Assert.Empty(vm.Sources);
    }

    [Fact]
    public async Task Force_N_stays_suppressed_when_the_declared_count_is_one()
    {
        // Forcing exactly 1 cluster is meaningless, and the count is a default nobody asserted.
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 1, retained: [SourceKind.Remote], localCount: 1);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;
        engine.Next = new DiarisationResult(
            [new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)], 2, "fake");

        await vm.RunCommand.ExecuteAsync(null);

        Assert.True(vm.CountMismatch);                  // 2 found vs 1 declared
        Assert.False(vm.ForceCountCommand.CanExecute(null));
    }

    [Fact]
    public async Task Force_N_is_still_offered_when_a_real_count_was_declared()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(
            remoteCount: 3, retained: [SourceKind.Remote], localCount: 1);
        var vm = MakeVm(svc, paths, engine);
        await vm.LoadAsync(id, default);
        vm.Sources[0].Selected = true;
        engine.Next = new DiarisationResult(
            [new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)], 2, "fake");

        await vm.RunCommand.ExecuteAsync(null);

        Assert.True(vm.CountMismatch);                  // 2 found vs 3 declared
        Assert.True(vm.ForceCountCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_in_progress_session_still_offers_nothing()
    {
        // Unrelated guard, deliberately unchanged: EndedAtUtc null means not finalized.
        var paths = new StoragePaths(_root);
        string id = "live";
        Directory.CreateDirectory(paths.SessionDir(id));
        await new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, StartedAtUtc = DateTimeOffset.UnixEpoch, EndedAtUtc = null,
            RetainedAudioSources = [SourceKind.Remote],
        }, default);
        await new MetadataStore(paths.MetaJson(id)).SaveAsync(new SessionMeta { RemoteCount = 3 }, default);
        File.WriteAllBytes(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac), [1, 2, 3]);
        var svc = new MaintenanceService(paths, new FakeSettingsService(new Settings()),
            new FakeRecycleBin(), TimeProvider.System);
        var vm = MakeVm(svc, paths, new FakeEngine());

        await vm.LoadAsync(id, default);

        Assert.Empty(vm.Sources);
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
