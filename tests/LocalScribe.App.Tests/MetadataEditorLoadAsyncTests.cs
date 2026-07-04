using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Stage 5.2 Task 2: the id-first entry point the Session Details window (Task 3) will
/// await from its Loaded handler. Covers the happy path, the "session no longer exists" detach,
/// and the "session.json is present but corrupt" resilience path - LoadSessionItemAsync THROWS on
/// a malformed record (Task 1's locked contract), so LoadAsync must catch, report, and detach
/// instead of crashing the dispatcher.</summary>
public sealed class MetadataEditorLoadAsyncTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-metaed-load-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    private readonly TimeProvider _time = TimeProvider.System;
    private readonly FakeUiErrorReporter _reporter = new();
    private readonly MaintenanceService _maintenance;
    private readonly SessionViewModel _session;

    public MetadataEditorLoadAsyncTests()
    {
        _paths = new StoragePaths(_root);
        _maintenance = new MaintenanceService(_paths, new FakeSettingsService(), new FakeRecycleBin(), _time);
        // A REAL controller over the 3a fakes, same wiring as MetadataEditorViewModelTests -
        // RecomputeEditable's live-gate check needs a genuine (idle) CurrentSessionId/State.
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        _session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private MetadataEditorViewModel MakeEditor()
        => new(_maintenance, _session, _reporter, dispatch: a => a(), _time);

    /// <summary>A finalized on-disk session fixture: valid v3 session.json + meta.json (mirrors
    /// MaintenanceServiceLoadItemTests.WriteFinalizedSessionAsync).</summary>
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

    [Fact]
    public async Task LoadAsync_populates_the_editor_from_disk_by_id()
    {
        const string id = "2026-07-03_0100_Webex_directions";
        await WriteFinalizedSessionAsync(id, "Directions");
        var editor = MakeEditor();

        await editor.LoadAsync(id, CancellationToken.None);

        Assert.Equal("Directions", editor.Title);
        Assert.True(editor.IsEditable);                     // a finalized session is editable
        Assert.Empty(_reporter.Reports);
    }

    [Fact]
    public async Task LoadAsync_with_unknown_id_detaches()
    {
        var editor = MakeEditor();

        await editor.LoadAsync("nope", CancellationToken.None);

        Assert.False(editor.IsEditable);                    // Attach(null) disables the pane
        Assert.Empty(_reporter.Reports);
    }

    [Fact]
    public async Task LoadAsync_with_a_corrupt_session_json_reports_and_detaches_instead_of_throwing()
    {
        const string id = "2026-07-03_0200_Webex_corrupt";
        Directory.CreateDirectory(_paths.SessionDir(id));
        await File.WriteAllTextAsync(_paths.SessionJson(id), "{ not valid json !!", CancellationToken.None);
        var editor = MakeEditor();

        await editor.LoadAsync(id, CancellationToken.None);  // must NOT throw

        Assert.False(editor.IsEditable);                    // pane disabled, not left stale/editable
        Assert.Single(_reporter.Reports);                   // user sees an error, not a crash
    }
}
