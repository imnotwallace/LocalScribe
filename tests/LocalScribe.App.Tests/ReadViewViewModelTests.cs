using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
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
    private readonly FakePlayer _player = new();

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
        Action closeA = () => { };
        Action closeB = () => { };
        Assert.Equal(0, reg.OpenCount);
        reg.Register("a", closeA);
        reg.Register("b", closeB);
        Assert.Equal(2, reg.OpenCount);
        reg.Unregister("a", closeA);
        Assert.Equal(1, reg.OpenCount);
        reg.Unregister("b", closeB);
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
        => new(_maintenance, _paths, _settings, _reporter, _player, dispatch: a => a(), _time);

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
        var samRow = vm.Rows[0].Data;
        Assert.False(samRow.IsMarker);
        Assert.Equal("Sam", samRow.DisplayName);                     // declared single Local participant
        Assert.Contains("ACME Corp", samRow.Text);                   // matter vocabulary applied
        Assert.Contains("the corrected words", samRow.Text);         // edits overlay wins verbatim
        Assert.DoesNotContain("orignal", samRow.Text);
        Assert.Equal("Jane", vm.Rows[1].Data.DisplayName);
        Assert.True(vm.Rows[2].Data.IsMarker);
        Assert.Equal(Markers.DegradedSystemAudioLoopback, vm.Rows[2].Data.Text);

        // Parity proof: the FILE render produced by SessionWriter shows the same projected text.
        await new SessionWriter(_paths, _settings.Current, _time)
            .RegenerateProjectionsAsync("read-1", CancellationToken.None);
        string fileRender = await File.ReadAllTextAsync(_paths.TranscriptTxt("read-1"));
        Assert.Contains("ACME Corp", fileRender);
        Assert.Contains("the corrected words", fileRender);
        Assert.DoesNotContain("orignal", fileRender);
    }

    [Fact]
    public async Task Header_badges_and_footer_come_from_session_truth()
    {
        await WriteFixtureSessionAsync("read-2");
        var vm = MakeVm();
        await vm.LoadAsync("read-2", CancellationToken.None);

        Assert.Equal("Client call", vm.Title);
        Assert.Equal("2026-07-01 17:00", vm.DateDisplay);            // 09:00Z at the session's UTC+8
        Assert.Equal("10:00", vm.DurationDisplay);
        Assert.Equal("Acme Litigation (REF-7)", Assert.Single(vm.MatterDisplays));
        Assert.Contains("Sam (Local)", vm.ParticipantDisplays);
        Assert.Contains("Jane (Remote)", vm.ParticipantDisplays);
        Assert.False(vm.Recovered);
        Assert.True(vm.Edited);                                      // EditStore flipped meta.Edited
        Assert.True(vm.SystemMix);                                   // FellBackToSystemMix in fixture
        Assert.True(vm.HasDegradedMarker);                           // marker text equals the constant
        Assert.Equal("small.en \u00B7 cuda", vm.ModelBackendFooter);
        Assert.Equal("relative", vm.TimestampsMode);
    }

    [Fact]
    public async Task SystemMix_badge_also_true_for_explicitly_chosen_systemMix()
    {
        await WriteFixtureSessionAsync("read-mix");
        var store = new SessionStore(_paths.SessionJson("read-mix"));
        var session = await store.ReadAsync(CancellationToken.None);
        await store.SaveAsync(session! with
        {
            Devices = new DeviceSnapshot
            {
                Remote = new RemoteSnapshot { Mode = RemoteMode.SystemMix, FellBackToSystemMix = false },
            },
        }, CancellationToken.None);

        var vm = MakeVm();
        await vm.LoadAsync("read-mix", CancellationToken.None);
        Assert.True(vm.SystemMix);                                   // chosen == fallback for the badge (design 3.2)
    }

    [Fact]
    public async Task Missing_meta_falls_back_to_CreateDefault_like_SessionWriter()
    {
        await WriteFixtureSessionAsync("read-3");
        File.Delete(_paths.MetaJson("read-3"));

        var vm = MakeVm();
        await vm.LoadAsync("read-3", CancellationToken.None);

        Assert.Empty(_reporter.Errors);
        Assert.Equal("Webex \u2014 2026-07-01 17:00", vm.Title);     // CreateDefault at session-local time
        Assert.False(vm.Edited);
    }

    [Fact]
    public async Task Missing_session_reports_and_stays_unloaded()
    {
        var vm = MakeVm();
        await vm.LoadAsync("nope", CancellationToken.None);
        Assert.False(vm.IsLoaded);
        var (context, ex) = Assert.Single(_reporter.Errors);
        Assert.Equal("Open read view", context);
        Assert.IsType<InvalidOperationException>(ex);
    }

    [Fact]
    public async Task Load_resolves_playback_legs_from_retained_sources()
    {
        await WriteFixtureSessionAsync("read-audio");                // RetainedAudioSources = Local+Remote
        File.WriteAllBytes(_paths.AudioFile("read-audio", SourceKind.Local, AudioFormat.Flac), new byte[] { 1 });
        File.WriteAllBytes(_paths.AudioFile("read-audio", SourceKind.Remote, AudioFormat.Wav), new byte[] { 1 });

        var vm = MakeVm();
        await vm.LoadAsync("read-audio", CancellationToken.None);

        Assert.True(vm.Playback.IsAvailable);
        Assert.Equal(_paths.AudioFile("read-audio", SourceKind.Local, AudioFormat.Flac), _player.LoadedLocal);
        Assert.Equal(_paths.AudioFile("read-audio", SourceKind.Remote, AudioFormat.Wav), _player.LoadedRemote);
    }

    [Fact]
    public async Task Load_without_audio_files_hides_the_transport()
    {
        await WriteFixtureSessionAsync("read-noaudio");              // retained says both, disk has neither
        var vm = MakeVm();
        await vm.LoadAsync("read-noaudio", CancellationToken.None);

        Assert.True(vm.IsLoaded);
        Assert.False(vm.Playback.IsAvailable);
        Assert.False(_player.LoadCalled);
    }

    [Fact]
    public void PlayingSectionIndex_follows_position_across_row_windows_and_mirrors_to_playback()
    {
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 0,    EndMs = 1500, DisplayName = "Sam",  Text = "a" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 1600, EndMs = 3000, DisplayName = "Sam",  Text = "b" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 3200, EndMs = 4200, DisplayName = "Jane", Text = "c" }));

        _player.PositionMs = 0;     vm.TickPlayback(); Assert.Equal(0, vm.PlayingSectionIndex);
        _player.PositionMs = 1550;  vm.TickPlayback(); Assert.Equal(0, vm.PlayingSectionIndex);   // gap holds prior section
        _player.PositionMs = 1600;  vm.TickPlayback(); Assert.Equal(1, vm.PlayingSectionIndex);
        _player.PositionMs = 3300;  vm.TickPlayback(); Assert.Equal(2, vm.PlayingSectionIndex);
        _player.PositionMs = 4200;  vm.TickPlayback(); Assert.Equal(2, vm.PlayingSectionIndex);   // inclusive last EndMs
        Assert.Equal(2, vm.Playback.PlayingIndex);                                                // mirrored (canonical)
    }

    [Fact]
    public void JumpToSection_seeks_to_row_start_and_starts_playback()
    {
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 0,    EndMs = 1500, DisplayName = "Sam",  Text = "a" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 3200, EndMs = 4200, DisplayName = "Jane", Text = "c" }));

        vm.JumpToSection(1);
        Assert.Equal(3200, vm.Playback.PositionMs);
        Assert.True(vm.Playback.IsPlaying);

        vm.JumpToSection(99);                    // out of range is a no-op
        Assert.Equal(3200, vm.Playback.PositionMs);
    }

    [Fact]
    public void NowPlaying_flag_follows_playing_section()
    {
        // Stage 5.4 smoke-fix: the moving highlight must live on a per-row IsNowPlaying flag,
        // NOT ListView.SelectedIndex - so it can never overwrite the user's own selection nor
        // fire a UIA selection announcement every time the section advances.
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 0,    EndMs = 1500, DisplayName = "Sam",  Text = "a" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 1600, EndMs = 3000, DisplayName = "Sam",  Text = "b" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 3200, EndMs = 4200, DisplayName = "Jane", Text = "c" }));

        _player.PositionMs = 0;
        vm.TickPlayback();
        Assert.True(vm.Rows[0].IsNowPlaying);
        Assert.False(vm.Rows[1].IsNowPlaying);
        Assert.False(vm.Rows[2].IsNowPlaying);

        _player.PositionMs = 1600;
        vm.TickPlayback();
        Assert.False(vm.Rows[0].IsNowPlaying);
        Assert.True(vm.Rows[1].IsNowPlaying);
        Assert.False(vm.Rows[2].IsNowPlaying);

        _player.PositionMs = 3300;
        vm.TickPlayback();
        Assert.False(vm.Rows[0].IsNowPlaying);
        Assert.False(vm.Rows[1].IsNowPlaying);
        Assert.True(vm.Rows[2].IsNowPlaying);

        vm.JumpToSection(0);
        Assert.Equal(0, vm.Rows[0].Data.StartMs);
        Assert.Equal(0, vm.Playback.PositionMs);
    }

    [Fact]
    public async Task Rows_carry_segments_and_the_corrected_flag_after_load()
    {
        await WriteFixtureSessionAsync("s-seg");
        var vm = MakeVm();
        await vm.LoadAsync("s-seg", CancellationToken.None);

        var allSegments = vm.Rows.SelectMany(r => r.Data.Segments).ToList();
        Assert.NotEmpty(allSegments);
        Assert.Contains(allSegments, s => s.IsCorrected);          // fixture's EditStore overlay
        Assert.Contains(vm.Rows, r => r.Data.HasCorrection);
    }

    [Fact]
    public async Task Correction_editor_factory_returns_null_for_marker_rows_only()
    {
        await WriteFixtureSessionAsync("s-fac");
        var vm = MakeVm();
        await vm.LoadAsync("s-fac", CancellationToken.None);

        int markerIdx = -1, segmentIdx = -1;
        for (int i = 0; i < vm.Rows.Count; i++)
        {
            if (vm.Rows[i].Data.IsMarker) markerIdx = i; else segmentIdx = i;
        }
        Assert.True(markerIdx >= 0 && segmentIdx >= 0);
        Assert.Null(vm.CreateCorrectionEditor(markerIdx));
        Assert.Null(vm.CreateCorrectionEditor(999));
        Assert.NotNull(vm.CreateCorrectionEditor(segmentIdx));
        Assert.NotNull(vm.CreateReassignEditor(segmentIdx));
    }

    [Fact]
    public async Task ReloadRows_refreshes_text_and_edited_badge_without_reresolving_audio()
    {
        await WriteFixtureSessionAsync("s-rel");
        // On-disk audio so the first LoadAsync actually resolves the transport (IsAvailable=true,
        // one _player.Load). Without real bytes the leg probe never fires and LoadCount stays 0
        // before AND after, making the "no re-resolve" assertion below vacuous - the whole point
        // of this test is to fail if a reload wrongly re-runs Playback.Resolve, which it only can
        // once there is a genuine Load to double.
        File.WriteAllBytes(_paths.AudioFile("s-rel", SourceKind.Local, AudioFormat.Flac), new byte[] { 1 });
        File.WriteAllBytes(_paths.AudioFile("s-rel", SourceKind.Remote, AudioFormat.Wav), new byte[] { 1 });
        var vm = MakeVm();
        await vm.LoadAsync("s-rel", CancellationToken.None);
        int loadsAfterFirst = _player.LoadCount;
        Assert.Equal(1, loadsAfterFirst);                          // guard is live: audio resolved once

        var target = vm.Rows.SelectMany(r => r.Data.Segments).First(s => !s.IsCorrected);
        await _maintenance.SaveTextCorrectionsAsync("s-rel",
            new Dictionary<int, string> { [target.Seq] = "RELOADED TEXT" },
            Array.Empty<int>(), CancellationToken.None);

        await vm.ReloadRowsAsync(CancellationToken.None);

        Assert.Contains(vm.Rows, r => r.Data.Text.Contains("RELOADED TEXT"));
        Assert.True(vm.Edited);
        Assert.Equal(loadsAfterFirst, _player.LoadCount);          // no Playback.Resolve re-run
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

    private sealed class FakePlayer : IDualAudioPlayer
    {
        public string? LoadedLocal, LoadedRemote;
        public bool LoadCalled;
        public int LoadCount { get; private set; }
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public event Action? MediaReady;
        public event Action? MediaEnded;
        public void Load(string? localPath, string? remotePath)
        {
            LoadCalled = true;
            LoadCount++;
            (LoadedLocal, LoadedRemote) = (localPath, remotePath);
        }
        public void Play() { }
        public void Pause() { }
        public void SeekMs(long ms) => PositionMs = ms;
        public void SetLegMuted(bool local, bool muted) { }
        public void SetLegVolume(bool local, double volume) { }
        public void Dispose() { }
        public void RaiseReady() => MediaReady?.Invoke();
        public void RaiseEnded() => MediaEnded?.Invoke();
    }
}
