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
/// AddFreeText(name, side) - same id-mint/auto-save/error-handling as AddFreeTextCommand.
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
        => new(_maintenance, _session, _reporter, dispatch: a => a(), _time);

    /// <summary>Writes a finalized session (valid v3 session.json, mirrors
    /// MetadataEditorLoadAsyncTests.WriteFinalizedSessionAsync) plus a meta.json whose
    /// Participants carry the given names on each Side, in the given order. Returns the
    /// minted session id.</summary>
    private async Task<string> SeedSessionWithParticipants(string[] local, string[] remote)
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
            new SessionMeta { Title = "S", MatterIds = [], Participants = participants },
            CancellationToken.None);
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
}
