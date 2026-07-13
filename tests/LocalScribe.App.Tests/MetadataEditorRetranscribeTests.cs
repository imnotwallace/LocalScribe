using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class MetadataEditorRetranscribeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-editor-retrans-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;
    public MetadataEditorRetranscribeTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
        Directory.CreateDirectory(_paths.MattersDir);
    }
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static SessionRowViewModel Row(string id, bool ended)
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var rec = new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = t,
            EndedAtUtc = ended ? t.AddMinutes(1) : null,
            UtcOffsetMinutes = 480, TimeZoneId = "Singapore Standard Time",
            DurationMs = 60000, Model = "small.en", Backend = "CPU", Language = "en",
        };
        var meta = new SessionMeta { Title = "T", Medium = Medium.Webex };
        return new SessionRowViewModel(new SessionListItem(id, rec, meta), TimeProvider.System);
    }

    [Fact]
    public void Retranscribe_gates_on_an_attached_finalized_row_and_raises_with_the_id()
    {
        var maintenance = new MaintenanceService(_paths, new FakeSettingsService(),
            new FakeRecycleBin(), TimeProvider.System);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var vm = new MetadataEditorViewModel(maintenance, session, new FakeUiErrorReporter(),
            a => a(), TimeProvider.System, confirm: _ => true);
        var requested = new List<string>();
        vm.RetranscribeRequested += requested.Add;

        Assert.False(vm.RetranscribeCommand.CanExecute(null));          // no row attached

        vm.Attach(Row("s-pending", ended: false));
        Assert.False(vm.RetranscribeCommand.CanExecute(null));          // pending-recovery row

        vm.Attach(Row("s-done", ended: true));
        Assert.True(vm.RetranscribeCommand.CanExecute(null));
        vm.RetranscribeCommand.Execute(null);
        Assert.Equal(new[] { "s-done" }, requested.ToArray());

        vm.Dispose();
        session.Dispose();
    }
}
