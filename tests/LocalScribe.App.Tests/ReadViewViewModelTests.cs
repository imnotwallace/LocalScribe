using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ReadViewViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-readview-vm-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FakeSettings _settings;
    private readonly FakeReporter _reporter = new();
    private readonly ManualUtcTimeProvider _time =
        new(new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly MaintenanceService _maintenance;

    public ReadViewViewModelTests()
    {
        _paths = new StoragePaths(_root);
        _settings = new FakeSettings(new Settings { StorageRoot = _root });
        _maintenance = new MaintenanceService(_paths, _settings, new FakeBin(), _time);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public void WindowRegistry_OpenCount_tracks_register_and_unregister()
    {
        var reg = new WindowRegistry();
        Assert.Equal(0, reg.OpenCount);
        reg.Register("a", () => { });
        reg.Register("b", () => { });
        Assert.Equal(2, reg.OpenCount);
        reg.Unregister("a");
        Assert.Equal(1, reg.OpenCount);
        reg.Unregister("b");
        Assert.Equal(0, reg.OpenCount);
    }

    [Fact]
    public void Placement_cascades_24px_per_open_view_and_carries_saved_size()
    {
        var p = ReadViewPlacement.Next(new WindowPlacement(100, 80, 800, 600), alreadyOpenCount: 2,
            windowWidth: 800, windowHeight: 600, vx: 0, vy: 0, vw: 1920, vh: 1080);
        Assert.Equal(148, p.X);
        Assert.Equal(128, p.Y);
        Assert.Equal(800, p.Width);
        Assert.Equal(600, p.Height);
    }

    [Fact]
    public void Placement_without_saved_state_uses_clamp_fallback_then_cascades()
    {
        var first = ReadViewPlacement.Next(null, 0, 720, 560, 0, 0, 1920, 1080);
        Assert.Equal(1920 - 720 - 16, first.X);                      // ScreenClamp fallback: top-right
        Assert.Equal(16, first.Y);
        Assert.Null(first.Width);

        var second = ReadViewPlacement.Next(null, 1, 720, 560, 0, 0, 1920, 1080);
        Assert.Equal(1200, second.X);                                // fallback + 24, clamped to vw - w
        Assert.Equal(40, second.Y);
    }

    [Fact]
    public void Placement_clamps_offscreen_saved_positions()
    {
        var p = ReadViewPlacement.Next(new WindowPlacement(5000, -900), 0, 720, 560, 0, 0, 1920, 1080);
        Assert.Equal(1200, p.X);                                     // 1920 - 720
        Assert.Equal(0, p.Y);
    }

    private ReadViewViewModel MakeVm()
        => new(_maintenance, _paths, _settings, _reporter, dispatch: a => a(), _time);

    /// <summary>Finalized v3 Webex session at UTC+8 with: a tagged matter carrying a
    /// vocabulary correction ("acme" -> "ACME Corp"), two consecutive Local segments (the
    /// second corrected via the EditStore overlay, which also flips meta.Edited), one Remote
    /// segment, and the degraded-system-audio marker. RetainedAudioSources set for Task 20.</summary>
    private async Task WriteFixtureSessionAsync(string id)
    {
        await new MatterStore(_paths.MattersDir).SaveAsync(new Matter
        {
            Id = "M-2026-001", Name = "Acme Litigation", Reference = "REF-7",
            Vocabulary = new Vocabulary
            {
                Corrections = new Dictionary<string, string> { ["acme"] = "ACME Corp" },
            },
        }, CancellationToken.None);

        var started = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = started.AddMinutes(10), DurationMs = 600_000,
            TimeZoneId = "Singapore Standard Time", UtcOffsetMinutes = 480,
            Model = "small.en", Backend = "cuda", Language = "en",
            RetainedAudioSources = new[] { SourceKind.Local, SourceKind.Remote },
            Devices = new DeviceSnapshot
            {
                Remote = new RemoteSnapshot { Mode = RemoteMode.PerProcess, FellBackToSystemMix = true },
            },
        }, CancellationToken.None);

        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            Title = "Client call", MatterIds = new[] { "M-2026-001" },
            Participants = new[]
            {
                new SessionParticipant { Id = "p-self", Name = "Sam", Side = SourceKind.Local, IsSelf = true },
                new SessionParticipant { Id = "p-jane-doe", Name = "Jane", Side = SourceKind.Remote },
            },
        }, CancellationToken.None);

        var transcript = new TranscriptStore(_paths.TranscriptJsonl(id));
        await transcript.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1500,
            "we spoke to acme this morning", "Me"), CancellationToken.None);
        await transcript.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 1600, 3000,
            "the orignal words", "Me"), CancellationToken.None);
        await transcript.AppendAsync(TranscriptLine.Segment(2, TranscriptSource.Remote, 3200, 4200,
            "sounds good", "Them"), CancellationToken.None);
        await transcript.AppendAsync(TranscriptLine.Marker(3, 4200,
            Markers.DegradedSystemAudioLoopback), CancellationToken.None);

        // Non-destructive correction overlay for seq 1 (also flips meta.Edited).
        await new EditStore(_paths.SessionDir(id), _time)
            .ApplyTextCorrectionAsync(1, "the corrected words", CancellationToken.None);
    }

    [Fact]
    public async Task Load_builds_projection_rows_matching_the_file_renders()
    {
        await WriteFixtureSessionAsync("read-1");
        var vm = MakeVm();
        await vm.LoadAsync("read-1", CancellationToken.None);

        Assert.Empty(_reporter.Errors);
        Assert.True(vm.IsLoaded);

        // Grouping: two consecutive Local "Sam" segments merge into one row; then Jane; then marker.
        Assert.Equal(3, vm.Rows.Count);
        var samRow = vm.Rows[0];
        Assert.False(samRow.IsMarker);
        Assert.Equal("Sam", samRow.DisplayName);                     // declared single Local participant
        Assert.Contains("ACME Corp", samRow.Text);                   // matter vocabulary applied
        Assert.Contains("the corrected words", samRow.Text);         // edits overlay wins verbatim
        Assert.DoesNotContain("orignal", samRow.Text);
        Assert.Equal("Jane", vm.Rows[1].DisplayName);
        Assert.True(vm.Rows[2].IsMarker);
        Assert.Equal(Markers.DegradedSystemAudioLoopback, vm.Rows[2].Text);

        // Parity proof: the FILE render produced by SessionWriter shows the same projected text.
        await new SessionWriter(_paths, _settings.Current, _time)
            .RegenerateProjectionsAsync("read-1", CancellationToken.None);
        string fileRender = await File.ReadAllTextAsync(_paths.TranscriptTxt("read-1"));
        Assert.Contains("ACME Corp", fileRender);
        Assert.Contains("the corrected words", fileRender);
        Assert.DoesNotContain("orignal", fileRender);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        {
            var old = Current;
            Current = updated;
            Changed?.Invoke(old, updated);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeBin : IRecycleBin
    {
        public void SendToRecycleBin(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) => Infos.Add(message);
    }
}
