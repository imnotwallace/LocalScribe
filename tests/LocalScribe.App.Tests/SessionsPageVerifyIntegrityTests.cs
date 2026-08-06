using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The Sessions-page integrity command (Tier 1 T1-7, spec 2026-08-05 :143). The outcome
/// goes through IUiErrorReporter.Info, not a bespoke dialog: it is a background-operation OUTCOME,
/// which is exactly what Info exists for (IUiErrorReporter's own doc), and it keeps this VM
/// WPF-free and headless-testable.</summary>
public sealed class SessionsPageVerifyIntegrityTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-verify-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public SessionsPageVerifyIntegrityTests()
    { _paths = new StoragePaths(_root); Directory.CreateDirectory(_paths.SessionsDir); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Lock-guarded stand-in for WPF's Dispatcher.BeginInvoke, pumped explicitly. The
    /// idiom carries a post-mortem (SessionsPageContentFilterTests): with a synchronous
    /// <c>a =&gt; a()</c> fake, THIS view model applies its results from a THREAD-POOL continuation
    /// and mutates Rows while the test thread is enumerating it - "Collection was modified" under
    /// full-suite load, one of the five flaky families fixed on 2026-07-30. A plain Queue&lt;Action&gt;
    /// would corrupt under the concurrent enqueue, so dequeue under the lock and invoke outside it.</summary>
    private sealed class QueuedDispatch
    {
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

    private (SessionsPageViewModel Vm, FakeUiErrorReporter Errors, QueuedDispatch Dispatcher) MakeVm()
    {
        var maintenance = new MaintenanceService(_paths, new FakeSettingsService(),
            new FakeRecycleBin(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero)));
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var errors = new FakeUiErrorReporter();
        var dispatcher = new QueuedDispatch();
        var vm = new SessionsPageViewModel(maintenance, session, new WindowRegistry(), errors,
            dispatch: dispatcher.Dispatch, time: TimeProvider.System, revealInExplorer: _ => { });
        return (vm, errors, dispatcher);
    }

    /// <summary>A sealed session on disk: three text files plus one leg, then a manifest over them.</summary>
    private async Task SealAsync(string id)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 8, 5, 9, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 8, 5, 9, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Doe intake" }, CancellationToken.None);
        File.WriteAllText(_paths.TranscriptJsonl(id), "{\"seq\":0}\n");
        File.WriteAllText(_paths.AudioFile(id, SourceKind.Local, AudioFormat.Flac), "AAAA");
        // sealAudio:true - this fixture stands in for the finalize path (ManifestBuilder's cost
        // gate); with false, local.flac would never enter the manifest.
        await ManifestBuilder.WriteAsync(_paths, id, TranscriptVersions.Root,
            new DateTimeOffset(2026, 8, 5, 10, 22, 0, TimeSpan.Zero), fabricated: null,
            sealAudio: true, CancellationToken.None);
    }

    [Fact]
    public async Task Verifying_an_untouched_session_reports_a_pass()
    {
        await SealAsync("s1");
        var (vm, errors, dispatcher) = MakeVm();
        await vm.RefreshCommand.ExecuteAsync(null);
        dispatcher.Pump();

        await vm.VerifyIntegrityCommand.ExecuteAsync(vm.Rows.Single(r => r.Id == "s1"));
        dispatcher.Pump();

        Assert.Empty(errors.Reports);
        Assert.Contains("Integrity check passed", Assert.Single(errors.Infos));
        Assert.Contains("Doe intake", errors.Infos[0]);
    }

    [Fact]
    public async Task Verifying_an_untouched_session_still_passes_after_a_verification()
    {
        // The verifier must not WRITE anything it is about to hash. SessionStore's two-argument
        // ReadAsync is persistMigration:TRUE, so a verifier using it would rewrite a legacy
        // session.json (and synthesize meta.json) before comparing, then report its own write as
        // `session.json CHANGED` on an untampered session. mtime is the cheapest proof that no
        // write happened at all - the MCP read-only precedent.
        await SealAsync("s-ro");
        var before = File.GetLastWriteTimeUtc(_paths.SessionJson("s-ro"));
        var (vm, errors, dispatcher) = MakeVm();
        await vm.RefreshCommand.ExecuteAsync(null);
        dispatcher.Pump();

        await vm.VerifyIntegrityCommand.ExecuteAsync(vm.Rows.Single(r => r.Id == "s-ro"));
        dispatcher.Pump();
        await vm.VerifyIntegrityCommand.ExecuteAsync(vm.Rows.Single(r => r.Id == "s-ro"));
        dispatcher.Pump();

        Assert.Equal(before, File.GetLastWriteTimeUtc(_paths.SessionJson("s-ro")));
        Assert.All(errors.Infos, i => Assert.Contains("Integrity check passed", i));
    }

    [Fact]
    public async Task Verifying_a_tampered_session_names_the_file_that_moved()
    {
        await SealAsync("s2");
        File.WriteAllText(_paths.TranscriptJsonl("s2"), "{\"seq\":0,\"text\":\"rewritten\"}\n");
        var (vm, errors, dispatcher) = MakeVm();
        await vm.RefreshCommand.ExecuteAsync(null);
        dispatcher.Pump();

        await vm.VerifyIntegrityCommand.ExecuteAsync(vm.Rows.Single(r => r.Id == "s2"));
        dispatcher.Pump();

        string info = Assert.Single(errors.Infos);
        Assert.Contains("Integrity check FAILED", info);
        Assert.Contains("transcript.jsonl CHANGED", info);
    }

    [Fact]
    public async Task A_null_row_is_a_no_op_rather_than_a_reported_error()
    {
        // Every other row command on this page tolerates the null the action bar can hand it before
        // a selection exists; a NullReferenceException surfaced as a red InfoBar would be noise.
        var (vm, errors, dispatcher) = MakeVm();
        await vm.VerifyIntegrityCommand.ExecuteAsync(null);
        dispatcher.Pump();
        Assert.Empty(errors.Infos);
        Assert.Empty(errors.Reports);
    }
}
