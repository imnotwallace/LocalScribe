using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MaintenanceServiceEditingTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 7, 9, 0, 0, TimeSpan.Zero);
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-maint-edit-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly MaintenanceService _maintenance;
    private readonly ManualUtcTimeProvider _time = new(T0);

    public MaintenanceServiceEditingTests()
    {
        _paths = new StoragePaths(_root);
        _maintenance = new MaintenanceService(_paths,
            new FakeSettings(new Settings { StorageRoot = _root }), new FakeBin(), _time);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>Finalized session with two Remote segments (seq 0, 1) and a Local one (seq 2);
    /// meta declares Remote participants Alice (owns Remote:0) and Bob (no cluster yet).</summary>
    private async Task WriteSessionAsync(string id)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = T0.AddMinutes(30),
        }, default);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            SessionMeta.CreateDefault(AppKind.Webex, T0, self: null) with
            {
                RemoteCount = 2,
                Participants =
                [
                    new SessionParticipant { Id = "p-alice", Name = "Alice", Side = SourceKind.Remote, ClusterKey = "Remote:0" },
                    new SessionParticipant { Id = "p-bob", Name = "Bob", Side = SourceKind.Remote },
                ],
            }, default);
        var transcript = new TranscriptStore(_paths.TranscriptJsonl(id));
        await transcript.AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 2000, "first remote line", "Them"), default);
        await transcript.AppendAsync(
            TranscriptLine.Segment(1, TranscriptSource.Remote, 8000, 10000, "second remote line", "Them"), default);
        await transcript.AppendAsync(
            TranscriptLine.Segment(2, TranscriptSource.Local, 12000, 14000, "a local line", "Me"), default);
    }

    [Fact]
    public async Task Save_corrections_writes_overlay_and_regenerates_projections()
    {
        await WriteSessionAsync("s1");
        bool wrote = await _maintenance.SaveTextCorrectionsAsync("s1",
            new Dictionary<int, string> { [0] = "CORRECTED remote line" },
            Array.Empty<int>(), default);

        Assert.True(wrote);
        string md = await File.ReadAllTextAsync(_paths.TranscriptMd("s1"));
        Assert.Contains("CORRECTED remote line", md);             // projection regenerated
        var edits = await new EditStore(_paths.SessionDir("s1"), _time).LoadAsync(default);
        Assert.Equal("CORRECTED remote line", edits!.Corrections["0"].Text);
    }

    [Fact]
    public async Task Save_corrections_after_delete_is_a_quiet_noop()
    {
        bool wrote = await _maintenance.SaveTextCorrectionsAsync("gone",
            new Dictionary<int, string> { [0] = "x" }, Array.Empty<int>(), default);

        Assert.False(wrote);
        Assert.False(Directory.Exists(_paths.SessionDir("gone"))
            && File.Exists(_paths.EditsJson("gone")));            // nothing resurrected
    }

    [Fact]
    public async Task Noop_batch_skips_the_projection_regen()
    {
        await WriteSessionAsync("s2");
        // Force a first render so the file exists, then capture its timestamp.
        await _maintenance.SaveTextCorrectionsAsync("s2",
            new Dictionary<int, string> { [0] = "seed" }, Array.Empty<int>(), default);
        var before = File.GetLastWriteTimeUtc(_paths.TranscriptMd("s2"));

        bool wrote = await _maintenance.SaveTextCorrectionsAsync("s2",
            new Dictionary<int, string>(), new[] { 99 }, default);   // revert of never-corrected seq

        Assert.False(wrote);
        Assert.Equal(before, File.GetLastWriteTimeUtc(_paths.TranscriptMd("s2")));
    }

    [Fact]
    public async Task Pin_to_participant_with_existing_cluster_uses_that_key()
    {
        await WriteSessionAsync("s3");
        bool wrote = await _maintenance.SaveSpeakerPinsAsync("s3", TranscriptSource.Remote,
            new[] { 1 }, new SpeakerPinTarget.Participant("p-alice"), default);

        Assert.True(wrote);
        var speakers = await new SpeakersStore(_paths.SpeakersJson("s3")).LoadAsync(default);
        Assert.Equal("Remote:0", speakers!.Assignments["Remote"]["1"]);   // Alice's owned key
        Assert.Contains("1", speakers.Pinned["Remote"]);
        string md = await File.ReadAllTextAsync(_paths.TranscriptMd("s3"));
        Assert.Contains("Alice:", md);                                    // ownership tier renders her name
    }

    [Fact]
    public async Task Pin_to_clusterless_participant_mints_a_noncolliding_key_and_records_ownership()
    {
        await WriteSessionAsync("s4");
        // Occupy Remote:0 (Alice owns it) and Remote:1 (a plain assignment) so the mint must pick Remote:2.
        await new SpeakersStore(_paths.SpeakersJson("s4")).SaveAsync(new Speakers
        {
            Assignments = new Dictionary<string, Dictionary<string, string>>
            { ["Remote"] = new() { ["0"] = "Remote:1" } },
        }, default);

        bool wrote = await _maintenance.SaveSpeakerPinsAsync("s4", TranscriptSource.Remote,
            new[] { 1 }, new SpeakerPinTarget.Participant("p-bob"), default);

        Assert.True(wrote);
        var speakers = await new SpeakersStore(_paths.SpeakersJson("s4")).LoadAsync(default);
        Assert.Equal("Remote:2", speakers!.Assignments["Remote"]["1"]);
        var meta = await new MetadataStore(_paths.MetaJson("s4")).LoadAsync(default);
        Assert.Equal("Remote:2",
            meta!.Participants.Single(p => p.Id == "p-bob").ClusterKey);   // ownership persisted
        string md = await File.ReadAllTextAsync(_paths.TranscriptMd("s4"));
        Assert.Contains("Bob:", md);
    }

    [Fact]
    public async Task Mint_path_pin_preserves_the_meta_Edited_flip()
    {
        await WriteSessionAsync("s4b");   // Bob has no cluster yet -> mint path
        var before = await new MetadataStore(_paths.MetaJson("s4b")).LoadAsync(default);
        Assert.False(before!.Edited);                                     // first-ever edit
        Assert.Null(before.LastEditedAtUtc);

        bool wrote = await _maintenance.SaveSpeakerPinsAsync("s4b", TranscriptSource.Remote,
            new[] { 1 }, new SpeakerPinTarget.Participant("p-bob"), default);

        Assert.True(wrote);
        var meta = await new MetadataStore(_paths.MetaJson("s4b")).LoadAsync(default);
        Assert.True(meta!.Edited);                                        // EditStore's MarkEditedAsync flip survives
        Assert.Equal(T0, meta.LastEditedAtUtc);                           // the ownership save did not revert it
        Assert.Equal("Remote:1",
            meta.Participants.Single(p => p.Id == "p-bob").ClusterKey);   // ...and ownership still landed (only Remote:0 taken)
    }

    [Fact]
    public async Task Pin_to_unknown_participant_reports_argument_error()
    {
        await WriteSessionAsync("s5");
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _maintenance.SaveSpeakerPinsAsync("s5", TranscriptSource.Remote,
                new[] { 1 }, new SpeakerPinTarget.Participant("p-nobody"), default));
    }

    [Fact]
    public async Task Remove_pins_regenerates_and_falls_back()
    {
        await WriteSessionAsync("s6");
        await _maintenance.SaveSpeakerPinsAsync("s6", TranscriptSource.Remote,
            new[] { 1 }, new SpeakerPinTarget.Participant("p-alice"), default);
        Assert.Contains("Alice:", await File.ReadAllTextAsync(_paths.TranscriptMd("s6")));

        bool removed = await _maintenance.RemoveSpeakerPinsAsync("s6", TranscriptSource.Remote,
            new[] { 1 }, default);

        Assert.True(removed);
        string md = await File.ReadAllTextAsync(_paths.TranscriptMd("s6"));
        Assert.DoesNotContain("Alice:", md);                              // back to baseline "Them"
    }

    [Fact]
    public async Task Pin_after_delete_is_a_quiet_noop()
    {
        bool wrote = await _maintenance.SaveSpeakerPinsAsync("gone2", TranscriptSource.Remote,
            new[] { 0 }, new SpeakerPinTarget.Cluster("Remote:0"), default);
        Assert.False(wrote);
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
}
