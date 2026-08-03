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

    [Fact]
    public async Task Markdown_export_sanitized_md_filename_filter_and_written_file()
    {
        // Design 2026-07-18 section 3: same Save-As shape as docx (sanitized title default name),
        // .md filter, and the file lands via MaintenanceService.ExportMarkdownAsync.
        var (svc, _, rep) = await MakeAsync();
        SavePathRequest? seen = null;
        string dest = Path.Combine(_root, "out.md");
        var vm = new ExportDialogViewModel("s1", "Doe: intake/2026", svc,
            req => { seen = req; return dest; }, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("Doe_ intake_2026.md", seen!.DefaultFileName);        // ':' and '/' -> '_'
        Assert.Equal("Markdown (*.md)|*.md", seen.Filter);
        Assert.True(File.Exists(dest));
        Assert.StartsWith("# Doe intake\n", await File.ReadAllTextAsync(dest));   // meta title, not the raw arg
        Assert.Single(rep.Infos);
        Assert.Empty(rep.Errors);
    }

    [Fact]
    public async Task Option_toggles_show_for_docx_and_markdown_not_zip()
    {
        // The dialog's two checkboxes apply to BOTH textual formats (design 2026-07-18 section 3);
        // ShowOptionToggles generalizes the old IsDocx gate without removing it.
        var (svc, _, rep) = await MakeAsync();
        var vm = new ExportDialogViewModel("s1", "T", svc, _ => null, _ => { }, rep, a => a());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(vm.ShowOptionToggles);                                // Zip default: hidden
        vm.Format = ExportFormat.Docx;
        Assert.True(vm.ShowOptionToggles);
        Assert.True(vm.IsDocx);
        vm.Format = ExportFormat.Markdown;
        Assert.True(vm.ShowOptionToggles);
        Assert.False(vm.IsDocx);                                           // IsDocx stays format-accurate
        vm.Format = ExportFormat.Zip;
        Assert.False(vm.ShowOptionToggles);
        Assert.Contains(nameof(ExportDialogViewModel.ShowOptionToggles), raised);
        Assert.Contains(nameof(ExportDialogViewModel.IsDocx), raised);
    }

    /// <summary>One same-speaker Local run with 400ms gaps (SectionGrouper merges it into a
    /// single row); the fifth segment starts 19.4s after the first, past the fixed 15s cadence.</summary>
    private static async Task SeedLongTurnAsync(StoragePaths paths)
    {
        long[][] times = [[0, 4000], [4400, 9000], [9400, 14000], [14400, 19000], [19400, 24000]];
        string[] words = ["one", "two", "three", "four", "five"];
        var store = new TranscriptStore(paths.TranscriptJsonl("s1"));
        for (int i = 0; i < words.Length; i++)
            await store.AppendAsync(TranscriptLine.Segment(i, TranscriptSource.Local,
                times[i][0], times[i][1], words[i], "Me"), default);
    }

    [Fact]
    public async Task Extra_timestamps_default_off_and_produce_no_continuation_stamps()
    {
        var (svc, paths, rep) = await MakeAsync();
        await SeedLongTurnAsync(paths);
        string dest = Path.Combine(_root, "plain.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        Assert.False(vm.ExtraTimestamps);                                  // off by default
        await vm.ExportCommand.ExecuteAsync(null);

        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains(":** one two three four five\n", md);              // one unbroken paragraph
        Assert.DoesNotContain("**[00:19]**", md);
        Assert.Empty(rep.Errors);
    }

    [Fact]
    public async Task Extra_timestamps_add_continuation_paragraphs_to_the_export()
    {
        var (svc, paths, rep) = await MakeAsync();
        await SeedLongTurnAsync(paths);
        string dest = Path.Combine(_root, "cadence.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, ExtraTimestamps = true };

        await vm.ExportCommand.ExecuteAsync(null);

        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains(":** one two three four\n", md);                   // chunk 0 keeps the label
        // (cont'd) repeats the name (design 2026-08-03 section 8) - no longer a bare stamp.
        Assert.Contains("\n**[00:19] Me (cont'd):** five\n", md);
        Assert.Empty(rep.Errors);
    }

    [Fact]
    public async Task Unchecking_include_timestamps_forces_the_cadence_off()
    {
        var (svc, paths, rep) = await MakeAsync();
        await SeedLongTurnAsync(paths);
        string dest = Path.Combine(_root, "nostamps.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, ExtraTimestamps = true, IncludeTimestamps = false };

        await vm.ExportCommand.ExecuteAsync(null);

        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains("one two three four five\n", md);                  // still one paragraph
        Assert.DoesNotContain("[00:", md);                                 // no stamps anywhere
        Assert.Empty(rep.Errors);
    }
}
