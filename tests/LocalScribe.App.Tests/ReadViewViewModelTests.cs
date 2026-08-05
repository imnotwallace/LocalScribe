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
                new SessionParticipant { Id = "p-jane-doe", Name = "Jane", Role = "Counsel", Side = SourceKind.Remote },
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
        // Side (Local/Remote) is dropped; Role is user-entered and kept (design 2026-08-03 sec 7).
        Assert.Contains("Sam", vm.ParticipantDisplays);
        Assert.Contains("Jane (Counsel)", vm.ParticipantDisplays);
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
    public void SeekSegment_seeks_to_the_given_ms_and_starts_playback()
    {
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 0, EndMs = 4200, DisplayName = "Sam", Text = "a" }));

        vm.SeekSegment(138720);
        Assert.Equal(138720, vm.Playback.PositionMs);
        Assert.True(vm.Playback.IsPlaying);

        vm.SeekSegment(130208);                  // a second seek while playing stays playing, moves position
        Assert.Equal(130208, vm.Playback.PositionMs);
        Assert.True(vm.Playback.IsPlaying);
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
    public void PlayingSegment_IsNowPlaying_follows_position_within_a_turn_and_clears_across_rows()
    {
        var vm = MakeVm();
        // One merged Christine turn (2 segments) then a Nel turn (1 segment).
        RowSegment S(int seq, long a, long b) =>
            new(seq, TranscriptSource.Local, a, b, "t", "t", false, false);
        vm.Rows.Add(new ReadRow(new DisplayRow
        {
            StartMs = 130208, EndMs = 143104, DisplayName = "Christine", Text = "a b",
            Segments = new[] { S(25, 130208, 136320), S(27, 138720, 143104) },
        }));
        vm.Rows.Add(new ReadRow(new DisplayRow
        {
            StartMs = 150000, EndMs = 152000, DisplayName = "Nel", Text = "c",
            Segments = new[] { S(30, 150000, 152000) },
        }));

        _player.PositionMs = 131000; vm.TickPlayback();             // inside seg 25
        Assert.True(vm.Rows[0].Segments[0].IsNowPlaying);
        Assert.False(vm.Rows[0].Segments[1].IsNowPlaying);

        _player.PositionMs = 137000; vm.TickPlayback();             // intra-turn gap (136320..138720): holds seg 25
        Assert.True(vm.Rows[0].Segments[0].IsNowPlaying);
        Assert.False(vm.Rows[0].Segments[1].IsNowPlaying);

        _player.PositionMs = 139000; vm.TickPlayback();             // inside seg 27
        Assert.False(vm.Rows[0].Segments[0].IsNowPlaying);
        Assert.True(vm.Rows[0].Segments[1].IsNowPlaying);

        _player.PositionMs = 151000; vm.TickPlayback();             // moved to the Nel turn
        Assert.False(vm.Rows[0].Segments[0].IsNowPlaying);
        Assert.False(vm.Rows[0].Segments[1].IsNowPlaying);          // prior turn's segment cleared
        Assert.True(vm.Rows[1].Segments[0].IsNowPlaying);
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
    public async Task Reassign_cluster_editor_falls_back_to_label_on_a_pinless_session()
    {
        // Regression (2026-07-31): the fixture writes NO speakers.json, so _loadedSpeakers is null -
        // exactly the state of an import whose speaker detection found "one voice" (every line under
        // the default label, no overlay). "Reassign all of this speaker" must still open, gathering by
        // the displayed label so the import is bulk-triageable. An over-strict `_loadedSpeakers is null`
        // guard used to refuse it outright (returned null -> the read view showed the info box instead).
        await WriteFixtureSessionAsync("s-pinless");
        var vm = MakeVm();
        await vm.LoadAsync("s-pinless", CancellationToken.None);

        int markerIdx = -1, segmentIdx = -1;
        for (int i = 0; i < vm.Rows.Count; i++)
            if (vm.Rows[i].Data.IsMarker) markerIdx = i; else segmentIdx = i;
        Assert.True(markerIdx >= 0 && segmentIdx >= 0);

        Assert.NotNull(vm.CreateReassignClusterEditor(segmentIdx));   // was null before the guard fix
        Assert.Null(vm.CreateReassignClusterEditor(markerIdx));       // a marker still has nothing to gather
    }

    [Fact]
    public async Task Search_all_sessions_carries_the_current_find_term()
    {
        await WriteFixtureSessionAsync("read-find");
        var vm = MakeVm();
        await vm.LoadAsync("read-find", CancellationToken.None);
        vm.OpenFind("privilege");
        string? requested = null;
        vm.SearchAllSessionsRequested += term => requested = term;

        vm.RequestSearchAllSessions();
        Assert.Equal("privilege", requested);

        vm.FindText = "";                              // empty term is allowed (opens Search blank)
        vm.RequestSearchAllSessions();
        Assert.Equal("", requested);
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
            Array.Empty<int>(), "v1", CancellationToken.None);

        await vm.ReloadRowsAsync(CancellationToken.None);

        Assert.Contains(vm.Rows, r => r.Data.Text.Contains("RELOADED TEXT"));
        Assert.True(vm.Edited);
        Assert.Equal(loadsAfterFirst, _player.LoadCount);          // no Playback.Resolve re-run
    }

    [Fact]
    public async Task SyncTranscript_survives_an_edit_mode_round_trip()
    {
        // Item 7: the toggle is inert while editing (view-layer disable) but its STATE must
        // survive Edit -> Cancel so follow re-engages on return to read mode.
        await WriteFixtureSessionAsync("read-sync");
        var vm = MakeVm();
        await vm.LoadAsync("read-sync", CancellationToken.None);

        vm.Playback.SyncTranscript = true;
        vm.EnterEditMode();
        Assert.True(vm.IsEditMode);
        Assert.True(vm.Playback.SyncTranscript);
        vm.CancelEdit();
        Assert.False(vm.IsEditMode);
        Assert.True(vm.Playback.SyncTranscript);
    }

    [Fact]
    public void PlayingSectionIndex_fires_once_per_row_advance_not_per_tick()
    {
        // Pin the contract the window's follow-scroll hook depends on: [ObservableProperty]
        // equality-gates same-value sets, so PropertyChanged fires once per row ADVANCE, never
        // per 150 ms tick - and the -1 sentinel (before the first row) never fires from -1.
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 1000, EndMs = 1500, DisplayName = "Sam", Text = "a" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 1600, EndMs = 3000, DisplayName = "Sam", Text = "b" }));

        int fired = 0;
        vm.PropertyChanged += (_, e) =>
        { if (e.PropertyName == nameof(ReadViewViewModel.PlayingSectionIndex)) fired++; };

        _player.PositionMs = 0;    vm.TickPlayback();   // before the first row: stays -1
        Assert.Equal(0, fired);
        Assert.Equal(-1, vm.PlayingSectionIndex);        // -1 sentinel: the window must never scroll
        _player.PositionMs = 1000; vm.TickPlayback();   // -1 -> 0
        _player.PositionMs = 1200; vm.TickPlayback();   // same row: equality-gated, no event
        _player.PositionMs = 1400; vm.TickPlayback();
        Assert.Equal(1, fired);
        _player.PositionMs = 1600; vm.TickPlayback();   // 0 -> 1
        Assert.Equal(2, fired);
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

    [Fact]
    public void GoToTimestamp_parses_seeks_and_requests_a_one_shot_scroll()
    {
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 0,    EndMs = 1500, DisplayName = "Sam",  Text = "a" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 1600, EndMs = 3000, DisplayName = "Sam",  Text = "b" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 3200, EndMs = 4200, DisplayName = "Jane", Text = "c" }));

        int? scrolledTo = null;
        vm.GoToRowScrollRequested += i => scrolledTo = i;

        vm.GoToText = "00:03";                       // TimestampsMode defaults to "relative"
        vm.GoToTimestamp();

        Assert.False(vm.GoToError);
        Assert.Equal(3000, vm.Playback.PositionMs);
        Assert.Equal(1, scrolledTo);                 // row window [1600, 3200)
        vm.TickPlayback();                           // the highlight lands on the next tick
        Assert.Equal(1, vm.PlayingSectionIndex);
    }

    [Fact]
    public void GoToTimestamp_clamps_to_duration_and_targets_the_last_section()
    {
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 0,    EndMs = 1500, DisplayName = "Sam",  Text = "a" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 1600, EndMs = 3000, DisplayName = "Sam",  Text = "b" }));
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 3200, EndMs = 4200, DisplayName = "Jane", Text = "c" }));
        _player.DurationMs = 4200;
        _player.RaiseReady();                        // publishes DurationMs (dispatch runs inline)

        int? scrolledTo = null;
        vm.GoToRowScrollRequested += i => scrolledTo = i;
        vm.GoToText = "59:59";
        vm.GoToTimestamp();

        Assert.False(vm.GoToError);
        Assert.Equal(4200, vm.Playback.PositionMs);  // clamped by Seek, never past end-of-media
        Assert.Equal(2, scrolledTo);                 // last row owns its inclusive EndMs
    }

    [Fact]
    public void GoToTimestamp_invalid_input_sets_the_quiet_error_and_keeps_text_and_position()
    {
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 0, EndMs = 1500, DisplayName = "Sam", Text = "a" }));
        bool scrolled = false;
        vm.GoToRowScrollRequested += _ => scrolled = true;

        // UX round 2026-08-03: GoToText is now auto-colon masked (TimestampMask), so a wholly
        // non-numeric string like "not a time" would mask away to "" before it ever reached the
        // parser - it can no longer exercise this path. "14" survives the mask unchanged (only
        // one completed pair, no trailing colon to insert) but still fails TimestampParser: a
        // bare 2-digit run with no colon splits to a single field, which TryParse rejects
        // outright (needs 2 or 3 colon-separated fields). Same invalid-input intent, still real
        // after masking.
        vm.GoToText = "14";
        vm.GoToTimestamp();

        Assert.True(vm.GoToError);
        Assert.Equal("14", vm.GoToText);              // retained - quiet inline error, no dialog
        Assert.Equal(0, vm.Playback.PositionMs);      // no seek happened
        Assert.False(scrolled);

        vm.GoToText = "00:0";                        // ANY edit clears the error state
        Assert.False(vm.GoToError);
    }

    [Fact]
    public void GoToText_setter_auto_colon_masks_typed_digits_left_anchored()
    {
        // Confirms the CommunityToolkit generated-setter re-entrancy the mask relies on: the
        // OnGoToTextChanged partial reassigns GoToText to the masked value, which re-enters the
        // same setter; the equality gate (masked value is already idempotent under Format) stops
        // the recursion on the very next call instead of looping.
        var vm = MakeVm();

        vm.GoToText = "1";
        Assert.Equal("1", vm.GoToText);

        vm.GoToText = "141530";
        Assert.Equal("14:15:30", vm.GoToText);

        vm.GoToText = "14:15";                        // pasting an already-colonised stamp re-masks cleanly
        Assert.Equal("14:15", vm.GoToText);
    }

    [Fact]
    public async Task GoToPlaceholder_is_HHmmss_for_wallclock_sessions_regardless_of_duration()
    {
        // Fixture session is only 10 minutes (DurationMs=600_000, well under an hour) - proves the
        // wallclock shape wins outright and never falls through to the duration-based relative
        // shapes, matching TimestampFormat.Stamp's own mode-first branch.
        await _settings.SaveAsync(_settings.Current with { Timestamps = "wallclock" }, CancellationToken.None);
        await WriteFixtureSessionAsync("read-wallclock");
        var vm = MakeVm();
        await vm.LoadAsync("read-wallclock", CancellationToken.None);

        Assert.Equal("HH:mm:ss", vm.GoToPlaceholder);
    }

    [Fact]
    public void GoToPlaceholder_is_hmmss_for_relative_sessions_an_hour_or_longer()
    {
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 0, EndMs = 3_700_000, DisplayName = "Sam", Text = "a" }));

        // No duration resolved yet: falls back to the loaded rows' last timestamp (>= 1h here).
        Assert.Equal("h:mm:ss", vm.GoToPlaceholder);
    }

    [Fact]
    public void GoToPlaceholder_is_mmss_for_relative_sessions_under_an_hour()
    {
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 0, EndMs = 4200, DisplayName = "Sam", Text = "a" }));

        Assert.Equal("mm:ss", vm.GoToPlaceholder);
    }

    [Fact]
    public void GoToPlaceholder_defaults_to_mmss_before_any_rows_or_duration_are_known()
    {
        var vm = MakeVm();
        Assert.Equal("mm:ss", vm.GoToPlaceholder);
    }

    [Fact]
    public void GoToPlaceholder_reraises_PropertyChanged_once_DurationMs_resolves_late()
    {
        // Playback.DurationMs resolves asynchronously after MediaReady - GoToPlaceholder must not
        // be frozen at construction. Before the media loads, the rows fallback is under an hour,
        // so the shorter shape shows; once DurationMs resolves past an hour, the hint must flip
        // to the longer shape AND announce it via PropertyChanged so a live binding refreshes.
        var vm = MakeVm();
        vm.Rows.Add(new ReadRow(new DisplayRow { StartMs = 0, EndMs = 4200, DisplayName = "Sam", Text = "a" }));
        Assert.Equal("mm:ss", vm.GoToPlaceholder);

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        _player.DurationMs = 3_700_000;
        _player.RaiseReady();

        Assert.Contains(nameof(ReadViewViewModel.GoToPlaceholder), raised);
        Assert.Equal("h:mm:ss", vm.GoToPlaceholder);
    }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message, bool privileged = true) => Infos.Add(message);
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
