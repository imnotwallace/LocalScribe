using System.IO;
using System.Linq;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
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
        var vm = new ExportDialogViewModel("s1", "Doe intake", svc, new FakeSettingsService(),
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
        var vm = new ExportDialogViewModel("s1", "Doe: intake/2026", svc, new FakeSettingsService(),
            req => { seen = req; return Path.Combine(_root, "out.docx"); }, _ => { }, rep, a => a())
        { Format = ExportFormat.Docx };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("Doe intake.docx", seen!.DefaultFileName);           // meta title, not the raw arg
        Assert.True(File.Exists(Path.Combine(_root, "out.docx")));
    }

    [Fact]
    public async Task Cancelling_save_as_is_a_no_op()
    {
        var (svc, _, rep) = await MakeAsync();
        bool revealed = false;
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => null, _ => revealed = true, rep, a => a());
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
        var vm = new ExportDialogViewModel("s1", "Doe: intake/2026", svc, new FakeSettingsService(),
            req => { seen = req; return dest; }, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("Doe intake.md", seen!.DefaultFileName);              // meta title, not the raw arg
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
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => null, _ => { }, rep, a => a());
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
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => dest, _ => { }, rep, a => a())
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
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => dest, _ => { }, rep, a => a())
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
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, ExtraTimestamps = true, IncludeTimestamps = false };

        await vm.ExportCommand.ExecuteAsync(null);

        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains("one two three four five\n", md);                  // still one paragraph
        Assert.DoesNotContain("[00:", md);                                 // no stamps anywhere
        Assert.Empty(rep.Errors);
    }

    [Fact]
    public async Task Text_export_sanitized_txt_filename_filter_and_written_file()
    {
        // design 2026-08-04 section 3: same Save-As shape as markdown, .txt filter, CRLF, no BOM.
        var (svc, _, rep) = await MakeAsync();
        SavePathRequest? seen = null;
        string dest = Path.Combine(_root, "out.txt");
        var vm = new ExportDialogViewModel("s1", "Doe: intake/2026", svc, new FakeSettingsService(),
            req => { seen = req; return dest; }, _ => { }, rep, a => a())
        { Format = ExportFormat.Text };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("Doe intake.txt", seen!.DefaultFileName);             // meta title, not the raw arg
        Assert.Equal("Plain text (*.txt)|*.txt", seen.Filter);
        Assert.True(File.Exists(dest));

        byte[] bytes = await File.ReadAllBytesAsync(dest);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        string txt = await File.ReadAllTextAsync(dest);
        Assert.StartsWith("Doe intake\r\n", txt);          // meta title, not the raw arg
        Assert.Single(rep.Infos);
        Assert.Empty(rep.Errors);
    }

    [Fact]
    public async Task Option_toggles_show_for_text_too()
    {
        var (svc, _, rep) = await MakeAsync();
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => null, _ => { }, rep, a => a())
        { Format = ExportFormat.Text };
        Assert.True(vm.ShowOptionToggles);
        Assert.False(vm.IsDocx);
    }

    [Fact]
    public async Task Vm_seeds_format_and_toggles_from_settings()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService(new Settings
        {
            Export = new ExportSetting
            {
                Format = ExportFormat.Markdown, IncludeTimestamps = false,
                IncludeMarkers = false, ExtraTimestamps = true,
            },
        });

        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => null, _ => { }, rep, a => a());

        Assert.Equal(ExportFormat.Markdown, vm.Format);
        Assert.False(vm.IncludeTimestamps);
        Assert.False(vm.IncludeMarkers);
        Assert.True(vm.ExtraTimestamps);
    }

    [Fact]
    public async Task A_successful_export_persists_the_choices()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService();
        var vm = new ExportDialogViewModel("s1", "T", svc, settings,
            _ => Path.Combine(_root, "out.md"), _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, IncludeMarkers = false };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(1, settings.SaveCount);
        Assert.Equal(ExportFormat.Markdown, settings.Current.Export.Format);
        Assert.False(settings.Current.Export.IncludeMarkers);
    }

    [Fact]
    public async Task A_cancelled_save_as_persists_nothing()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService();
        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => null, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(0, settings.SaveCount);
    }

    [Fact]
    public async Task A_failed_export_persists_nothing()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService();
        // A directory path as the destination makes the FileStream open throw.
        string bad = Path.Combine(_root, "a-directory");
        Directory.CreateDirectory(bad);
        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => bad, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.NotEmpty(rep.Errors);
        Assert.Equal(0, settings.SaveCount);
    }

    [Fact]
    public async Task A_settings_save_failure_is_reported_but_the_export_still_succeeds()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new ThrowingSettingsService();
        string dest = Path.Combine(_root, "out.md");
        string? revealed = null;
        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => dest,
            p => revealed = p, rep, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(File.Exists(dest));                 // the export itself landed
        Assert.Equal(dest, revealed);                   // reveal not suppressed
        Assert.Single(rep.Infos);                       // success Info not suppressed
        Assert.Contains(rep.Errors, e => e.StartsWith("Saving export choices", StringComparison.Ordinal));
    }

    private sealed class ThrowingSettingsService : ISettingsService
    {
        public Settings Current { get; } = new();
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        {
            Changed?.Invoke(Current, updated);          // keeps the compiler quiet about the event
            throw new IOException("settings.json is locked");
        }
    }

    [Fact]
    public async Task Cadence_offers_four_presets_and_defaults_to_fifteen_seconds()
    {
        var (svc, _, rep) = await MakeAsync();
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => null, _ => { }, rep, a => a());

        Assert.Equal([10000, 15000, 30000, 60000], vm.CadenceChoices.Select(c => c.Ms));
        Assert.Equal(["10 s", "15 s", "30 s", "60 s"], vm.CadenceChoices.Select(c => c.Label));
        Assert.Equal(15000, vm.CadenceIntervalMs);
        Assert.Equal(15000, vm.SelectedCadenceMs);
    }

    [Fact]
    public async Task A_non_preset_settings_value_stays_effective_and_displays_as_the_nearest_preset()
    {
        // settings.json is user-editable: a hand-typed 20000 must not be rewritten to 15000
        // before the user has chosen anything (design 2026-08-04 section 5).
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService(new Settings
        { Export = new ExportSetting { CadenceIntervalMs = 20000 } });
        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => null, _ => { }, rep, a => a());

        // Order matters: read SelectedCadenceMs FIRST, then re-check CadenceIntervalMs AFTER.
        // A getter that mutated as a side effect (e.g. snapping CadenceIntervalMs to the nearest
        // preset on read) would still return the right value here, so the only way to catch that
        // regression is to assert the effective value is still untouched once the read is done.
        Assert.Equal(15000, vm.SelectedCadenceMs);       // nearest preset for DISPLAY only
        Assert.Equal(20000, vm.CadenceIntervalMs);       // effective value still preserved after the read
    }

    [Fact]
    public async Task Picking_a_preset_replaces_the_effective_value_and_persists_on_export()
    {
        var (svc, paths, rep) = await MakeAsync();
        await SeedLongTurnAsync(paths);
        var settings = new FakeSettingsService();
        string dest = Path.Combine(_root, "cad.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, ExtraTimestamps = true };

        vm.SelectedCadenceMs = 10000;
        Assert.Equal(10000, vm.CadenceIntervalMs);

        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Equal(10000, settings.Current.Export.CadenceIntervalMs);

        // The 10s cadence splits the seeded turn earlier than the 15s default did.
        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains("(cont'd):", md);
    }

    [Fact]
    public async Task The_filename_template_drives_the_save_as_default_name()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService(new Settings
        { Export = new ExportSetting { FilenameTemplate = "{date} {title}" } });
        SavePathRequest? seen = null;
        var vm = new ExportDialogViewModel("s1", "ignored", svc, settings,
            req => { seen = req; return null; }, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("2026-07-03 Doe intake.md", seen!.DefaultFileName);
    }

    [Fact]
    public async Task Zip_ignores_the_template_and_keeps_the_session_id_name()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService(new Settings
        { Export = new ExportSetting { FilenameTemplate = "{date} {title}" } });
        SavePathRequest? seen = null;
        var vm = new ExportDialogViewModel("s1", "T", svc, settings,
            req => { seen = req; return null; }, _ => { }, rep, a => a());

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("s1.zip", seen!.DefaultFileName);
    }

    [Fact]
    public async Task A_null_latest_summary_provider_exports_with_no_summary_and_no_crash()
    {
        // Every existing unit-test construction of MaintenanceService gets this for free.
        var (svc, _, _) = await MakeAsync();
        Assert.Null(svc.LatestSummaryProvider);
        string dest = Path.Combine(_root, "nosum.md");

        await svc.ExportMarkdownAsync("s1", dest,
            new ExportOptions { IncludeSummary = true }, null, default);

        Assert.DoesNotContain(ExportNotices.SummaryHeading, await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task A_configured_latest_summary_provider_renders_visible_summary_content()
    {
        // Positive counterpart to the null-provider test above (Task 8 review finding): a
        // LoadSummaryAsync that always returned null would still pass every other test in this
        // round, so this one proves a REAL provider's content actually reaches the written file.
        var (svc, _, _) = await MakeAsync();
        svc.LatestSummaryProvider = (sessionId, ct) => Task.FromResult<SummaryVersion?>(
            new SummaryVersion("sum-1", new DateTimeOffset(2026, 8, 1, 14, 22, 0, TimeSpan.Zero), "v1",
                new AssistantModelRef("Qwen3-4B-Instruct-2507.gguf", "abc123", "cuda"),
                2, "## Summary\nThey agreed to file.\n", Stale: false));
        string dest = Path.Combine(_root, "sum.md");

        await svc.ExportMarkdownAsync("s1", dest,
            new ExportOptions { IncludeSummary = true }, null, default);

        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains(ExportNotices.SummaryHeading, md);
        Assert.Contains(AssistantPrompts.DraftLabel, md);
        Assert.Contains("They agreed to file.", md);
    }

    [Fact]
    public async Task IncludeSummary_false_omits_the_summary_even_with_a_configured_provider()
    {
        // Opt-out counterpart (task-9 review finding 2, the dangerous direction): every other
        // service-level export test drives IncludeSummary = true with a configured provider, so a
        // dropped or inverted "!options.IncludeSummary" guard in LoadSummaryAsync would silently
        // attach AI-generated content to an evidentiary export the user explicitly opted OUT of,
        // and the whole suite would stay green. This test would catch exactly that.
        var (svc, _, _) = await MakeAsync();
        svc.LatestSummaryProvider = (sessionId, ct) => Task.FromResult<SummaryVersion?>(
            new SummaryVersion("sum-1", new DateTimeOffset(2026, 8, 1, 14, 22, 0, TimeSpan.Zero), "v1",
                new AssistantModelRef("Qwen3-4B-Instruct-2507.gguf", "abc123", "cuda"),
                2, "## Summary\nThey agreed to file.\n", Stale: false));
        string dest = Path.Combine(_root, "nosum2.md");

        await svc.ExportMarkdownAsync("s1", dest,
            new ExportOptions { IncludeSummary = false }, null, default);

        string md = await File.ReadAllTextAsync(dest);
        Assert.DoesNotContain(ExportNotices.SummaryHeading, md);
    }

    [Fact]
    public async Task Include_summary_is_off_by_default_and_persists_when_ticked()
    {
        var (svc, _, rep) = await MakeAsync();
        var settings = new FakeSettingsService();
        string dest = Path.Combine(_root, "sum.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, settings, _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        Assert.False(vm.IncludeSummary);
        vm.IncludeSummary = true;
        await vm.ExportCommand.ExecuteAsync(null);

        Assert.True(settings.Current.Export.IncludeSummary);
    }

    [Fact]
    public async Task Include_summary_off_produces_no_summary_section_even_when_one_exists()
    {
        // VM-layer counterpart to IncludeSummary_false_omits_the_summary_even_with_a_configured_provider
        // above: that test drives MaintenanceService.ExportMarkdownAsync directly and never touches
        // ExportDialogViewModel, so it cannot catch a bug where the VM's real ExportAsync wiring
        // ignores IncludeSummary (e.g. a hardcoded IncludeSummary = true in the ExportOptions build).
        // This one runs the full VM -> ExportCommand -> service path with IncludeSummary left at its
        // default (false).
        var (svc, _, rep) = await MakeAsync();
        svc.LatestSummaryProvider = (_, _) => Task.FromResult<SummaryVersion?>(
            new SummaryVersion("sum-1", DateTimeOffset.UnixEpoch, "v1",
                new AssistantModelRef("m.gguf", "sha", "cpu"), 2, "## Summary\nx\n", false));
        string dest = Path.Combine(_root, "nosum.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.DoesNotContain(ExportNotices.SummaryHeading, await File.ReadAllTextAsync(dest));
    }

    [Fact]
    public async Task Include_summary_on_reaches_the_document()
    {
        // Positive VM-layer counterpart to the off test above: proves the checkbox's true state
        // actually reaches ExportOptions.IncludeSummary through the real command pipeline, not just
        // through a directly-constructed ExportOptions (that direction is already covered by
        // A_configured_latest_summary_provider_renders_visible_summary_content at the service layer).
        var (svc, _, rep) = await MakeAsync();
        svc.LatestSummaryProvider = (_, _) => Task.FromResult<SummaryVersion?>(
            new SummaryVersion("sum-1", DateTimeOffset.UnixEpoch, "v1",
                new AssistantModelRef("m.gguf", "sha", "cpu"), 2, "## Summary\nThey agreed.\n", false));
        string dest = Path.Combine(_root, "withsum.md");
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => dest, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, IncludeSummary = true };

        await vm.ExportCommand.ExecuteAsync(null);

        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains(ExportNotices.SummaryHeading, md);
        Assert.Contains(AssistantPrompts.DraftLabel, md);
        Assert.Contains("They agreed.", md);
    }

    [Fact]
    public async Task A_summary_export_leaves_session_txt_byte_identical()
    {
        // The summary is EXPORT-ONLY: SessionTextView.Summary stays null, so session.txt does not
        // vary with assistant state and never needs regenerating when a summary is generated
        // (design 2026-08-04 section 7).
        var (svc, paths, rep) = await MakeAsync();
        // MaintenanceService has no per-session regenerate; RegenerateAllAsync covers the one
        // session this fixture seeds. StoragePaths.SessionTxt(id) is session.txt.
        await svc.RegenerateAllAsync(null, CancellationToken.None);
        byte[] before = await File.ReadAllBytesAsync(paths.SessionTxt("s1"));

        svc.LatestSummaryProvider = (_, _) => Task.FromResult<SummaryVersion?>(
            new SummaryVersion("sum-1", DateTimeOffset.UnixEpoch, "v1",
                new AssistantModelRef("m.gguf", "sha", "cpu"), 2, "## Summary\nThey agreed.\n", false));
        var vm = new ExportDialogViewModel("s1", "T", svc, new FakeSettingsService(),
            _ => Path.Combine(_root, "s.md"), _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown, IncludeSummary = true };
        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal(before, await File.ReadAllBytesAsync(paths.SessionTxt("s1")));
    }
}
