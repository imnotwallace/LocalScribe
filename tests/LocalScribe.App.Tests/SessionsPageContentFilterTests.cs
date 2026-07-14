using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SessionsPageContentFilterTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-content-filter-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public SessionsPageContentFilterTests()
    {
        _paths = new StoragePaths(_root);
        Directory.CreateDirectory(_paths.SessionsDir);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class RecordingErrors : IUiErrorReporter
    {
        public List<string> Reports { get; } = [];
        public void Report(string context, Exception ex) => Reports.Add(context + ": " + ex.Message);
        public void Info(string message) { }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(Settings current) => Current = current;
        public Settings Current { get; private set; }
        public event Action<Settings, Settings>? Changed;
        public Task SaveAsync(Settings updated, CancellationToken ct)
        { var old = Current; Current = updated; Changed?.Invoke(old, updated); return Task.CompletedTask; }
    }

    private sealed class NoopBin : IRecycleBin
    {
        public void SendToRecycleBin(string path) { }
    }

    private async Task WriteSessionAsync(string id, string title, string text, DateTimeOffset started)
    {
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = AppKind.Webex, StartedAtUtc = started,
            EndedAtUtc = started.AddMinutes(5), DurationMs = 300_000,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = title, Medium = Medium.Webex }, CancellationToken.None);
        await new TranscriptStore(_paths.TranscriptJsonl(id)).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, text, "Me"),
            CancellationToken.None);
    }

    private async Task<(SessionsPageViewModel Vm, SearchIndexService? Index, RecordingErrors Errors)>
        MakeVmAsync(bool withIndex = true)
    {
        var maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), TimeProvider.System);
        var (controller, _, _, _) = LiveTestDoubles.MakeController(_root);
        var session = new SessionViewModel(controller, new Settings(), dispatch: a => a(),
            startOptions: LiveTestDoubles.Options());
        var errors = new RecordingErrors();
        SearchIndexService? index = null;
        if (withIndex)
        {
            index = new SearchIndexService(_paths, () => new Settings(), TimeProvider.System,
                saveDebounceMs: 0);
            await index.InitializeAsync(CancellationToken.None);
        }
        var vm = new SessionsPageViewModel(maintenance, session, new WindowRegistry(), errors,
            dispatch: a => a(), TimeProvider.System, revealInExplorer: _ => { },
            searchIndex: index, contentSearchDebounceMs: 0);
        return (vm, index, errors);
    }

    [Fact]
    public async Task Content_match_keeps_the_row_visible_with_one_snippet_line()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-a", "Alpha", "we discussed the retainer agreement", t);
        await WriteSessionAsync("s-b", "Bravo", "totally unrelated content", t.AddHours(1));
        var (vm, _, errors) = await MakeVmAsync();
        await vm.OnNavigatedToAsync();

        vm.FilterText = "retainer";                        // matches NO title
        Assert.Empty(vm.Rows);                             // the instant title pass hides everything
        await (vm.ContentFilterTask ?? Task.CompletedTask);

        var row = Assert.Single(vm.Rows);                  // the content match re-surfaces s-a
        Assert.Equal("s-a", row.Id);
        Assert.NotNull(row.ContentSnippet);
        Assert.Contains("retainer", row.ContentSnippet);
        Assert.StartsWith("Me:", row.ContentSnippet);      // speaker-prefixed snippet
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task Clearing_the_filter_clears_snippets_and_title_matches_still_work()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-a", "Alpha", "we discussed the retainer agreement", t);
        await WriteSessionAsync("s-b", "Bravo", "totally unrelated content", t.AddHours(1));
        var (vm, _, _) = await MakeVmAsync();
        await vm.OnNavigatedToAsync();

        vm.FilterText = "retainer";
        await (vm.ContentFilterTask ?? Task.CompletedTask);
        Assert.Single(vm.Rows);

        vm.FilterText = "";
        Assert.Equal(2, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.Null(r.ContentSnippet));

        vm.FilterText = "Bravo";                           // pure title match: instant
        Assert.Contains(vm.Rows, r => r.Id == "s-b");
        await (vm.ContentFilterTask ?? Task.CompletedTask);
        var bravo = vm.Rows.Single(r => r.Id == "s-b");
        Assert.Null(bravo.ContentSnippet);                 // "Bravo" is nowhere in transcript content
    }

    [Fact]
    public async Task Without_an_index_title_filtering_is_unchanged()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-a", "Alpha", "we discussed the retainer agreement", t);
        var (vm, _, errors) = await MakeVmAsync(withIndex: false);
        await vm.OnNavigatedToAsync();

        vm.FilterText = "retainer";
        Assert.Null(vm.ContentFilterTask);                 // no index -> no content query scheduled
        Assert.Empty(vm.Rows);                             // title-only behavior, exactly as before
        vm.FilterText = "Alph";
        Assert.Single(vm.Rows);
        Assert.Empty(errors.Reports);
    }
}
