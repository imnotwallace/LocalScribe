using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.People;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Records dispatched actions and runs them only when explicitly pumped, one turn at a
/// time - the same ordering microscope SplitSpeakersViewModelVoiceprintTests.cs:29-42 and
/// SettingsVoiceprintTests.cs:45-50 use. Deliberately duplicated here rather than shared (house
/// convention: no cross-file test helper) - kept internal (not nested) so this one copy is visible
/// to, and shared by, both test classes below. (A `file`-local type cannot appear in a member
/// signature of a non-file-local type, so it cannot be nested inside either public test class and
/// used by the other - CS9051.)</summary>
sealed class QueuedDispatch
{
    // Lock-guarded: this stands in for WPF's Dispatcher, whose BeginInvoke is thread-safe from any
    // thread. A fire-and-forget load's pool-thread continuation can enqueue here while the test
    // thread is inside Pump/PumpOne; a plain Queue<Action> corrupts under that concurrent access.
    // Dequeue under the lock, invoke outside it so a re-entrant dispatch cannot deadlock.
    private readonly object _gate = new();
    private readonly Queue<Action> _queue = new();
    public Action<Action> Dispatch => a => { lock (_gate) _queue.Enqueue(a); };
    public bool PumpOne()
    {
        Action next;
        lock (_gate)
        {
            if (_queue.Count == 0) return false;
            next = _queue.Dequeue();
        }
        next();
        return true;
    }
    public void Pump() { while (PumpOne()) { } }
}

/// <summary>One-engine-at-a-time gate for diarisation (design 2026-07-28 adjacent fix 3, Task 11):
/// today a Split Speakers run or a voiceprint backfill scan can start mid-recording (or while
/// another offline engine owns the machine) with no refusal and no banner - contention is CPU/RAM
/// only (the diariser sets no GPU field), but CPU theft can spuriously trip whisper's RTF downgrade
/// ladder (TranscriptionWorker.cs:121-134). Probe-and-refuse, not a latch: the seam is deliberately
/// cooperative, the same contract SessionControllerTests.cs:544-566 pins for the live engine. The
/// new `engineBusy` ctor parameter is a TRAILING OPTIONAL on both VMs (null = never refuse), so
/// every existing construction site is unaffected.</summary>
public sealed class DiarisationEngineGateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_degt_{Guid.NewGuid():N}");

    private sealed class FakeEngine : IDiarisationEngine
    {
        public int Calls { get; private set; }
        public DiarisationResult Next { get; set; } =
            new([new DiarisedSegment(0, 2000, 0)], 1, "fake");

        public Task<DiarisationResult> DiariseAsync(DiarisationRequest r, IProgress<double> p, CancellationToken ct)
        {
            Calls++;
            p.Report(1.0);
            return Task.FromResult(Next);
        }
    }

    // Mirrors SplitSpeakersViewModelTests.MakeFinalizedSession (Task 9) - a finalized session with
    // a retained leg per source kind, ready for the Split Speakers dialog to load.
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

    // QUEUED dispatch (fix round 1, Important finding): a synchronous a => a() fake was flagged
    // against the plan-mandated constraint - a past Critical stamp-ordering bug was masked by
    // exactly that shortcut. Every test below now pumps explicitly after each awaited command,
    // mirroring SplitSpeakersViewModelVoiceprintTests.cs's sequencing.
    private static SplitSpeakersViewModel MakeVm(MaintenanceService svc, StoragePaths paths, FakeEngine engine,
        QueuedDispatch dispatcher, FakeUiErrorReporter reporter, Func<string?>? engineBusy) =>
        new(engine, svc, paths, new FakeSettingsService(new Settings()), reporter,
            dispatcher.Dispatch, TimeProvider.System, fileName => fileName,
            new PeopleStore(paths.PeopleJson),
            (_, _) => Task.FromResult<IReadOnlyList<Matter>>([]),
            new VoiceprintEnrollmentService(paths, TimeProvider.System, () => Guid.NewGuid().ToString("N")),
            engineBusy);

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Split_speakers_refuses_to_run_while_an_engine_is_busy()
    {
        // Today you can start a Split Speakers run mid-recording with no refusal and no banner.
        // Contention is CPU/RAM only (the diariser sets no GPU field), but CPU theft can spuriously
        // trip whisper's RTF downgrade ladder at TranscriptionWorker.cs:121-134.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var dispatcher = new QueuedDispatch();
        var reporter = new FakeUiErrorReporter();
        var vm = MakeVm(svc, paths, engine, dispatcher, reporter,
            engineBusy: () => "a recording is in progress");
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;

        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Equal(0, engine.Calls);
        Assert.Empty(vm.Clusters);
        Assert.Contains(reporter.Infos, m => m.Contains("recording", StringComparison.OrdinalIgnoreCase));
        // Probe-and-refuse, not a fault: the dialog stays usable.
        Assert.Empty(reporter.Reports);
    }

    [Fact]
    public async Task Split_speakers_runs_normally_when_nothing_is_busy()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var dispatcher = new QueuedDispatch();
        var reporter = new FakeUiErrorReporter();
        var vm = MakeVm(svc, paths, engine, dispatcher, reporter, engineBusy: () => null);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;
        engine.Next = new DiarisationResult(
            [new DiarisedSegment(0, 1000, 0), new DiarisedSegment(1000, 2000, 1)], 2, "fake");

        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Equal(1, engine.Calls);
        Assert.Equal(2, vm.Clusters.Count);
    }

    [Fact]
    public async Task A_null_probe_never_refuses_so_existing_call_sites_are_unaffected()
    {
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        var dispatcher = new QueuedDispatch();
        var vm = MakeVm(svc, paths, engine, dispatcher, new FakeUiErrorReporter(), engineBusy: null);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;

        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Equal(1, engine.Calls);
    }

    [Fact]
    public async Task The_probe_is_re_read_at_run_time_not_captured_at_construction()
    {
        // A dialog opened while idle must still refuse if a recording starts before Run is pressed.
        var (svc, paths, id, engine) = MakeFinalizedSession(remoteCount: 2, retained: [SourceKind.Remote]);
        string? busy = null;
        var dispatcher = new QueuedDispatch();
        var reporter = new FakeUiErrorReporter();
        var vm = MakeVm(svc, paths, engine, dispatcher, reporter, engineBusy: () => busy);
        await vm.LoadAsync(id, default);
        dispatcher.Pump();
        vm.Sources[0].Selected = true;

        busy = "a recording is in progress";
        await vm.RunCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Equal(0, engine.Calls);
    }
}

/// <summary>The voiceprint backfill scan's half of the same gate (SettingsPageViewModel.cs:965-992):
/// it walks EVERY finished session through the diarisation helper with CancellationToken.None and,
/// before this task, no engine-busy check at all. Named with the DiarisationEngineGateTests prefix
/// (rather than a wholly separate name) so the single `--filter FullyQualifiedName~
/// DiarisationEngineGateTests` run picks up all 5 tests in this file together.</summary>
public sealed class DiarisationEngineGateTestsBackfill : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_degt_bf_{Guid.NewGuid():N}");

    private StoragePaths Paths => new(Path.Combine(_root, "storage"));

    public DiarisationEngineGateTestsBackfill() => Directory.CreateDirectory(Path.Combine(_root, "models"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class FakeEmbeddingEngine : IEmbeddingEngine
    {
        public Task<EmbedResult> EmbedAsync(EmbedRequest request, CancellationToken ct)
            => Task.FromResult(new EmbedResult([1f, 0f, 0f], EmbeddingMethods.CampPlus));
    }

    // QUEUED dispatch (see DiarisationEngineGateTests.MakeVm for why the synchronous a => a() fake
    // was replaced): the refusal path dispatches BackfillStatus with no intervening await, so the
    // test below must pump before reading it.
    private SettingsPageViewModel MakeSettingsVm(QueuedDispatch dispatcher, Func<string?>? engineBusy)
    {
        var paths = Paths;
        var settings = new FakeSettingsService(new Settings());
        var maintenance = new MaintenanceService(paths, settings, new FakeRecycleBin(), TimeProvider.System);
        return new SettingsPageViewModel(settings, maintenance, new FakeLaunchAtLogin(),
            pickFolder: () => null, openFolder: _ => { }, new FakeUiErrorReporter(),
            dispatch: dispatcher.Dispatch, new FakeCaptureDeviceEnumerator(),
            modelsRoot: Path.Combine(_root, "models"),
            assistantHelperProbe: () => null,
            paths: paths,
            people: new PeopleStore(paths.PeopleJson),
            enrollment: new VoiceprintEnrollmentService(paths, TimeProvider.System, () => Guid.NewGuid().ToString("N")),
            embeddingEngine: new FakeEmbeddingEngine(),
            resolveModel: fileName => Path.Combine(_root, "models", fileName),
            engineBusy: engineBusy);
    }

    [Fact]
    public async Task The_voiceprint_backfill_scan_refuses_while_an_engine_is_busy()
    {
        // SettingsPageViewModel.cs:966-977 runs the same helper over EVERY finished session, with
        // CancellationToken.None, with no engine-busy check at all.
        var dispatcher = new QueuedDispatch();
        var vm = MakeSettingsVm(dispatcher, engineBusy: () => "a recording is in progress");

        await vm.BackfillScanCommand.ExecuteAsync(null);
        dispatcher.Pump();

        Assert.Contains("recording", vm.BackfillStatus, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsVoiceprintBusy);
    }
}
