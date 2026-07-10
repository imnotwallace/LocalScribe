using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class PlaybackViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-playback-vm-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly FakePlayer _player = new();

    public PlaybackViewModelTests() => _paths = new StoragePaths(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private PlaybackViewModel MakeVm() => new(_player, dispatch: a => a());

    private void WriteAudio(string sessionId, SourceKind kind, AudioFormat format)
    {
        Directory.CreateDirectory(_paths.SessionDir(sessionId));
        File.WriteAllBytes(_paths.AudioFile(sessionId, kind, format), new byte[] { 1 });
    }

    [Fact]
    public void Resolve_probes_disk_per_leg_and_prefers_the_settings_format()
    {
        // Local exists in the preferred format; remote predates a format change (wav only).
        WriteAudio("s-audio", SourceKind.Local, AudioFormat.Flac);
        WriteAudio("s-audio", SourceKind.Remote, AudioFormat.Wav);

        var vm = MakeVm();
        vm.Resolve(_paths, "s-audio", new[] { SourceKind.Local, SourceKind.Remote }, AudioFormat.Flac);

        Assert.True(vm.IsAvailable);
        Assert.True(vm.HasLocalLeg);
        Assert.True(vm.HasRemoteLeg);
        Assert.Equal(_paths.AudioFile("s-audio", SourceKind.Local, AudioFormat.Flac), _player.LoadedLocal);
        Assert.Equal(_paths.AudioFile("s-audio", SourceKind.Remote, AudioFormat.Wav), _player.LoadedRemote);
        Assert.True(vm.PlayPauseCommand.CanExecute(null));
    }

    [Fact]
    public void Resolve_with_one_retained_leg_degrades_to_that_leg()
    {
        WriteAudio("s-one", SourceKind.Remote, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-one", new[] { SourceKind.Remote }, AudioFormat.Flac);

        Assert.True(vm.IsAvailable);
        Assert.False(vm.HasLocalLeg);
        Assert.True(vm.HasRemoteLeg);
        Assert.Null(_player.LoadedLocal);
    }

    [Fact]
    public void Resolve_without_any_files_hides_the_transport()
    {
        // Retention "never" / files gone: retained list says Local but nothing is on disk.
        var vm = MakeVm();
        vm.Resolve(_paths, "s-none", new[] { SourceKind.Local }, AudioFormat.Flac);

        Assert.False(vm.IsAvailable);
        Assert.False(_player.LoadCalled);                            // never load a missing file
        Assert.False(vm.PlayPauseCommand.CanExecute(null));
    }

    [Fact]
    public void PlayPause_toggles_and_drives_both_legs_via_the_player()
    {
        WriteAudio("s-pp", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-pp", new[] { SourceKind.Local }, AudioFormat.Flac);

        vm.PlayPauseCommand.Execute(null);
        Assert.True(vm.IsPlaying);
        Assert.Contains("Play", _player.Calls);
        vm.PlayPauseCommand.Execute(null);
        Assert.False(vm.IsPlaying);
        Assert.Contains("Pause", _player.Calls);
    }

    [Fact]
    public void PlayPauseCaption_reflects_IsPlaying()
    {
        WriteAudio("s-cap", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-cap", new[] { SourceKind.Local }, AudioFormat.Flac);

        Assert.Equal("Play", vm.PlayPauseCaption);
        vm.PlayPauseCommand.Execute(null);
        Assert.Equal("Pause", vm.PlayPauseCaption);
        vm.PlayPauseCommand.Execute(null);
        Assert.Equal("Play", vm.PlayPauseCaption);
    }

    [Fact]
    public void MediaReady_publishes_duration_and_MediaEnded_stops()
    {
        WriteAudio("s-dur", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-dur", new[] { SourceKind.Local }, AudioFormat.Flac);

        _player.DurationMs = 65_000;
        _player.RaiseReady();
        Assert.Equal(65_000, vm.DurationMs);
        Assert.Equal("01:05", vm.DurationDisplay);                   // mm:ss under an hour

        vm.PlayPauseCommand.Execute(null);
        _player.RaiseEnded();
        Assert.False(vm.IsPlaying);
    }

    [Fact]
    public void MediaEnded_holds_at_end_and_next_play_seeks_to_zero()
    {
        WriteAudio("s-end", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-end", new[] { SourceKind.Local }, AudioFormat.Flac);
        _player.DurationMs = 30_000;
        _player.RaiseReady();

        vm.PlayPauseCommand.Execute(null);                 // playing
        _player.RaiseEnded();
        Assert.False(vm.IsPlaying);
        Assert.True(vm.EndReached);
        Assert.Equal(30_000, vm.PositionMs);               // held at end, no auto-rewind
        Assert.Equal("Play", vm.PlayPauseCaption);

        _player.Calls.Clear();
        vm.PlayPauseCommand.Execute(null);                 // replay
        Assert.Equal(new[] { "Seek:0", "Play" }, _player.Calls);   // seek-to-0 precedes play
        Assert.Equal(0, vm.PositionMs);
        Assert.False(vm.EndReached);
        Assert.True(vm.IsPlaying);
    }

    [Fact]
    public void Long_durations_render_h_mm_ss()
    {
        WriteAudio("s-long", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-long", new[] { SourceKind.Local }, AudioFormat.Flac);

        _player.DurationMs = 3_665_000;                              // 1:01:05
        _player.RaiseReady();
        Assert.Equal("1:01:05", vm.DurationDisplay);
        _player.PositionMs = 3_600_000;
        vm.Tick();
        Assert.Equal("1:00:00", vm.PositionDisplay);
    }

    [Fact]
    public void Tick_polls_position_and_Seek_forwards_to_the_player()
    {
        WriteAudio("s-seek", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-seek", new[] { SourceKind.Local }, AudioFormat.Flac);

        _player.PositionMs = 42_000;
        vm.Tick();                                                   // 150 ms timer pattern; tests call directly
        Assert.Equal(42_000, vm.PositionMs);
        Assert.Equal("00:42", vm.PositionDisplay);

        vm.Seek(90_000);
        Assert.Contains("Seek:90000", _player.Calls);
        Assert.Equal(90_000, vm.PositionMs);
    }

    [Fact]
    public void Tick_is_suppressed_while_scrubbing_but_Seek_still_applies()
    {
        WriteAudio("s-scrub", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-scrub", new[] { SourceKind.Local }, AudioFormat.Flac);

        vm.Seek(10_000);
        Assert.Equal(10_000, vm.PositionMs);

        vm.IsScrubbing = true;
        _player.PositionMs = 50_000;                 // player advanced under the hood
        vm.Tick();                                   // must NOT drag the thumb back to 50s
        Assert.Equal(10_000, vm.PositionMs);

        vm.Seek(20_000);                             // an explicit seek still lands mid-scrub
        Assert.Equal(20_000, vm.PositionMs);          // (this also drives the player to 20_000)

        vm.IsScrubbing = false;
        _player.PositionMs = 65_000;                 // playback continues on from the seek point
        vm.Tick();                                   // polling resumes
        Assert.Equal(65_000, vm.PositionMs);
    }

    [Fact]
    public void Seek_after_end_of_media_is_not_clobbered_by_replay()
    {
        WriteAudio("s-seek-end", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-seek-end", new[] { SourceKind.Local }, AudioFormat.Flac);
        _player.DurationMs = 30_000;
        _player.RaiseReady();

        vm.PlayPauseCommand.Execute(null);                 // playing
        _player.RaiseEnded();
        Assert.True(vm.EndReached);

        vm.Seek(15_000);                                   // manual seek after end-of-media
        Assert.False(vm.EndReached);                        // must clear the held-at-end state
        Assert.Equal(15_000, vm.PositionMs);

        _player.Calls.Clear();
        vm.PlayPauseCommand.Execute(null);                 // resume, NOT replay-from-zero
        Assert.DoesNotContain("Seek:0", _player.Calls);
        Assert.Contains("Play", _player.Calls);
        Assert.Equal(15_000, vm.PositionMs);
    }

    [Fact]
    public void Per_leg_mute_toggles_route_to_the_right_leg()
    {
        WriteAudio("s-mute", SourceKind.Local, AudioFormat.Flac);
        WriteAudio("s-mute", SourceKind.Remote, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-mute", new[] { SourceKind.Local, SourceKind.Remote }, AudioFormat.Flac);

        vm.LocalMuted = true;
        Assert.Contains("Mute:local:True", _player.Calls);
        vm.RemoteMuted = true;
        Assert.Contains("Mute:remote:True", _player.Calls);
        vm.LocalMuted = false;
        Assert.Contains("Mute:local:False", _player.Calls);
    }

    [Fact]
    public void Per_leg_volume_routes_to_the_right_leg()
    {
        WriteAudio("s-vol", SourceKind.Local, AudioFormat.Flac);
        WriteAudio("s-vol", SourceKind.Remote, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-vol", new[] { SourceKind.Local, SourceKind.Remote }, AudioFormat.Flac);

        Assert.Equal(1.0, vm.LocalVolume);           // full by default
        Assert.Equal(1.0, vm.RemoteVolume);
        vm.LocalVolume = 0.5;
        Assert.Contains("Vol:local:0.5", _player.Calls);
        vm.RemoteVolume = 0.25;
        Assert.Contains("Vol:remote:0.25", _player.Calls);
    }

    [Fact]
    public void Dispose_disposes_the_player()
    {
        var vm = MakeVm();
        vm.Dispose();
        Assert.Contains("Dispose", _player.Calls);
    }

    [Fact]
    public void Stop_pauses_rewinds_and_clears_state()
    {
        WriteAudio("s-stop", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-stop", new[] { SourceKind.Local }, AudioFormat.Flac);
        vm.PlayPauseCommand.Execute(null);           // playing
        _player.PositionMs = 12_345;
        vm.Tick();
        _player.Calls.Clear();

        vm.Stop();

        Assert.Contains("Pause", _player.Calls);
        Assert.Contains("Seek:0", _player.Calls);
        Assert.Equal(0, vm.PositionMs);
        Assert.Equal("00:00", vm.PositionDisplay);
        Assert.False(vm.IsPlaying);
        Assert.False(vm.EndReached);
        Assert.Equal("Play", vm.PlayPauseCaption);
        Assert.True(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public void Tick_ignores_position_readback_beyond_duration_tolerance()
    {
        // Windows Media Foundation misreports Position on the app's small near-silent local
        // FLAC legs after a seek-then-Play: a ~23s file reads back ~54s. Tick() must reject an
        // insane readback and hold the last-known-good position rather than snap the read-view
        // bar forward (probe-verified 2026-07-06).
        WriteAudio("s-insane", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-insane", new[] { SourceKind.Local }, AudioFormat.Flac);
        _player.DurationMs = 23_000;
        _player.RaiseReady();

        vm.Seek(3_608);
        Assert.Equal(3_608, vm.PositionMs);

        _player.PositionMs = 54_064;                 // corrupted MF readback
        vm.Tick();
        Assert.Equal(3_608, vm.PositionMs);           // ignored; last-known-good retained

        _player.PositionMs = 5_000;                   // player recovers
        vm.Tick();
        Assert.Equal(5_000, vm.PositionMs);
    }

    [Fact]
    public void Tick_pins_position_slightly_beyond_reported_duration_to_duration()
    {
        // MF truncates FLAC NaturalDuration to whole seconds, so a legitimate position can
        // slightly exceed DurationMs. Within tolerance this is pinned to DurationMs, not
        // rejected outright.
        WriteAudio("s-overshoot", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-overshoot", new[] { SourceKind.Local }, AudioFormat.Flac);
        _player.DurationMs = 23_000;
        _player.RaiseReady();

        _player.PositionMs = 23_590;                  // within the 2000ms tolerance
        vm.Tick();
        Assert.Equal(23_000, vm.PositionMs);          // pinned to duration
    }

    [Fact]
    public void SliderValue_change_commits_seek_when_not_scrubbing()
    {
        // WPF Slider's own class handlers (track-click IsMoveToPointEnabled, arrow/Page/Home/End
        // command bindings) mark the routed event Handled BEFORE our XAML instance handlers run,
        // so those gestures never armed IsScrubbing under the old Preview*/KeyDown wiring. The
        // TwoWay SliderValueMs binding fires regardless of Handled state, so it must commit a
        // Seek by itself whenever the user isn't mid-drag.
        WriteAudio("s-slider1", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-slider1", new[] { SourceKind.Local }, AudioFormat.Flac);
        _player.DurationMs = 23_000;
        _player.RaiseReady();

        vm.SliderValueMs = 9_000;

        Assert.Contains("Seek:9000", _player.Calls);
        Assert.Equal(9_000, vm.PositionMs);
    }

    [Fact]
    public void SliderValue_change_during_scrub_does_not_seek()
    {
        WriteAudio("s-slider2", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-slider2", new[] { SourceKind.Local }, AudioFormat.Flac);
        _player.DurationMs = 23_000;
        _player.RaiseReady();

        vm.IsScrubbing = true;
        vm.SliderValueMs = 9_000;
        Assert.DoesNotContain("Seek:9000", _player.Calls);

        // release: the drag/track-click handler commits the final value itself
        vm.IsScrubbing = false;
        vm.Seek(vm.SliderValueMs);
        Assert.Contains("Seek:9000", _player.Calls);
    }

    [Fact]
    public void Tick_sync_does_not_echo_a_seek()
    {
        WriteAudio("s-slider3", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-slider3", new[] { SourceKind.Local }, AudioFormat.Flac);

        _player.PositionMs = 5_000;
        _player.Calls.Clear();
        vm.Tick();

        Assert.Equal(5_000, vm.SliderValueMs);
        Assert.DoesNotContain(_player.Calls, c => c.StartsWith("Seek:"));
    }

    [Fact]
    public void Seek_updates_slider_value()
    {
        WriteAudio("s-slider4", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-slider4", new[] { SourceKind.Local }, AudioFormat.Flac);
        _player.DurationMs = 23_000;
        _player.RaiseReady();

        vm.Seek(12_000);

        Assert.Equal(12_000, vm.SliderValueMs);
        Assert.Equal(1, _player.Calls.Count(c => c == "Seek:12000"));   // single Seek call, no echo loop
    }

    [Fact]
    public void Tick_after_a_seek_and_play_never_regresses_PositionDisplay_to_the_jumped_raw_value()
    {
        // VM-level reproduction of the MF FLAC clock-offset probe (2026-07-11, same numbers as
        // PlaybackClockCorrectorTests): after a paused seek + Play, the fake player's raw
        // PositionMs jumps by a constant ~3753ms offset at a 150ms tick cadence. Tick() must
        // route PositionMs through the corrector so it never snaps forward to the jumped raw
        // value - the offset is learned/applied by the third tick, well before end-of-file.
        WriteAudio("s-clockfix", SourceKind.Local, AudioFormat.Flac);
        long wall = 0;
        var vm = new PlaybackViewModel(_player, dispatch: a => a(), wallClock: () => wall);
        vm.Resolve(_paths, "s-clockfix", new[] { SourceKind.Local }, AudioFormat.Flac);
        _player.DurationMs = 60_000;
        _player.RaiseReady();

        vm.Seek(34_202);                          // paused seek: exact
        Assert.Equal(34_202, vm.PositionMs);

        vm.PlayPauseCommand.Execute(null);        // play
        Assert.True(vm.IsPlaying);

        wall = 338;
        _player.PositionMs = 38_278;              // MF's raw readback jumps ~3753ms + noise
        vm.Tick();
        Assert.True(vm.PositionMs < 35_000, $"expected a corrected estimate, got {vm.PositionMs}");
        Assert.Equal(34_540, vm.PositionMs);
        Assert.Equal(Format(34_540), vm.PositionDisplay);

        wall = 488;
        _player.PositionMs = 38_443;
        vm.Tick();
        Assert.Equal(34_690, vm.PositionMs);      // learned + applied; nowhere near the raw jump

        wall = 638;
        _player.PositionMs = 38_606;
        vm.Tick();
        Assert.Equal(34_853, vm.PositionMs);
        Assert.True(vm.PositionMs < 35_000);      // never regressed toward the jumped raw value
    }

    [Fact]
    public void Replay_after_end_resets_the_corrector_so_an_exact_readback_passes_through()
    {
        // Review Important 1: replay-from-end rewinds via a real seek-to-0. The probe never
        // established that seek-to-0+Play re-manifests the learned offset, so the replay branch
        // must route through the corrector's OnSeek(0) (like Stop() does) - otherwise an EXACT
        // post-replay readback would be corrected down by the stale offset and the display
        // would pin at 00:00 until the raw position exceeded the learned offset.
        WriteAudio("s-replayfix", SourceKind.Local, AudioFormat.Flac);
        long wall = 0;
        var vm = new PlaybackViewModel(_player, dispatch: a => a(), wallClock: () => wall);
        vm.Resolve(_paths, "s-replayfix", new[] { SourceKind.Local }, AudioFormat.Flac);
        _player.DurationMs = 60_000;
        _player.RaiseReady();

        vm.Seek(34_202);
        vm.PlayPauseCommand.Execute(null);        // play
        wall = 338; _player.PositionMs = 38_278; vm.Tick();   // pending
        wall = 488; _player.PositionMs = 38_443; vm.Tick();   // learns + applies 3753
        Assert.Equal(34_690, vm.PositionMs);

        _player.RaiseEnded();                      // held at end
        Assert.True(vm.EndReached);

        wall = 10_000;
        vm.PlayPauseCommand.Execute(null);         // replay from the top (seek-to-0 + play)
        Assert.True(vm.IsPlaying);
        Assert.Equal(0, vm.PositionMs);

        // MF reads back exact after the rewind: must pass through, NOT come back 0-clamped
        // (150 - 3753) or otherwise offset-shifted.
        wall = 10_150;
        _player.PositionMs = 150;
        vm.Tick();
        Assert.Equal(150, vm.PositionMs);

        // If the offset DOES re-manifest, one jumped reading re-applies the learned 3753.
        wall = 10_300;
        _player.PositionMs = 300 + 3_753;
        vm.Tick();
        Assert.Equal(300, vm.PositionMs);
    }

    private static string Format(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss",
            System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Seek_clamps_to_media_range()
    {
        // Transcript timestamps can exceed the retained audio; a click on such a section should
        // land at end-of-media, not request a seek past it.
        WriteAudio("s-clamp", SourceKind.Local, AudioFormat.Flac);
        var vm = MakeVm();
        vm.Resolve(_paths, "s-clamp", new[] { SourceKind.Local }, AudioFormat.Flac);
        _player.DurationMs = 23_000;
        _player.RaiseReady();

        vm.Seek(30_000);
        Assert.Contains("Seek:23000", _player.Calls);
        Assert.Equal(23_000, vm.PositionMs);

        vm.Seek(-5);
        Assert.Contains("Seek:0", _player.Calls);
        Assert.Equal(0, vm.PositionMs);
    }

    private sealed class FakePlayer : IDualAudioPlayer
    {
        public string? LoadedLocal, LoadedRemote;
        public bool LoadCalled;
        public List<string> Calls { get; } = new();
        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public event Action? MediaReady;
        public event Action? MediaEnded;

        public void Load(string? localPath, string? remotePath)
        {
            LoadCalled = true;
            (LoadedLocal, LoadedRemote) = (localPath, remotePath);
            Calls.Add("Load");
        }

        public void Play() => Calls.Add("Play");
        public void Pause() => Calls.Add("Pause");
        public void SeekMs(long ms) { PositionMs = ms; Calls.Add($"Seek:{ms}"); }
        public void SetLegMuted(bool local, bool muted) => Calls.Add($"Mute:{(local ? "local" : "remote")}:{muted}");
        public void SetLegVolume(bool local, double volume)
            => Calls.Add($"Vol:{(local ? "local" : "remote")}:{volume.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        public void Dispose() => Calls.Add("Dispose");
        public void RaiseReady() => MediaReady?.Invoke();
        public void RaiseEnded() => MediaEnded?.Invoke();
    }
}
