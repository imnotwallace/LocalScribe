// tests/LocalScribe.App.Tests/InsertLineViewModelTests.cs
using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>VM tests for the read view's "Add line here..." dialog (Task 10, design 2026-08-11
/// section 2.3).
///
/// Adaptation note: the task brief's own test bodies assert against `_maintenance.SavedBatches` /
/// `_maintenance.SavedVersionIds`, as if MaintenanceService were fakeable. It is not - it is a
/// `sealed class` (MaintenanceService.cs), and grepping every MaintenanceService reference across
/// tests/LocalScribe.App.Tests confirms NOT ONE test double exists for it anywhere on this branch;
/// every VM that consumes it (CorrectTextViewModelTests, ReadViewVersionSwitchTests,
/// MaintenanceServiceEditorTests - the file that tested this very SaveTranscriptEditsAsync
/// insertions leg for Task 7) drives the real service over a temp-directory StoragePaths root. This
/// file follows that established precedent instead, the same kind of adaptation
/// EditableSectionViewModelTests already documents for this brief's fixture code (its
/// "MachineSeg(seq: 0, ...)" example is not legal C#). NewVmAsync seeds a real finalized session;
/// the pseudocode SavedBatches/SavedVersionIds assertions become real-storage checks via
/// EditStore.LoadAsync and StoragePaths.EditsJson.</summary>
public sealed class InsertLineViewModelTests : IDisposable
{
    private static readonly DateTimeOffset StartedAtLocal = new(2026, 7, 7, 9, 0, 0, TimeSpan.Zero);
    private readonly List<string> _roots = new();
    private readonly FakeUiErrorReporter _reporter = new();

    public void Dispose()
    {
        foreach (var root in _roots)
            try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static SessionParticipant Named(string id, string name, SourceKind side)
        => new() { Id = id, Name = name, Side = side, Kind = ParticipantKind.Named };

    // Accepted but deliberately never forwarded into the VM (InsertLineViewModel's constructor
    // takes SessionMeta only - see ambiguity resolution #2, ClusterKey choices are not offered).
    // Persisted to disk anyway so the ONE test that passes it proves the dropdown ignores a REAL
    // speakers.json on disk, not just an object the VM was never given a route to read.
    private static Speakers SpeakersWithNamedCluster(string clusterKey, string name)
        => new() { Names = new Dictionary<string, string> { [clusterKey] = name } };

    /// <summary>Seeds a real finalized session (session.json + meta.json + transcript.jsonl, plus a
    /// versioned copy when <paramref name="versionId"/> is not root) under a fresh temp
    /// StoragePaths root, then constructs the VM against it - mirrors
    /// MaintenanceServiceEditorTests.EditorHarness / ReadViewVersionSwitchTests.SeedAsync.</summary>
    private async Task<(InsertLineViewModel Vm, StoragePaths Paths, string SessionId)> NewVmAsync(
        long seedStartMs = 0, string timestampsMode = "relative",
        IReadOnlyList<SessionParticipant>? participants = null, Speakers? speakers = null,
        long durationMs = 0, string versionId = TranscriptVersions.Root)
    {
        string root = Path.Combine(Path.GetTempPath(), $"ls_insertline_{Guid.NewGuid():N}");
        _roots.Add(root);
        var paths = new StoragePaths(root);
        const string sessionId = "s1";
        Directory.CreateDirectory(paths.SessionDir(sessionId));

        var versions = versionId == TranscriptVersions.Root
            ? Array.Empty<TranscriptVersion>()
            : new[] { new TranscriptVersion { Id = versionId, Model = "tiny.en", Backend = "CPU", Language = "en" } };

        await new SessionStore(paths.SessionJson(sessionId)).SaveAsync(new SessionRecord
        {
            Id = sessionId, App = AppKind.Webex,
            StartedAtUtc = StartedAtLocal.ToUniversalTime(),
            EndedAtUtc = StartedAtLocal.ToUniversalTime().AddMinutes(30),   // finalized
            DurationMs = durationMs,
            Versions = versions,
        }, default);
        await new MetadataStore(paths.MetaJson(sessionId)).SaveAsync(new SessionMeta(), default);
        await new TranscriptStore(paths.TranscriptJsonl(sessionId)).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "Root words.", "Them"), default);

        if (versionId != TranscriptVersions.Root)
        {
            Directory.CreateDirectory(paths.VersionDir(sessionId, versionId));
            await new TranscriptStore(paths.TranscriptJsonl(sessionId, versionId)).AppendAsync(
                TranscriptLine.Segment(0, TranscriptSource.Remote, 0, 1000, "Version words.", "Them"), default);
            if (speakers is not null)
                await new SpeakersStore(paths.SpeakersJson(sessionId, versionId)).SaveAsync(speakers, default);
        }
        else if (speakers is not null)
        {
            await new SpeakersStore(paths.SpeakersJson(sessionId)).SaveAsync(speakers, default);
        }

        var meta = new SessionMeta { Participants = participants ?? [] };
        var maintenance = new MaintenanceService(paths, new FakeSettingsService(),
            new FakeRecycleBin(), TimeProvider.System);
        var vm = new InsertLineViewModel(maintenance, _reporter, sessionId, seedStartMs, meta,
            timestampsMode, StartedAtLocal, versionId, durationMs);
        return (vm, paths, sessionId);
    }

    [Fact]
    public async Task The_dialog_seeds_the_clicked_rows_timestamp()
    {
        var (vm, _, _) = await NewVmAsync(seedStartMs: 3_725_000, timestampsMode: "relative");
        Assert.Equal(3_725_000, vm.StartMs);
        Assert.Equal(TimestampFormat.Stamp(3_725_000, "relative", StartedAtLocal), vm.TimestampText);
    }

    [Fact]
    public async Task The_speaker_list_offers_named_participants_and_Unattributed_but_no_clusters()
    {
        var (vm, _, _) = await NewVmAsync(participants: [Named("p-1", "Jane Okafor", SourceKind.Remote)],
            speakers: SpeakersWithNamedCluster("Remote:1", "Detected voice 1"));

        Assert.Contains(vm.SpeakerChoices, c => c.Display == "Jane Okafor" && c.ParticipantId == "p-1");
        Assert.Contains(vm.SpeakerChoices, c => c.Display == InsertedLineLabels.Unattributed);
        Assert.DoesNotContain(vm.SpeakerChoices, c => c.ClusterKey is not null);
    }

    [Fact]
    public async Task The_default_selection_is_Unattributed()
    {
        var (vm, _, _) = await NewVmAsync(participants: [Named("p-1", "Jane Okafor", SourceKind.Remote)]);
        Assert.Null(vm.Speaker!.ParticipantId);
        Assert.Equal(InsertedLineLabels.Unattributed, vm.Speaker.Display);
    }

    [Fact]
    public async Task Save_rejects_blank_text_without_writing()
    {
        var (vm, paths, sessionId) = await NewVmAsync();
        vm.Text = "   ";
        Assert.False(await vm.SaveAsync(default));
        Assert.NotEmpty(vm.ValidationMessage);
        Assert.False(File.Exists(paths.EditsJson(sessionId)));
    }

    [Fact]
    public async Task Save_rejects_an_unparseable_timestamp_without_writing()
    {
        var (vm, paths, sessionId) = await NewVmAsync();
        vm.Text = "Off-mic answer.";
        vm.TimestampText = "not a time";
        Assert.False(await vm.SaveAsync(default));
        Assert.NotEmpty(vm.ValidationMessage);
        Assert.False(File.Exists(paths.EditsJson(sessionId)));
    }

    [Fact]
    public async Task Save_rejects_a_timestamp_past_the_session_duration_without_writing()
    {
        var (vm, paths, sessionId) = await NewVmAsync(durationMs: 600_000, timestampsMode: "relative");
        vm.Text = "Off-mic answer.";
        vm.TimestampText = "00:11:00";
        Assert.False(await vm.SaveAsync(default));
        Assert.NotEmpty(vm.ValidationMessage);
        Assert.False(File.Exists(paths.EditsJson(sessionId)));
    }

    [Fact]
    public async Task Save_writes_one_insertion_with_the_parsed_timestamp_and_participant()
    {
        var (vm, paths, sessionId) = await NewVmAsync(
            participants: [Named("p-1", "Jane Okafor", SourceKind.Remote)],
            durationMs: 600_000, timestampsMode: "relative");
        vm.Text = "  Off-mic answer.  ";
        vm.TimestampText = "00:01:01";
        vm.Speaker = vm.SpeakerChoices.Single(c => c.ParticipantId == "p-1");

        Assert.True(await vm.SaveAsync(default));

        var edits = await new EditStore(paths.SessionDir(sessionId), TimeProvider.System).LoadAsync(default);
        var line = Assert.Single(edits!.Insertions);
        Assert.Equal("i000001", line.Key);           // mint: the session's first-ever insertion
        Assert.Equal("Off-mic answer.", line.Value.Text);
        Assert.Equal(61_000, line.Value.StartMs);
        Assert.Equal("p-1", line.Value.SpeakerParticipantId);
    }

    [Fact]
    public async Task Save_targets_the_version_the_dialog_was_opened_against()
    {
        var (vm, paths, sessionId) = await NewVmAsync(versionId: "v2", durationMs: 600_000);
        vm.Text = "Off-mic answer.";
        Assert.True(await vm.SaveAsync(default));

        Assert.True(File.Exists(paths.EditsJson(sessionId, "v2")));
        Assert.False(File.Exists(paths.EditsJson(sessionId)));   // never landed in the root version
    }

    [Fact]
    public async Task A_timestamp_at_exactly_the_session_duration_is_accepted()
    {
        var (vm, _, _) = await NewVmAsync(durationMs: 600_000, timestampsMode: "relative");
        vm.Text = "Final word.";
        vm.TimestampText = "00:10:00";
        Assert.True(await vm.SaveAsync(default));
    }
}
