using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MaintenanceServiceContentChangedTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-content-changed-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly MaintenanceService _svc;
    private readonly List<string> _raised = [];

    public MaintenanceServiceContentChangedTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
        _svc = new MaintenanceService(_paths, new FakeSettings(new Settings()), new NoopBin(),
            TimeProvider.System);
        _svc.SessionContentChanged += id => { lock (_raised) _raised.Add(id); };
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // Per-file fakes, byte-identical to SessionsPageViewModelTests' so this file compiles standalone.
    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        { var old = Current; Current = updated; Changed?.Invoke(old, updated); return Task.CompletedTask; }
    }

    private sealed class NoopBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) { }
    }

    private async Task SeedAsync(string id, bool ended = true)
    {
        var t0 = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = t0,
            EndedAtUtc = ended ? t0.AddMinutes(5) : null, DurationMs = ended ? 300_000 : 0,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id))
            .SaveAsync(new SessionMeta { Title = id }, CancellationToken.None);
        await new TranscriptStore(_paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "hello world", "Me"),
            CancellationToken.None);
    }

    [Fact]
    public async Task Correction_save_raises_once_and_a_noop_batch_is_silent()
    {
        await SeedAsync("s-1");
        bool changed = await _svc.SaveTextCorrectionsAsync("s-1",
            new Dictionary<int, string> { [0] = "hello corrected world" }, reverts: [],
            TranscriptVersions.Root, CancellationToken.None);
        Assert.True(changed);
        Assert.Equal(new[] { "s-1" }, _raised.ToArray());

        _raised.Clear();
        bool noop = await _svc.SaveTextCorrectionsAsync("s-1",
            new Dictionary<int, string>(), reverts: new[] { 99 },
            TranscriptVersions.Root, CancellationToken.None);
        Assert.False(noop);
        Assert.Empty(_raised);                                            // nothing changed -> no re-index
    }

    [Fact]
    public async Task Meta_save_raises_and_a_deleted_session_is_silent()
    {
        await SeedAsync("s-3");
        var meta = await new MetadataStore(_paths.MetaJson("s-3")).LoadAsync(CancellationToken.None);
        await _svc.SaveMetaAsync("s-3", meta! with { Title = "Renamed" }, previousMatterIds: [],
            CancellationToken.None);
        Assert.Equal(new[] { "s-3" }, _raised.ToArray());

        _raised.Clear();
        File.Delete(_paths.SessionJson("s-3"));                           // the delete-race guard path
        await _svc.SaveMetaAsync("s-3", meta with { Title = "Again" }, previousMatterIds: [],
            CancellationToken.None);
        Assert.Empty(_raised);                                            // skipped save -> silent
    }

    [Fact]
    public async Task Archive_flip_and_delete_raise_with_the_session_id()
    {
        await SeedAsync("s-2");
        await _svc.SetArchivedAsync("s-2", archived: true, CancellationToken.None);
        Assert.Equal(new[] { "s-2" }, _raised.ToArray());

        _raised.Clear();
        await _svc.SetArchivedAsync("s-2", archived: true, CancellationToken.None);   // already archived
        Assert.Empty(_raised);                                            // no write -> silent

        await _svc.DeleteSessionAsync("s-2", CancellationToken.None);
        Assert.Equal(new[] { "s-2" }, _raised.ToArray());
    }

    // ---- B4-3: direct raise-site coverage for the write paths previously resting on code-pattern
    // analogy (SaveTranscriptEdits / SaveSpeakerPins / RemoveSpeakerPins / SaveDiarisation) plus the
    // B2-4 version-switch no-op contract. Each also pins the silent no-op path so it has real bite. ----

    private async Task SeedVersionedAsync(string id, string vid)
    {
        var t0 = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = t0,
            EndedAtUtc = t0.AddMinutes(5), DurationMs = 300_000, ActiveVersion = vid,
            Versions = new[] { new TranscriptVersion { Id = vid, Model = "tiny.en", Backend = "CPU", Language = "en" } },
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id))
            .SaveAsync(new SessionMeta { Title = id }, CancellationToken.None);
    }

    [Fact]
    public async Task Version_switch_raises_on_a_real_switch_but_an_active_noop_is_silent()
    {
        const string vid = "v2-tiny.en-2026-07-01";
        await SeedVersionedAsync("s-ver", vid);              // seeded ActiveVersion = vid

        // No-op: switch to the ALREADY-active version. Valid (returns true) but writes nothing, so
        // the "never raised for a no-op" contract means no spurious search re-derive (B2-4).
        Assert.True(await _svc.SetActiveVersionAsync("s-ver", vid, CancellationToken.None));
        Assert.Empty(_raised);

        // Real switch vid -> root(v1): writes ActiveVersion, so it raises exactly once.
        Assert.True(await _svc.SetActiveVersionAsync("s-ver", TranscriptVersions.Root, CancellationToken.None));
        Assert.Equal(new[] { "s-ver" }, _raised.ToArray());
    }

    [Fact]
    public async Task TranscriptEdits_save_raises_and_an_empty_batch_is_silent()
    {
        await SeedAsync("s-te");
        var batch = new TranscriptEditBatch(
            new Dictionary<int, string> { [0] = "hello edited world" }, [], [], []);
        Assert.True(await _svc.SaveTranscriptEditsAsync("s-te", batch, TranscriptVersions.Root, CancellationToken.None));
        Assert.Equal(new[] { "s-te" }, _raised.ToArray());

        _raised.Clear();
        var empty = new TranscriptEditBatch(new Dictionary<int, string>(), [], [], []);
        Assert.False(await _svc.SaveTranscriptEditsAsync("s-te", empty, TranscriptVersions.Root, CancellationToken.None));
        Assert.Empty(_raised);                                            // nothing wrote -> no re-index
    }

    [Fact]
    public async Task SpeakerPin_and_unpin_each_raise_and_a_redundant_unpin_is_silent()
    {
        await SeedAsync("s-sp");
        Assert.True(await _svc.SaveSpeakerPinsAsync("s-sp", TranscriptSource.Local, [0],
            new SpeakerPinTarget.Cluster("Local:0"), TranscriptVersions.Root, CancellationToken.None));
        Assert.Equal(new[] { "s-sp" }, _raised.ToArray());

        _raised.Clear();
        Assert.True(await _svc.RemoveSpeakerPinsAsync("s-sp", TranscriptSource.Local, [0],
            TranscriptVersions.Root, CancellationToken.None));
        Assert.Equal(new[] { "s-sp" }, _raised.ToArray());

        _raised.Clear();
        Assert.False(await _svc.RemoveSpeakerPinsAsync("s-sp", TranscriptSource.Local, [0],
            TranscriptVersions.Root, CancellationToken.None));           // nothing left to unpin
        Assert.Empty(_raised);
    }

    [Fact]
    public async Task Diarisation_save_raises_the_session_id()
    {
        await SeedAsync("s-di");
        var commit = new DiarisationCommit(
            [SourceKind.Local],
            new Dictionary<string, IReadOnlyDictionary<string, string>>
            { ["Local"] = new Dictionary<string, string> { ["0"] = "Local:0" } },
            new Dictionary<string, string> { ["Local:0"] = "Local Speaker 1" },
            "sherpa", DateTimeOffset.UnixEpoch);

        await _svc.SaveDiarisationAsync("s-di", commit, TranscriptVersions.Root, CancellationToken.None);
        Assert.Equal(new[] { "s-di" }, _raised.ToArray());
    }

    [Fact]
    public async Task Recovery_and_regenerate_raise_per_session()
    {
        await SeedAsync("s-a");
        await SeedAsync("s-crash", ended: false);

        await _svc.RecoverAllAsync(CancellationToken.None);
        Assert.Equal(new[] { "s-crash" }, _raised.ToArray());             // only the recovered one

        _raised.Clear();
        await _svc.RegenerateAllAsync(progress: null, CancellationToken.None);
        Assert.Equal(new[] { "s-a", "s-crash" },
            _raised.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }
}
