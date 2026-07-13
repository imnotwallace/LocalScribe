using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ReadViewVersionSwitchTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-readview-versions-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public ReadViewVersionSwitchTests()
    { _paths = new StoragePaths(_root); Directory.CreateDirectory(_paths.SessionsDir); }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class NoopDualPlayer : IDualAudioPlayer
    {
        public void Load(string? localPath, string? remotePath) { }
        public void Play() { }
        public void Pause() { }
        public void SeekMs(long ms) { }
        public void SetLegMuted(bool local, bool muted) { }
        public void SetLegVolume(bool local, double volume) { }
        public long PositionMs => 0;
        public long DurationMs => 0;
        public event Action? MediaReady { add { } remove { } }
        public event Action? MediaEnded { add { } remove { } }
        public void Dispose() { }
    }

    private const string Vid = "v2-tiny.en-2026-07-13";

    private async Task<string> SeedAsync(bool withVersion)
    {
        string id = "2026-07-10_1000_Webex_seed";
        Directory.CreateDirectory(_paths.SessionDir(id));
        var record = new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 10, 2, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 10, 2, 1, 0, TimeSpan.Zero),
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
            DurationMs = 60000, Model = "small.en", Backend = "CUDA", Language = "en",
        };
        if (withVersion)
        {
            Directory.CreateDirectory(_paths.VersionDir(id, Vid));
            var vT = new TranscriptStore(_paths.TranscriptJsonl(id, Vid));
            await vT.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "V2 words.", "Me"), default);
            await JsonFile.WriteAsync(_paths.EditsJson(id, Vid), new Edits(), default);
            record = record with
            {
                ActiveVersion = Vid,
                Versions = new[] { new TranscriptVersion { Id = Vid, Model = "tiny.en", Backend = "CPU", Language = "en" } },
            };
        }
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(record, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "Seed", Medium = Medium.Webex }, default);
        var rootT = new TranscriptStore(_paths.TranscriptJsonl(id));
        await rootT.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Root words.", "Me"), default);
        return id;
    }

    private ReadViewViewModel MakeVm()
        => new(new MaintenanceService(_paths, new FakeSettingsService(), new FakeRecycleBin(),
                TimeProvider.System),
            _paths, new FakeSettingsService(), new FakeUiErrorReporter(), new NoopDualPlayer(),
            dispatch: a => a(), TimeProvider.System);

    [Fact]
    public async Task Load_shows_the_active_versions_rows_badge_and_footer()
    {
        string id = await SeedAsync(withVersion: true);
        var vm = MakeVm();
        await vm.LoadAsync(id, CancellationToken.None);

        Assert.Equal("V2 words.", vm.Rows.Single(r => !r.Data.IsMarker).Data.Text);
        Assert.True(vm.HasVersions);
        Assert.Equal(new[] { "v1 \u00B7 small.en", "v2 \u00B7 tiny.en" },
            vm.VersionOptions.Select(o => o.Label).ToArray());
        Assert.Equal(Vid, vm.SelectedVersionOption?.Id);
        Assert.Equal("tiny.en \u00B7 CPU", vm.ModelBackendFooter);

        // The programmatic selection sync must NOT have written anything.
        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal(Vid, session!.ActiveVersion);
        vm.Dispose();
    }

    [Fact]
    public async Task Switching_persists_activeVersion_and_reloads_rows_and_footer()
    {
        string id = await SeedAsync(withVersion: true);
        var vm = MakeVm();
        await vm.LoadAsync(id, CancellationToken.None);

        await vm.SwitchVersionAsync("v1", CancellationToken.None);

        var session = await new SessionStore(_paths.SessionJson(id)).ReadAsync(default);
        Assert.Equal("v1", session!.ActiveVersion);                    // persisted
        Assert.Equal("Root words.", vm.Rows.Single(r => !r.Data.IsMarker).Data.Text);
        Assert.Equal("v1", vm.SelectedVersionOption?.Id);
        Assert.Equal("small.en \u00B7 CUDA", vm.ModelBackendFooter);
        vm.Dispose();
    }

    [Fact]
    public async Task Unversioned_session_hides_the_switcher()
    {
        string id = await SeedAsync(withVersion: false);
        var vm = MakeVm();
        await vm.LoadAsync(id, CancellationToken.None);
        Assert.False(vm.HasVersions);
        Assert.Equal("small.en \u00B7 CUDA", vm.ModelBackendFooter);   // footer moved, value unchanged
        vm.Dispose();
    }
}
