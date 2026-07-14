using System.IO;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;
using Xunit;

namespace LocalScribe.Core.Tests;

public sealed class SessionControllerTranscriptionFaultTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-txfault-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task Worker_fault_keeps_audio_and_finalizes_cleanly_with_a_marker()
    {
        // Engine factory that throws on creation -> the worker faults right after Start. Audio must
        // still be written and Stop must finalize (not throw, not "recovered").
        var faulting = new FakeEngineFactory((BackendPlan plan) => throw new FileNotFoundException("boom"));
        var (c, provider, paths, _) = LiveTestDoubles.MakeController(_root, engineFactory: faulting);
        // ensure the model fail-fast passes: MakeController stubs ggml-base.en.bin (Task 3 Step 5)

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);                                     // Start succeeded (fault is async)

        string? stopped = await c.StopAsync(CancellationToken.None);   // must NOT throw
        Assert.Equal(id, stopped);

        await c.PendingFinalize;                                // the worker-fault finalize (marker + audio-only) now runs in the background
        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.False(record!.Recovered);                        // clean stop, not recovery
        Assert.True(record.RetainedAudioSources.Count > 0);     // audio retained
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Contains(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.TranscriptionFailed);
        // audio actually has samples: the flac exists and is > an empty-header size
        Assert.True(new FileInfo(paths.AudioFile(id!, SourceKind.Local, record.RetainedAudioSources.Contains(SourceKind.Local) ? AudioFormat.Flac : AudioFormat.Flac)).Length > 0);
    }

    [Fact]
    public async Task Session_json_records_the_backend_the_session_ended_on_after_a_floor_fall()
    {
        // B1-1 follow-up: a mid-session CUDA->CPU floor-fall (VRAM OOM at the tiny.en ladder floor)
        // must be recorded in session.json.Backend, not left as the Start-time CUDA. Both fake engines
        // default to the SAME weights file (ggml-tiny.en.bin), so this is exactly the gap the review
        // flagged: no weights-changed marker fires, so session.json.Backend is the only persisted
        // signal of the fall. It reads "CPU" only because finalize now persists the effective backend.
        var factory = new FakeEngineFactory(plan => plan.Backend == Backend.Cuda
            ? new FakeTranscriptionEngine("tiny.en", new object[] { new VramOutOfMemoryException("oom") })
            : new FakeTranscriptionEngine("tiny.en", s => new TranscriptionResult("recovered", "en", 0.0)));
        var paths = new StoragePaths(_root);
        var controller = new SessionController(paths, new Settings(), factory,
            () => new AmplitudeSpeechModel(),
            new StaticHardwareProbe(new HardwareInfo(true, 8192, false, 4)),   // HasCuda -> CUDA start plan
            new FakeProvider(), () => new FakeClock(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 2, 6, 0, 0, TimeSpan.Zero)),
            "0.3.0", () => new HashSet<string> { "tiny.en" });                 // only the floor model

        string? id = await controller.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);
        Assert.True(SpinWait.SpinUntil(() => controller.ActiveEngineBackend == Backend.Cpu,
            TimeSpan.FromSeconds(10)), "the worker never fell to CPU");

        await controller.StopAsync(CancellationToken.None);
        await controller.PendingFinalize;

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.Equal("CPU", record!.Backend);                                 // the floor-fall backend, not Start-time CUDA
    }

    [Fact]
    public async Task Late_worker_fault_after_stop_finalizes_recovered_false_with_a_marker()
    {
        // Fix #7 (late-fault branch, previously untested): the engine BUILDS successfully (after the
        // gate opens) but throws on the first TranscribeAsync. The gate keeps the build blocked all
        // through Recording, so nothing transcribes until AFTER Stop has left Recording -> the
        // mid-session ContinueWith's State==Recording guard is FALSE and the FinalizeInBackgroundAsync
        // catch is the branch that writes the marker. Proves that path finalizes audio-only
        // (Recovered:false) with exactly one "transcription failed" marker.
        var gated = new GatedEngineFactory(_ => throw new InvalidOperationException("transcribe boom"));
        var (c, _, paths, clock) = LiveTestDoubles.MakeController(_root, engineFactory: gated);

        string? id = await c.StartAsync(LiveTestDoubles.Options(), CancellationToken.None);
        Assert.NotNull(id);                                     // build gated -> nothing transcribes during Recording
        clock.ElapsedMs = 5_000;

        string? stopped = await c.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(id, stopped);
        Assert.Equal(SessionState.Idle, c.State);               // Stop left Recording; State is now Idle
        Assert.False(c.PendingFinalize.IsCompleted);            // finalize awaits the still-gated worker build

        gated.CreateGate.Set();                                 // build completes -> first TranscribeAsync throws -> worker faults
        await c.PendingFinalize.WaitAsync(TimeSpan.FromSeconds(10));

        var record = await new SessionStore(paths.SessionJson(id!)).ReadAsync(CancellationToken.None);
        Assert.False(record!.Recovered);                        // clean finalize, not a recovery husk
        Assert.True(record.RetainedAudioSources.Count > 0);     // audio retained through the transcriber fault
        var lines = await new TranscriptStore(paths.TranscriptJsonl(id!)).ReadAllAsync(CancellationToken.None);
        Assert.Single(lines, l => l.Kind == TranscriptKind.Marker && l.Text == Markers.TranscriptionFailed);  // exactly one marker
    }
}
