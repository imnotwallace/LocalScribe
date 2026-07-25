using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Task 11: the Related-discussion section (semantic seam). Mirrors
/// SearchPageViewModelTests's arrange plumbing exactly (same temp-StoragePaths seeding, same
/// inline dispatch convention) - the semantic-specific cases live here.</summary>
public sealed class SearchPageViewModelSemanticTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-search-semantic-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public SearchPageViewModelSemanticTests()
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

    /// <summary>Pins the local zone to UTC so date-facet day boundaries are deterministic.</summary>
    private sealed class UtcZoneTimeProvider : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class FakeSemantic : ISemanticSearch
    {
        public (int Fresh, int Eligible) Coverage { get; set; } = (1, 1);
        public event Action? Changed;
        public Func<IReadOnlyList<SemanticResult>>? OnQuery { get; set; }
        public int Queries;
        public Task<IReadOnlyList<SemanticResult>> QueryAsync(SearchQuery query,
            IReadOnlyList<SearchResult> lexicalResults, CancellationToken ct)
        { Queries++; return Task.FromResult(OnQuery?.Invoke() ?? []); }
        public void RaiseChanged() => Changed?.Invoke();
    }

    private async Task WriteSessionAsync(string id, string title, DateTimeOffset started,
        AppKind app = AppKind.Webex, string[]? matterIds = null, params string[] texts)
    {
        await new SessionStore(_paths.SessionJson(id)).SaveAsync(new SessionRecord
        {
            Id = id, App = app, StartedAtUtc = started, EndedAtUtc = started.AddMinutes(5),
            DurationMs = 300_000,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson(id)).SaveAsync(
            new SessionMeta { Title = title, MatterIds = matterIds ?? [] }, CancellationToken.None);
        var store = new TranscriptStore(_paths.TranscriptJsonl(id));
        for (int i = 0; i < texts.Length; i++)
            await store.AppendAsync(TranscriptLine.Segment(i, TranscriptSource.Local,
                i * 5000, i * 5000 + 1000, texts[i], "Me"), CancellationToken.None);
    }

    private async Task<(SearchPageViewModel Vm, SearchIndexService Index, RecordingErrors Errors)>
        MakeVmAsync(bool initialize = true)
    {
        var index = new SearchIndexService(_paths, () => new Settings(), TimeProvider.System,
            saveDebounceMs: 0);
        if (initialize) await index.InitializeAsync(CancellationToken.None);
        var maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), TimeProvider.System);
        var errors = new RecordingErrors();
        var vm = new SearchPageViewModel(index, maintenance, errors, dispatch: a => a(),
            new UtcZoneTimeProvider(), debounceMs: 0);
        return (vm, index, errors);
    }

    /// <summary>Builds a SearchSessionEntry matching a seeded session, for a fake QueryAsync
    /// result - the entry only needs the fields ToRelatedCard reads.</summary>
    private static SearchSessionEntry MakeEntry(string id, string title, DateTimeOffset started,
        string app = "Webex") => new()
    {
        SessionId = id, Title = title, StartedAtUtc = started, UtcOffsetMinutes = 0, App = app,
    };

    [Fact]
    public async Task Related_section_fills_from_the_semantic_seam()
    {
        var t = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-1", "Alpha", t, texts: new[] { "unrelated line" });
        var (vm, _, _) = await MakeVmAsync();
        var semantic = new FakeSemantic();
        var entry = MakeEntry("s-1", "Alpha", t);
        semantic.OnQuery = () => new List<SemanticResult>
        {
            new(entry, new[] { new SemanticHit(0, 0, 0, "related text", 0.9f) }, 0.9f),
        };
        vm.AttachSemantic(semantic);

        vm.QueryText = "acme";
        await (vm.PendingSearch ?? Task.CompletedTask);

        var card = Assert.Single(vm.RelatedResults);
        Assert.True(vm.ShowRelatedSection);
        Assert.Equal("s-1", card.SessionId);
        var row = Assert.Single(card.Snippets);
        Assert.Equal("", row.Speaker);
        Assert.Equal("", row.MatchedTerm);
        Assert.Equal("related text", row.Snippet);
    }

    [Fact]
    public async Task Without_semantic_the_section_stays_hidden()
    {
        var t = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-1", "Alpha", t, texts: new[] { "hello" });
        var (vm, _, _) = await MakeVmAsync();

        vm.QueryText = "hello";
        await (vm.PendingSearch ?? Task.CompletedTask);

        Assert.False(vm.ShowRelatedSection);
        Assert.Empty(vm.RelatedResults);
    }

    [Fact]
    public async Task Semantic_failure_degrades_to_a_status_line()
    {
        var t = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-1", "Alpha", t, texts: new[] { "hello" });
        var (vm, _, errors) = await MakeVmAsync();
        var semantic = new FakeSemantic { OnQuery = () => throw new InvalidOperationException("boom") };
        vm.AttachSemantic(semantic);

        vm.QueryText = "hello";
        await (vm.PendingSearch ?? Task.CompletedTask);

        Assert.Contains("unavailable", vm.RelatedStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Single(vm.Results);                       // lexical untouched by the semantic failure
        Assert.False(vm.IsRelatedSearching);
    }

    [Fact]
    public async Task Incomplete_coverage_shows_the_searched_N_of_M_note()
    {
        var t = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-1", "Alpha", t, texts: new[] { "hello" });
        var (vm, _, _) = await MakeVmAsync();
        var semantic = new FakeSemantic { Coverage = (84, 120) };
        vm.AttachSemantic(semantic);

        vm.QueryText = "hello";
        await (vm.PendingSearch ?? Task.CompletedTask);

        Assert.Equal("searched 84 of 120 sessions - indexing continues", vm.RelatedCoverageNote);
    }

    [Fact]
    public async Task Full_coverage_hides_the_note()
    {
        var t = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-1", "Alpha", t, texts: new[] { "hello" });
        var (vm, _, _) = await MakeVmAsync();
        var semantic = new FakeSemantic { Coverage = (5, 5) };
        vm.AttachSemantic(semantic);

        vm.QueryText = "hello";
        await (vm.PendingSearch ?? Task.CompletedTask);

        Assert.Equal("", vm.RelatedCoverageNote);
    }

    [Fact]
    public async Task Clearing_the_query_clears_related()
    {
        var t = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-1", "Alpha", t, texts: new[] { "hello" });
        var (vm, _, _) = await MakeVmAsync();
        var semantic = new FakeSemantic();
        var entry = MakeEntry("s-1", "Alpha", t);
        semantic.OnQuery = () => new List<SemanticResult>
        {
            new(entry, new[] { new SemanticHit(0, 0, 0, "related text", 0.9f) }, 0.9f),
        };
        vm.AttachSemantic(semantic);
        vm.QueryText = "hello";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.NotEmpty(vm.RelatedResults);

        vm.QueryText = "";
        await (vm.PendingSearch ?? Task.CompletedTask);

        Assert.Empty(vm.RelatedResults);
        Assert.False(vm.ShowRelatedSection);
    }
}
