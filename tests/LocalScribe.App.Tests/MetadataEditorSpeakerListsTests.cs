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

/// <summary>Stage 5.2 Task 6: LocalParticipants/RemoteParticipants are filtered views of
/// Participants split by Side, kept in sync as Participants changes; AddLocalNameCommand/
/// AddRemoteNameCommand add a free-text person to the correct side by reusing the existing
/// AddFreeText(name, side) - same id-mint/auto-save/error-handling.
/// Harness mirrors MetadataEditorLoadAsyncTests (id-first LoadAsync entry point) - there is no
/// TempStorage helper in this codebase (verified: not present anywhere under tests/), so this
/// file builds its own root/StoragePaths/MaintenanceService/SessionViewModel exactly as that
/// sibling file does.</summary>
public sealed class MetadataEditorSpeakerListsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-metaed-speakers-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly FakeUiErrorReporter _reporter = new();
    private readonly MaintenanceService _maintenance;
    private readonly SessionViewModel _session;

    public MetadataEditorSpeakerListsTests()
    {
        _paths = new StoragePaths(_root);
        _maintenance = new MaintenanceService(_paths, new FakeSettingsService(), new FakeRecycleBin(), _time);
        // A REAL controller over the 3a fakes, same wiring as MetadataEditorViewModelTests /
        // MetadataEditorLoadAsyncTests - RecomputeEditable's live-gate check needs a genuine
        // (idle) CurrentSessionId/State so a freshly-loaded finalized session is IsEditable.
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        _session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private MetadataEditorViewModel MakeEditor()
        => new(_maintenance, _session, _reporter, dispatch: a => a(), _time, confirm: _ => true);

    /// <summary>Writes a finalized session (valid v3 session.json, mirrors
    /// MetadataEditorLoadAsyncTests.WriteFinalizedSessionAsync) plus a meta.json whose
    /// Participants carry the given names on each Side, in the given order. localCount/remoteCount
    /// seed the meta's DECLARED speaker counts (Task 7): default 1/1, but a test may set them to
    /// MATCH the list sizes (derivation is skipped on load) or deliberately EXCEED them (the
    /// system-mix / declared-count evidentiary guard). Returns the minted session id.</summary>
    private async Task<string> SeedSessionWithParticipants(string[] local, string[] remote,
        int localCount = 1, int remoteCount = 1)
    {
        string id = "2026-07-05_0100_Webex_" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, CancellationToken.None);

        var participants = local
            .Select((n, i) => new SessionParticipant { Id = $"p-local-{i}", Name = n, Side = SourceKind.Local })
            .Concat(remote.Select((n, i) =>
                new SessionParticipant { Id = $"p-remote-{i}", Name = n, Side = SourceKind.Remote }))
            .ToArray();
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta
            {
                Title = "S", MatterIds = [], Participants = participants,
                LocalCount = localCount, RemoteCount = remoteCount,
            },
            CancellationToken.None);
        return id;
    }

    /// <summary>Writes a matter with the given roster member(s) and a finalized session TAGGED to
    /// it, so that after LoadAsync the editor's RosterPicks populates (via the fire-and-forget
    /// RefreshMatterDataAsync started in Attach - tests must SpinWait on RosterPicks.Count).
    /// Returns the minted session id. Accepts one or more roster names (Stage 5.4 C1's
    /// independent-per-side-picker test needs two distinct roster members).</summary>
    private async Task<string> SeedSessionTaggedToMatterWithRoster(params string[] rosterNames)
    {
        const string matterId = "M-2026-777";
        await _maintenance.SaveMatterAsync(new Matter
        {
            Id = matterId, Name = "Estate",
            Roster = rosterNames.Select(n =>
                new RosterMember { Id = "p-" + n.ToLowerInvariant(), Name = n, Role = "Witness" }).ToArray(),
            DateCreatedUtc = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero),
        }, CancellationToken.None);

        string id = "2026-07-05_0200_Webex_" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = "S", MatterIds = [matterId] }, CancellationToken.None);
        return id;
    }

    [Fact]
    public async Task Participants_split_into_local_and_remote_by_side()
    {
        string id = await SeedSessionWithParticipants(
            local: new[] { "Samuel" }, remote: new[] { "Colleague", "Barrister" });
        var editor = MakeEditor();

        await editor.LoadAsync(id, CancellationToken.None);

        Assert.Equal(new[] { "Samuel" }, editor.LocalParticipants.Select(p => p.Name));
        Assert.Equal(new[] { "Colleague", "Barrister" }, editor.RemoteParticipants.Select(p => p.Name));
    }

    [Fact]
    public async Task AddLocalNameCommand_adds_a_local_participant()
    {
        string id = await SeedSessionWithParticipants(local: new string[0], remote: new string[0]);
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);

        editor.NewLocalName = "Samuel";
        editor.AddLocalNameCommand.Execute(null);

        Assert.Contains(editor.LocalParticipants, p => p.Name == "Samuel" && p.Side == SourceKind.Local);
    }

    // ---- Task 7: per-side ROSTER add (fix the "everything is remote" bug) --------------------

    [Fact]
    public async Task AddRemoteFromRoster_adds_on_the_remote_side_not_hardcoded()
    {
        string id = await SeedSessionTaggedToMatterWithRoster("Barrister");
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);
        // RosterPicks populates via the fire-and-forget RefreshMatterDataAsync started in Attach.
        Assert.True(SpinWait.SpinUntil(() => editor.RosterPicks.Count > 0, TimeSpan.FromSeconds(10)));
        editor.RemoteSelectedRosterPick = editor.RosterPicks.First(r => r.Display.Contains("Barrister"));

        await editor.AddRemoteFromRosterCommand.ExecuteAsync(null);

        Assert.Contains(editor.RemoteParticipants, p => p.Name == "Barrister");
        Assert.DoesNotContain(editor.LocalParticipants, p => p.Name == "Barrister");
    }

    [Fact]
    public async Task AddLocalFromRoster_adds_on_the_local_side_proving_side_is_parameterized()
    {
        string id = await SeedSessionTaggedToMatterWithRoster("Paralegal");
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => editor.RosterPicks.Count > 0, TimeSpan.FromSeconds(10)));
        editor.LocalSelectedRosterPick = editor.RosterPicks.First(r => r.Display.Contains("Paralegal"));

        await editor.AddLocalFromRosterCommand.ExecuteAsync(null);

        Assert.Contains(editor.LocalParticipants, p => p.Name == "Paralegal");
        Assert.DoesNotContain(editor.RemoteParticipants, p => p.Name == "Paralegal");
    }

    // ---- Stage 5.4 5.2 (C1): independent per-side roster pickers -----------------------------

    [Fact]
    public async Task Per_side_roster_selections_are_independent()
    {
        string id = await SeedSessionTaggedToMatterWithRoster("Barrister", "Paralegal");
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => editor.RosterPicks.Count >= 2, TimeSpan.FromSeconds(10)));

        editor.LocalSelectedRosterPick = editor.RosterPicks.First(r => r.Display.Contains("Paralegal"));
        editor.RemoteSelectedRosterPick = editor.RosterPicks.First(r => r.Display.Contains("Barrister"));
        Assert.NotEqual(editor.LocalSelectedRosterPick, editor.RemoteSelectedRosterPick);

        await editor.AddLocalFromRosterCommand.ExecuteAsync(null);
        await editor.AddRemoteFromRosterCommand.ExecuteAsync(null);

        Assert.Contains(editor.LocalParticipants, p => p.Name == "Paralegal");
        Assert.DoesNotContain(editor.LocalParticipants, p => p.Name == "Barrister");
        Assert.Contains(editor.RemoteParticipants, p => p.Name == "Barrister");
        Assert.DoesNotContain(editor.RemoteParticipants, p => p.Name == "Paralegal");
    }

    // ---- Task 7: counts derive from the lists (with manual override) -------------------------

    [Fact]
    public async Task Counts_follow_the_lists_by_default()
    {
        // Seed counts to MATCH list sizes: derivation is intentionally SKIPPED during load (Attach
        // runs RebuildSideLists under _loading==true), so the post-load assertion reads the
        // persisted counts; the edit below is what proves derivation-on-edit.
        string id = await SeedSessionWithParticipants(local: new[] { "Samuel" },
            remote: new[] { "A", "B", "C" }, localCount: 1, remoteCount: 3);
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);

        Assert.True(editor.CountsFollowLists);
        Assert.Equal(1, editor.LocalCount);
        Assert.Equal(3, editor.RemoteCount);

        editor.NewRemoteName = "D"; editor.AddRemoteNameCommand.Execute(null);
        Assert.Equal(4, editor.RemoteCount);                // derivation-on-edit
    }

    [Fact]
    public async Task Manual_override_stops_counts_following_the_lists()
    {
        string id = await SeedSessionWithParticipants(local: new[] { "Samuel" },
            remote: new[] { "A", "B" }, localCount: 1, remoteCount: 2);
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);

        editor.CountsFollowLists = false;                   // system-mix override: declared != real
        editor.RemoteCount = 9;                             // a deliberate manual value
        editor.NewRemoteName = "C"; editor.AddRemoteNameCommand.Execute(null);

        Assert.False(editor.CountsFollowLists);
        Assert.True(editor.CountsAreManual);
        Assert.Equal(9, editor.RemoteCount);                // adding a participant did NOT re-derive
        Assert.Equal(3, editor.RemoteParticipants.Count);   // the list still tracks
    }

    [Fact]
    public async Task Single_local_speaker_keeps_LocalCount_1_for_NameResolver_tier2()
    {
        string id = await SeedSessionWithParticipants(local: new[] { "Samuel" },
            remote: new[] { "A" }, localCount: 1, remoteCount: 1);
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);

        Assert.Equal(1, editor.LocalCount);                 // tier-2 precondition after load

        // Editing the REMOTE side re-derives RemoteCount but must not disturb the single-local
        // count==1 that NameResolver tier-2 name resolution depends on.
        editor.NewRemoteName = "B"; editor.AddRemoteNameCommand.Execute(null);
        Assert.Equal(2, editor.RemoteCount);
        Assert.Equal(1, editor.LocalCount);
    }

    [Fact]
    public async Task Load_preserves_declared_count_exceeding_named_participants()
    {
        // Evidentiary system-mix guard (the case Counts_follow_the_lists_by_default cannot express):
        // a session can DECLARE more remote speakers (for diarisation / ForcedClusterCount) than
        // have been NAMED. Loading it must NEVER silently re-derive the declared count down to the
        // named-participant count - that would corrupt the persisted evidentiary count on mere
        // window-open. RemoteCount=3 declared, ONE named remote participant.
        string id = await SeedSessionWithParticipants(local: new[] { "Samuel" },
            remote: new[] { "OnlyNamed" }, localCount: 1, remoteCount: 3);
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);

        Assert.Equal(3, editor.RemoteCount);                // PRESERVED, not overwritten to 1
        Assert.Equal(1, editor.LocalCount);
        Assert.Single(editor.RemoteParticipants);           // list reflects the single named remote
        // Fix wave 1: the mismatched (declared 3 > named 1) leg opens with follow OFF, so the
        // declared count is protected from silent re-derivation on a later edit (see below).
        Assert.False(editor.CountsFollowLists);
    }

    [Fact]
    public async Task System_mix_declared_count_survives_reopen_and_edit()
    {
        // Fix wave 1 payoff: a declared system-mix count is protected NOT ONLY on load but across a
        // subsequent speaker edit, because auto-set opened the session with follow OFF. RemoteCount=3
        // declared, ONE named remote -> follow OFF -> adding another remote speaker does NOT lower
        // the declared 3.
        string id = await SeedSessionWithParticipants(local: new[] { "Samuel" },
            remote: new[] { "OnlyNamed" }, localCount: 1, remoteCount: 3);
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);

        Assert.False(editor.CountsFollowLists);             // auto-set OFF on the mismatched load
        Assert.True(editor.CountsAreManual);

        editor.NewRemoteName = "Second"; editor.AddRemoteNameCommand.Execute(null);

        Assert.Equal(2, editor.RemoteParticipants.Count);   // the list still tracks the edit
        Assert.Equal(3, editor.RemoteCount);                // ...but the DECLARED count stays 3
    }

    [Fact]
    public async Task Emptying_a_side_under_follow_on_preserves_its_count()
    {
        // Fix wave 1, invariant 3 (the Count>0 guard) under follow-ON: a session whose counts match
        // the list sizes opens follow-ON; emptying a side by removing its only participant must NOT
        // zero that side's count - the empty side keeps its current value.
        string id = await SeedSessionWithParticipants(local: new[] { "X" },
            remote: new[] { "Y" }, localCount: 1, remoteCount: 1);
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);

        Assert.True(editor.CountsFollowLists);              // matching counts -> follow ON

        var onlyRemote = Assert.Single(editor.RemoteParticipants);
        editor.RemoveParticipantCommand.Execute(onlyRemote);

        Assert.Empty(editor.RemoteParticipants);            // side emptied
        Assert.Equal(1, editor.RemoteCount);               // count PRESERVED (Count>0 guard), not 0
    }

    // ---- Stage 5.4 5.2 (C1): explicit unnamed speaker slots ----------------------------------

    [Fact]
    public async Task AddRemoteUnnamed_appends_an_unnamed_slot_rendered_speaker_n()
    {
        string id = await SeedSessionWithParticipants(local: new[] { "Samuel" },
            remote: new[] { "Colleague" }, localCount: 1, remoteCount: 1);
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);

        editor.AddRemoteUnnamedCommand.Execute(null);
        editor.AddRemoteUnnamedCommand.Execute(null);

        Assert.Equal(new[] { "Colleague", "Speaker 1", "Speaker 2" },
            editor.RemoteParticipants.Select(p => p.DisplayLabel));
        var unnamed = editor.RemoteParticipants.Where(p => p.IsUnnamed).ToArray();
        Assert.All(unnamed, p => Assert.Equal(ParticipantKind.Unnamed, p.Kind));
        Assert.All(unnamed, p => Assert.Equal("", p.Name));
        Assert.All(unnamed, p => Assert.Equal(SourceKind.Remote, p.Side));
        Assert.Equal(2, unnamed.Select(p => p.Id).Distinct().Count());  // distinct session-scoped ids
    }

    [Fact]
    public async Task Unnamed_slots_number_independently_per_side_and_named_show_their_name()
    {
        string id = await SeedSessionWithParticipants(local: new[] { "Samuel" },
            remote: new[] { "Bob" }, localCount: 1, remoteCount: 1);
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);

        editor.AddLocalUnnamedCommand.Execute(null);
        editor.AddRemoteUnnamedCommand.Execute(null);
        editor.AddRemoteUnnamedCommand.Execute(null);

        Assert.Equal(new[] { "Samuel", "Speaker 1" },
            editor.LocalParticipants.Select(p => p.DisplayLabel));
        Assert.Equal(new[] { "Bob", "Speaker 1", "Speaker 2" },
            editor.RemoteParticipants.Select(p => p.DisplayLabel));
    }

    [Fact]
    public async Task Adding_an_unnamed_slot_buffers_marks_dirty_and_saves_kind_losslessly()
    {
        string id = await SeedSessionWithParticipants(local: new[] { "Samuel" },
            remote: new[] { "Colleague" }, localCount: 1, remoteCount: 1);
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);
        Assert.False(editor.IsDirty);

        editor.AddRemoteUnnamedCommand.Execute(null);

        Assert.True(editor.IsDirty);                        // buffered under Group A's Save model
        var beforeSave = await new MetadataStore(_paths.MetaJson(id)).LoadAsync(CancellationToken.None);
        Assert.Equal(2, beforeSave!.Participants.Count);    // disk untouched until explicit Save

        await editor.SaveCommand.ExecuteAsync(null);

        var after = await new MetadataStore(_paths.MetaJson(id)).LoadAsync(CancellationToken.None);
        var slot = Assert.Single(after!.Participants, p => p.Kind == ParticipantKind.Unnamed);
        Assert.Equal(SourceKind.Remote, slot.Side);
        Assert.Equal("", slot.Name);
    }
}
