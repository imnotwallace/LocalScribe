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
}
