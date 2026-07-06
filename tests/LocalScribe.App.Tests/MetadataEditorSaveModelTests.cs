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

/// <summary>Stage 5.4 5.1: the Session Details editor BUFFERS every edit (fields, participants,
/// matter tags) and writes meta.json only on the explicit SaveCommand; DiscardCommand reverts
/// to the last-saved baseline. Harness mirrors MetadataEditorSpeakerListsTests (own root/
/// StoragePaths/MaintenanceService/SessionViewModel over the 3a fakes, id-first LoadAsync).</summary>
public sealed class MetadataEditorSaveModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-metaed-save-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly FakeUiErrorReporter _reporter = new();
    private readonly MaintenanceService _maintenance;
    private readonly SessionViewModel _session;

    public MetadataEditorSaveModelTests()
    {
        _paths = new StoragePaths(_root);
        _maintenance = new MaintenanceService(_paths, new FakeSettingsService(), new FakeRecycleBin(), _time);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        _session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private MetadataEditorViewModel MakeEditor()
        => new(_maintenance, _session, _reporter, dispatch: a => a(), _time, confirm: _ => true);

    private async Task WriteFinalizedSessionAsync(string id, string title)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = title, MatterIds = [] }, CancellationToken.None);
    }

    private async Task WriteMatterAsync(string id, string name)
        => await _maintenance.SaveMatterAsync(new Matter
        {
            Id = id, Name = name,
            DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

    // Gated read, same rationale as MetadataEditorViewModelTests.SessionCount.
    private int SessionCount(string matterId)
        => _maintenance.ListMattersAsync(CancellationToken.None)
           .GetAwaiter().GetResult().Matters.Single(m => m.Id == matterId).SessionCount;

    private async Task<SessionMeta?> MetaOnDisk(string id)
        => await new MetadataStore(_paths.MetaJson(id)).LoadAsync(CancellationToken.None);

    [Fact]
    public async Task Field_edit_marks_dirty_and_writes_nothing_to_disk()
    {
        const string id = "2026-07-06_0100_Webex_dirty";
        await WriteFinalizedSessionAsync(id, "Before");
        var ed = MakeEditor();
        await ed.LoadAsync(id, CancellationToken.None);
        Assert.False(ed.IsDirty);
        Assert.False(ed.SaveCommand.CanExecute(null));
        Assert.False(ed.DiscardCommand.CanExecute(null));

        ed.Title = "After";

        Assert.True(ed.IsDirty);
        Assert.True(ed.SaveCommand.CanExecute(null));
        Assert.True(ed.DiscardCommand.CanExecute(null));
        Assert.Equal("Before", (await MetaOnDisk(id))!.Title);      // NOTHING hit disk
    }

    [Fact]
    public async Task Participant_and_tag_edits_mark_dirty_without_disk_writes()
    {
        const string id = "2026-07-06_0200_Webex_paths";
        await WriteFinalizedSessionAsync(id, "S");
        await WriteMatterAsync("M-2026-001", "Estate");
        var ed = MakeEditor();
        await ed.LoadAsync(id, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => ed.MatterOptions.Count == 1, TimeSpan.FromSeconds(10)));

        ed.AddFreeText("Bob Witness", SourceKind.Remote);           // participant add path
        ed.ToggleMatterCommand.Execute(ed.MatterOptions.Single());  // tag-toggle path

        Assert.True(ed.IsDirty);
        var onDisk = (await MetaOnDisk(id))!;
        Assert.Empty(onDisk.Participants);                          // buffered, not written
        Assert.Empty(onDisk.MatterIds);
        Assert.Equal(0, SessionCount("M-2026-001"));                // no tag delta before Save

        // Default LocalCount/RemoteCount=1 with no named participants synthesizes one Unnamed
        // slot per side on load (Stage 5.4 C1 lazy migration), so target the free-text add by
        // name rather than assuming it is the only row.
        ed.Remove(ed.Participants.Single(p => p.Name == "Bob Witness")); // participant remove path
        Assert.True(ed.IsDirty);
    }

    [Fact]
    public async Task Save_commits_once_clears_dirty_and_raises_Saved()
    {
        const string id = "2026-07-06_0300_Webex_commit";
        await WriteFinalizedSessionAsync(id, "Before");
        var ed = MakeEditor();
        await ed.LoadAsync(id, CancellationToken.None);
        var saved = new List<string>();
        ed.Saved += sid => saved.Add(sid);

        ed.Title = "After";
        ed.Description = "notes";
        await ed.SaveCommand.ExecuteAsync(null);

        var onDisk = (await MetaOnDisk(id))!;
        Assert.Equal("After", onDisk.Title);
        Assert.Equal("notes", onDisk.Description);
        Assert.False(ed.IsDirty);
        Assert.False(ed.SaveCommand.CanExecute(null));              // greyed when clean
        Assert.Equal(new[] { id }, saved.ToArray());                // exactly once, with the id
        Assert.Empty(_reporter.Reports);
    }

    [Fact]
    public async Task Save_applies_the_buffered_matter_tag_delta_at_commit_time()
    {
        const string id = "2026-07-06_0400_Webex_tags";
        await WriteFinalizedSessionAsync(id, "S");
        await WriteMatterAsync("M-2026-001", "Estate");
        var ed = MakeEditor();
        await ed.LoadAsync(id, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => ed.MatterOptions.Count == 1, TimeSpan.FromSeconds(10)));

        ed.ToggleMatterCommand.Execute(ed.MatterOptions.Single());
        Assert.Equal(0, SessionCount("M-2026-001"));                // buffered
        await ed.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, SessionCount("M-2026-001"));                // +1 exactly once, at commit
        Assert.Equal(new[] { "M-2026-001" }, (await MetaOnDisk(id))!.MatterIds);

        ed.ToggleMatterCommand.Execute(ed.TaggedMatters.Single());
        Assert.Equal(1, SessionCount("M-2026-001"));                // untag buffered too
        await ed.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, SessionCount("M-2026-001"));
        Assert.Empty((await MetaOnDisk(id))!.MatterIds);
    }

    [Fact]
    public async Task Discard_restores_the_last_saved_baseline_and_clears_dirty()
    {
        const string id = "2026-07-06_0500_Webex_discard";
        await WriteFinalizedSessionAsync(id, "Original");
        await WriteMatterAsync("M-2026-001", "Estate");
        var ed = MakeEditor();
        await ed.LoadAsync(id, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => ed.MatterOptions.Count == 1, TimeSpan.FromSeconds(10)));
        // Default LocalCount/RemoteCount=1 with no named participants synthesizes one Unnamed
        // slot per side on load (Stage 5.4 C1 lazy migration) - capture that baseline so Discard
        // can be proven to regenerate exactly it, not an empty list.
        int baseline = ed.Participants.Count;

        ed.Title = "Mangled";
        ed.AddFreeText("Accident", SourceKind.Local);
        ed.ToggleMatterCommand.Execute(ed.MatterOptions.Single());
        Assert.True(ed.IsDirty);

        ed.DiscardCommand.Execute(null);

        Assert.Equal("Original", ed.Title);
        Assert.Equal(baseline, ed.Participants.Count);              // reverted, not persisted
        Assert.DoesNotContain(ed.Participants, p => p.Name == "Accident");
        Assert.Empty(ed.TaggedMatters);
        Assert.False(ed.IsDirty);
        Assert.False(ed.DiscardCommand.CanExecute(null));
        Assert.Equal("Original", (await MetaOnDisk(id))!.Title);    // disk was never touched
        Assert.Equal(0, SessionCount("M-2026-001"));
    }

    [Fact]
    public async Task Loading_another_session_resets_the_dirty_flag()
    {
        const string a = "2026-07-06_0600_Webex_first";
        const string b = "2026-07-06_0700_Webex_second";
        await WriteFinalizedSessionAsync(a, "A");
        await WriteFinalizedSessionAsync(b, "B");
        var ed = MakeEditor();
        await ed.LoadAsync(a, CancellationToken.None);
        ed.Title = "A-edited";
        Assert.True(ed.IsDirty);

        await ed.LoadAsync(b, CancellationToken.None);              // Attach resets

        Assert.False(ed.IsDirty);
        Assert.Equal("B", ed.Title);
    }
}
