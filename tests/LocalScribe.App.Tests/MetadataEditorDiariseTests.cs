using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Stage 5.3 Task 7: Split speakers relocates from the Sessions-list context menu into
/// the Session Details window. Mirrors MetadataEditorLoadAsyncTests' harness (same fixture shape)
/// - a fresh MetadataEditorViewModel per test, id-first LoadAsync entry point.</summary>
public sealed class MetadataEditorDiariseTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-metaed-diarise-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly FakeUiErrorReporter _reporter = new();
    private readonly MaintenanceService _maintenance;
    private readonly SessionViewModel _session;

    public MetadataEditorDiariseTests()
    {
        _paths = new StoragePaths(_root);
        _maintenance = new MaintenanceService(_paths, new FakeSettingsService(), new FakeRecycleBin(), _time);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        _session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private MetadataEditorViewModel MakeEditor()
        => new(_maintenance, _session, _reporter, dispatch: a => a(), _time);

    /// <summary>A finalized on-disk session fixture (mirrors MetadataEditorLoadAsyncTests).</summary>
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

    /// <summary>A pending/in-progress session fixture: EndedAtUtc null (design 3.1) -
    /// SessionRowViewModel.IsPendingRecovery flips true from this alone.</summary>
    private async Task WritePendingSessionAsync(string id, string title)
    {
        Directory.CreateDirectory(_paths.SessionDir(id));
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero),
            EndedAtUtc = null,
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 0,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = title, MatterIds = [] }, CancellationToken.None);
    }

    [Fact]
    public async Task DiariseCommand_raises_request_with_session_id_when_finalized()
    {
        const string id = "2026-07-05_0100_Webex_hearing";
        await WriteFinalizedSessionAsync(id, "Hearing");
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);
        string? asked = null;
        editor.DiariseRequested += sid => asked = sid;

        Assert.True(editor.DiariseCommand.CanExecute(null));
        editor.DiariseCommand.Execute(null);

        Assert.Equal(id, asked);
    }

    [Fact]
    public async Task DiariseCommand_CanExecute_is_false_for_a_pending_session()
    {
        const string id = "2026-07-05_0200_Webex_inprogress";
        await WritePendingSessionAsync(id, "In progress");
        var editor = MakeEditor();
        await editor.LoadAsync(id, CancellationToken.None);
        string? asked = null;
        editor.DiariseRequested += sid => asked = sid;

        Assert.False(editor.DiariseCommand.CanExecute(null));
        editor.DiariseCommand.Execute(null);       // belt-and-braces early return: no-op even if invoked

        Assert.Null(asked);
    }

    [Fact]
    public void DiariseCommand_CanExecute_is_false_when_no_row_is_attached()
    {
        var editor = MakeEditor();

        Assert.False(editor.DiariseCommand.CanExecute(null));
    }
}
