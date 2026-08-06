using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

/// <summary>The Core-side retained-leg probe (Tier 1B, T1-2). It exists because neither existing
/// probe can re-derive `retained`: AudioLegProbe lives in LocalScribe.App.Services (Core cannot
/// reference it) and RetranscriptionRunner.ResolveLegs is private AND returns null the moment
/// !retained.Contains(kind) - i.e. both consult the very list this probe has to rebuild.</summary>
public sealed class RetainedAudioProbeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-legprobe-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public RetainedAudioProbeTests() => _paths = new StoragePaths(_root);
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private void WriteLeg(string id, SourceKind kind, AudioFormat format)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        using var sink = AudioSinkFactory.Create(_paths.AudioFile(id, kind, format), format);
        sink.Write(new float[1600]);                       // 100 ms of silence
    }

    [Fact]
    public void Finds_nothing_for_a_session_with_no_audio()
    {
        Directory.CreateDirectory(_paths.SessionDir("s1"));
        Assert.Empty(RetainedAudioProbe.Legs(_paths, "s1"));
    }

    [Fact]
    public void Finds_nothing_for_a_session_folder_that_does_not_exist()
        => Assert.Empty(RetainedAudioProbe.Legs(_paths, "never-existed"));

    [Fact]
    public void Returns_local_before_remote_matching_the_live_feed_order()
    {
        WriteLeg("s1", SourceKind.Remote, AudioFormat.Flac);
        WriteLeg("s1", SourceKind.Local, AudioFormat.Flac);

        var legs = RetainedAudioProbe.Legs(_paths, "s1");

        Assert.Equal(new[] { SourceKind.Local, SourceKind.Remote }, legs.Select(l => l.Kind));
        Assert.Equal(_paths.AudioFile("s1", SourceKind.Local, AudioFormat.Flac), legs[0].Path);
    }

    [Fact]
    public void Falls_back_to_wav_so_a_session_recorded_before_a_format_change_still_resolves()
    {
        // SessionWriter is constructed with settings.Current, i.e. the format the user has
        // configured NOW - not the one the crashed session recorded in. Probing only the
        // preferred format would lose a WAV session on a machine since switched to FLAC.
        WriteLeg("s1", SourceKind.Local, AudioFormat.Wav);

        var leg = Assert.Single(RetainedAudioProbe.Legs(_paths, "s1"));

        Assert.Equal(SourceKind.Local, leg.Kind);
        Assert.EndsWith("local.wav", leg.Path);
    }

    [Fact]
    public void Prefers_flac_when_both_containers_somehow_exist()
    {
        WriteLeg("s1", SourceKind.Local, AudioFormat.Wav);
        WriteLeg("s1", SourceKind.Local, AudioFormat.Flac);

        var leg = Assert.Single(RetainedAudioProbe.Legs(_paths, "s1"));

        Assert.EndsWith("local.flac", leg.Path);
    }
}
