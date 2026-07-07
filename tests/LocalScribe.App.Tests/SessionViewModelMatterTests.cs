using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SessionViewModelMatterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-svm-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    [Fact]
    public async Task Start_seeds_meta_matterIds_from_the_provider()
    {
        var paths = new StoragePaths(_root);
        await new MatterStore(paths.MattersDir).SaveAsync(new Matter { Id = "M-2026-014", Name = "Doe" }, default);
        var seam = new MatterSelectionOverride { MatterIds = new[] { "M-2026-014" } };

        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var vm = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options(), matterIdsProvider: () => seam.MatterIds);

        await vm.StartCommand.ExecuteAsync(null);
        string? id = vm.CurrentSessionId;
        await vm.StopCommand.ExecuteAsync(null);

        Assert.NotNull(id);
        var meta = await new MetadataStore(paths.MetaJson(id!)).LoadAsync(default);
        Assert.Equal(new[] { "M-2026-014" }, meta!.MatterIds);
    }
}
