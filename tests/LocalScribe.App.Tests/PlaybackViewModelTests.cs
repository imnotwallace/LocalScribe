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
