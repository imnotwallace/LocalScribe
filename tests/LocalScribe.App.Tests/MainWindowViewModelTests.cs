using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Stage 5.4 section 6: the shell VM also exposes the ONE shared SessionViewModel so
/// MainWindow can host the nav-rail Record command and the bottom status strip. Harness builds
/// a real SessionViewModel over the 3a fakes (LiveTestDoubles) with a disposable temp root,
/// mirroring MetadataEditorSaveModelTests.</summary>
public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-mainvm-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private SessionViewModel MakeSession()
    {
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        return new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
    }

    [Fact]
    public void Defaults_to_sessions_and_raises_change_notifications()
    {
        var vm = new MainWindowViewModel(new InfoBarErrorReporter(a => a()), MakeSession());
        Assert.Equal("Sessions", vm.SelectedSection);      // design section 2: Sessions is default

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.SelectedSection = "Matters";
        Assert.Equal("Matters", vm.SelectedSection);
        Assert.Contains(nameof(MainWindowViewModel.SelectedSection), raised);
    }

    [Fact]
    public void Exposes_the_shared_error_queue()
    {
        var errors = new InfoBarErrorReporter(a => a());
        var vm = new MainWindowViewModel(errors, MakeSession());
        Assert.Same(errors, vm.Errors);
    }

    [Fact]
    public void Exposes_the_shared_session_view_model()
    {
        var session = MakeSession();
        var vm = new MainWindowViewModel(new InfoBarErrorReporter(a => a()), session);
        Assert.Same(session, vm.Session);
    }
}
