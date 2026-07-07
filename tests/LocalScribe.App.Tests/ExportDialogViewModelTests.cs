using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class ExportDialogViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-exp-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private async Task<(MaintenanceService Svc, StoragePaths Paths, CollectingReporter Rep)> MakeAsync()
    {
        var paths = new StoragePaths(_root);
        // NoopRecycleBin from MaintenanceServiceTests is private to that class; FakeRecycleBin
        // (AppServiceFakes.cs, same LocalScribe.App.Tests namespace) is the public equivalent.
        var svc = new MaintenanceService(paths, new FakeSettingsService(), new FakeRecycleBin(),
            new ManualUtcTimeProvider(new DateTimeOffset(2026, 7, 3, 6, 0, 0, TimeSpan.Zero)));
        Directory.CreateDirectory(paths.SessionDir("s1"));
        await new SessionStore(paths.SessionJson("s1")).SaveAsync(new SessionRecord
        {
            Id = "s1", App = AppKind.Webex,
            StartedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 0, 0, TimeSpan.Zero),
            EndedAtUtc = new DateTimeOffset(2026, 7, 3, 1, 30, 0, TimeSpan.Zero),
            TimeZoneId = "UTC", UtcOffsetMinutes = 0, DurationMs = 1_800_000,
        }, default);
        await new MetadataStore(paths.MetaJson("s1")).SaveAsync(new SessionMeta { Title = "Doe intake" }, default);
        return (svc, paths, new CollectingReporter());
    }

    private sealed class CollectingReporter : IUiErrorReporter
    {
        public readonly List<string> Infos = new();
        public readonly List<string> Errors = new();
        public void Report(string context, Exception ex) => Errors.Add(context + ": " + ex.Message);
        public void Info(string message) => Infos.Add(message);
    }

    [Fact]
    public async Task Zip_export_defaults_to_folder_id_writes_file_and_reveals()
    {
        var (svc, _, rep) = await MakeAsync();
        SavePathRequest? seen = null;
        string dest = Path.Combine(_root, "s1.zip");
        string? revealed = null;
        bool closed = false;
        var vm = new ExportDialogViewModel("s1", "Doe intake", svc,
            req => { seen = req; return dest; }, p => revealed = p, rep, a => a());
        vm.Closed += () => closed = true;

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("s1.zip", seen!.DefaultFileName);
        Assert.True(File.Exists(dest));
        Assert.Equal(dest, revealed);
        Assert.True(closed);
        Assert.Single(rep.Infos);
    }

    [Fact]
    public async Task Docx_export_default_filename_sanitizes_the_title()
    {
        var (svc, _, rep) = await MakeAsync();
        SavePathRequest? seen = null;
        var vm = new ExportDialogViewModel("s1", "Doe: intake/2026", svc,
            req => { seen = req; return Path.Combine(_root, "out.docx"); }, _ => { }, rep, a => a())
        { Format = ExportFormat.Docx };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("Doe_ intake_2026.docx", seen!.DefaultFileName);     // ':' and '/' -> '_'
        Assert.True(File.Exists(Path.Combine(_root, "out.docx")));
    }

    [Fact]
    public async Task Cancelling_save_as_is_a_no_op()
    {
        var (svc, _, rep) = await MakeAsync();
        bool revealed = false;
        var vm = new ExportDialogViewModel("s1", "T", svc, _ => null, _ => revealed = true, rep, a => a());
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.False(revealed);
        Assert.Empty(rep.Infos);
    }
}
