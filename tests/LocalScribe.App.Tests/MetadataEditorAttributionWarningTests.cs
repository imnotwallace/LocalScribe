using System.IO;
using System.Linq;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Stage 5.4 5.1 attribution warning: Save asks the injected confirm seam ONLY when
/// the pending commit changes rendered speaker attribution vs the last-saved baseline -
/// NameResolver tier-2 (declared==1 labels the whole side with its first participant's name)
/// or a cluster-owning participant (ClusterKey set) removed/renamed. Declining keeps the edits
/// buffered and dirty; nothing touches disk. Harness mirrors MetadataEditorSaveModelTests.</summary>
public sealed class MetadataEditorAttributionWarningTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-metaed-warn-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly FakeUiErrorReporter _reporter = new();
    private readonly MaintenanceService _maintenance;
    private readonly SessionViewModel _session;

    public MetadataEditorAttributionWarningTests()
    {
        _paths = new StoragePaths(_root);
        _maintenance = new MaintenanceService(_paths, new FakeSettingsService(), new FakeRecycleBin(), _time);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        _session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private MetadataEditorViewModel MakeEditor(Func<string, bool> confirm)
        => new(_maintenance, _session, _reporter, dispatch: a => a(), _time, confirm);

    private async Task<string> SeedAsync(SessionParticipant[] participants,
        int localCount, int remoteCount)
    {
        string id = "2026-07-06_0100_Webex_" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(new SessionMeta
        {
            Title = "S", MatterIds = [], Participants = participants,
            LocalCount = localCount, RemoteCount = remoteCount,
        }, CancellationToken.None);
        return id;
    }

    private async Task<SessionMeta?> MetaOnDisk(string id)
        => await new MetadataStore(_paths.MetaJson(id)).LoadAsync(CancellationToken.None);

    [Fact]
    public async Task Save_that_does_not_change_attribution_never_asks()
    {
        string id = await SeedAsync(
            [new SessionParticipant { Id = "p-alice", Name = "Alice", Side = SourceKind.Remote }],
            localCount: 1, remoteCount: 1);
        int asks = 0;
        var ed = MakeEditor(_ => { asks++; return true; });
        await ed.LoadAsync(id, CancellationToken.None);

        ed.Title = "Only the title changed";
        await ed.SaveCommand.ExecuteAsync(null);

        Assert.Equal(0, asks);                              // no attribution delta -> no dialog
        Assert.Equal("Only the title changed", (await MetaOnDisk(id))!.Title);
        Assert.False(ed.IsDirty);
    }

    [Fact]
    public async Task Declined_warning_aborts_the_save_and_keeps_the_editor_dirty()
    {
        string id = await SeedAsync(
            [new SessionParticipant { Id = "p-alice", Name = "Alice", Side = SourceKind.Remote }],
            localCount: 1, remoteCount: 1);
        string? message = null;
        var ed = MakeEditor(m => { message = m; return false; });
        await ed.LoadAsync(id, CancellationToken.None);
        var saved = new List<string>();
        ed.Saved += sid => saved.Add(sid);

        // "Alice" labeled EVERY remote line (tier-2: declared==1); removing her changes rendering.
        ed.Remove(ed.RemoteParticipants.Single());
        await ed.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(message);
        Assert.Contains("Alice", message);
        Assert.True(ed.IsDirty);                            // edits kept, still pending
        Assert.Empty(saved);                                // no commit -> no grid refresh
        Assert.Single((await MetaOnDisk(id))!.Participants); // disk untouched
        Assert.Empty(_reporter.Reports);
    }

    [Fact]
    public async Task Accepted_warning_commits_the_attribution_change()
    {
        string id = await SeedAsync(
            [new SessionParticipant { Id = "p-alice", Name = "Alice", Side = SourceKind.Remote }],
            localCount: 1, remoteCount: 1);
        var ed = MakeEditor(_ => true);
        await ed.LoadAsync(id, CancellationToken.None);

        ed.Remove(ed.RemoteParticipants.Single());
        await ed.SaveCommand.ExecuteAsync(null);

        Assert.Empty((await MetaOnDisk(id))!.Participants);
        Assert.False(ed.IsDirty);
    }

    [Fact]
    public async Task Renaming_the_single_named_side_names_both_labels_in_the_message()
    {
        string id = await SeedAsync(
            [new SessionParticipant { Id = "p-alice", Name = "Alice", Side = SourceKind.Remote }],
            localCount: 1, remoteCount: 1);
        string? message = null;
        var ed = MakeEditor(m => { message = m; return true; });
        await ed.LoadAsync(id, CancellationToken.None);

        ed.Remove(ed.RemoteParticipants.Single());
        ed.AddFreeText("Alicia", SourceKind.Remote);        // net effect: the side's one name changed
        await ed.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(message);
        Assert.Contains("\"Alice\"", message);
        Assert.Contains("\"Alicia\"", message);
        Assert.Equal("Alicia", (await MetaOnDisk(id))!.Participants.Single().Name);
    }

    [Fact]
    public async Task Removing_a_cluster_owning_participant_warns_even_without_a_side_label_change()
    {
        // RemoteCount=2 on both sides of the commit: tier-2 renders nothing either way, so ONLY
        // the ClusterKey-ownership rule can (and must) fire. Local declared 1 with no named local
        // opens with CountsFollowLists OFF (5.2 fix wave), so the declared 2 survives the removal.
        string id = await SeedAsync(
            [
                new SessionParticipant { Id = "p-alice", Name = "Alice", Side = SourceKind.Remote, ClusterKey = "remote:1" },
                new SessionParticipant { Id = "p-bob", Name = "Bob", Side = SourceKind.Remote },
            ],
            localCount: 1, remoteCount: 2);
        string? message = null;
        var ed = MakeEditor(m => { message = m; return true; });
        await ed.LoadAsync(id, CancellationToken.None);

        ed.Remove(ed.RemoteParticipants.First(p => p.Name == "Alice"));
        await ed.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(message);
        Assert.Contains("Alice", message);
        Assert.Single((await MetaOnDisk(id))!.Participants);   // committed after accept
    }
}
