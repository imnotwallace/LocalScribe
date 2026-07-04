using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Diarisation;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MaintenanceServiceDiarisationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_diar_{Guid.NewGuid():N}");

    // audioRetention is a parameter (final-review test-hardening): the no-delete firewall in
    // SaveDiarisationAsync must hold for EVERY Settings.AudioRetention value ("keep" the default,
    // "never", and the legacy-migration-only "days:N" shape), not merely the default "keep" the
    // original test happened to exercise. See Writes_speakers_flips_diarised_regenerates_and_keeps_audio.
    private (MaintenanceService svc, StoragePaths paths, string id) MakeFinalizedSession(string audioRetention = "keep")
    {
        var paths = new StoragePaths(_root);
        string id = "s1";
        Directory.CreateDirectory(paths.SessionDir(id));

        // Finalized session with a retained Remote leg + two remote segments.
        new SessionStore(paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, StartedAtUtc = DateTimeOffset.UnixEpoch,
            EndedAtUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            RetainedAudioSources = [SourceKind.Remote],
        }, default).GetAwaiter().GetResult();
        new MetadataStore(paths.MetaJson(id)).SaveAsync(
            new SessionMeta { RemoteCount = 2 }, default).GetAwaiter().GetResult();
        var jsonl = new TranscriptStore(paths.TranscriptJsonl(id));
        jsonl.AppendAsync(TranscriptLine.Segment(3, TranscriptSource.Remote, 0, 1000, "hello", "Them"), default).GetAwaiter().GetResult();
        jsonl.AppendAsync(TranscriptLine.Segment(4, TranscriptSource.Remote, 1000, 2000, "world", "Them"), default).GetAwaiter().GetResult();
        // A retained leg file so the no-delete firewall has something to protect.
        File.WriteAllBytes(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac), [1, 2, 3]);

        var settings = new FakeSettingsService(new Settings { AudioRetention = audioRetention });
        var svc = new MaintenanceService(paths, settings, new FakeRecycleBin(), TimeProvider.System);
        return (svc, paths, id);
    }

    private static DiarisationCommit RemoteCommit() => new(
        [SourceKind.Remote],
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        { ["Remote"] = new Dictionary<string, string> { ["3"] = "Remote:0", ["4"] = "Remote:1" } },
        new Dictionary<string, string> { ["Remote:0"] = "Remote Speaker 1", ["Remote:1"] = "Remote Speaker 2" },
        "sherpa", DateTimeOffset.UnixEpoch);

    // Theory over every Settings.AudioRetention shape (final-review test-hardening): "keep" (the
    // default), "never", and the legacy-migration-only "days:N" form (see Settings.AudioRetention /
    // memory note - "keep" is permanent-retention-by-default and days:N is never written by current
    // code, only read on migration). MaintenanceService.SaveDiarisationAsync's comment states it
    // NEVER deletes/touches audio "for any AudioRetention value" - this hardens that firewall
    // against a future afterDiarisation-wiring regression that scopes deletion to only SOME
    // retention values instead of none.
    [Theory]
    [InlineData("keep")]
    [InlineData("never")]
    [InlineData("days:30")]
    public async Task Writes_speakers_flips_diarised_regenerates_and_keeps_audio(string audioRetention)
    {
        var (svc, paths, id) = MakeFinalizedSession(audioRetention);

        await svc.SaveDiarisationAsync(id, RemoteCommit(), default);

        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.NotNull(speakers);
        Assert.Equal("Remote:0", speakers!.Assignments["Remote"]["3"]);

        var session = await new SessionStore(paths.SessionJson(id)).ReadAsync(default);
        Assert.True(session!.Diarised);

        // Projection re-rendered with the resolved names.
        string md = await File.ReadAllTextAsync(paths.TranscriptTxt(id));
        Assert.Contains("Remote Speaker 1", md);

        // Firewall: the retained leg is untouched, regardless of the AudioRetention value above.
        Assert.True(File.Exists(paths.AudioFile(id, SourceKind.Remote, AudioFormat.Flac)));
    }

    [Fact]
    public async Task Rediarise_preserves_a_prior_pinned_assignment()
    {
        var (svc, paths, id) = MakeFinalizedSession();
        // Seed a pinned reassignment on seq 3 via the existing EditStore path.
        await new EditStore(paths.SessionDir(id), TimeProvider.System)
            .ReassignSpeakerAsync(3, TranscriptSource.Remote, "Remote:custom", default);

        await svc.SaveDiarisationAsync(id, RemoteCommit(), default);

        var speakers = await new SpeakersStore(paths.SpeakersJson(id)).LoadAsync(default);
        Assert.Equal("Remote:custom", speakers!.Assignments["Remote"]["3"]);   // pin survived
        Assert.Equal("Remote:1", speakers.Assignments["Remote"]["4"]);          // non-pin took the run
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }
}
