using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SearchPageViewModelTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-search-page-" + Guid.NewGuid().ToString("N"));
    private readonly StoragePaths _paths;

    public SearchPageViewModelTests()
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

    [Fact]
    public async Task Query_produces_ranked_cards_with_clickable_snippet_rows()
    {
        var t = new DateTimeOffset(2026, 6, 1, 2, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-two", "Two hits", t, texts: new[] { "acme first line", "acme second line" });
        await WriteSessionAsync("s-one", "One hit", t.AddDays(1), texts: new[] { "acme only once" });
        var (vm, _, errors) = await MakeVmAsync();

        vm.QueryText = "acme";
        await (vm.PendingSearch ?? Task.CompletedTask);

        Assert.False(vm.ShowNoQuery);
        Assert.False(vm.ShowNoResults);
        Assert.Equal(new[] { "s-two", "s-one" }, vm.Results.Select(c => c.SessionId).ToArray());
        var card = vm.Results[0];
        Assert.Equal("Two hits", card.Title);
        Assert.Equal("Webex", card.App);
        Assert.Equal(2, card.Snippets.Count);
        var snip = card.Snippets[0];
        Assert.Contains("acme first line", snip.Snippet);
        Assert.Equal("acme", snip.MatchedTerm);
        Assert.Equal("[00:00]", snip.StampDisplay);
        Assert.Equal(0, snip.Seq);
        Assert.False(snip.MatchesOriginalOnly);
        Assert.DoesNotContain("(matches original text)", snip.SnippetDisplay);

        (string, int, string)? opened = null;
        vm.OpenSnippetRequested += (sid, seq, term) => opened = (sid, seq, term);
        vm.OpenSnippetCommand.Execute(snip);
        Assert.NotNull(opened);
        Assert.Equal(("s-two", 0, "acme"), opened!.Value);
        Assert.Empty(errors.Reports);
    }

    [Fact]
    public async Task Card_exposes_the_session_date_and_matter_labels()
    {
        // B4-6: DateDisplay + MattersDisplay were unasserted by the card tests. UtcOffsetMinutes is
        // pinned so DateDisplay is deterministic (the ToLocalTime fallback would be machine-zoned).
        var t = new DateTimeOffset(2026, 6, 1, 14, 30, 0, TimeSpan.Zero);
        await new MatterStore(_paths.MattersDir).SaveAsync(
            new Matter { Id = "M-1", Name = "Acme Litigation", Reference = "AC-1" }, CancellationToken.None);
        await new SessionStore(_paths.SessionJson("s-1")).SaveAsync(new SessionRecord
        {
            Id = "s-1", App = AppKind.Webex, StartedAtUtc = t, EndedAtUtc = t.AddMinutes(5),
            DurationMs = 300_000, UtcOffsetMinutes = 0,
        }, CancellationToken.None);
        await new MetadataStore(_paths.MetaJson("s-1")).SaveAsync(
            new SessionMeta { Title = "Hearing", MatterIds = new[] { "M-1" } }, CancellationToken.None);
        await new TranscriptStore(_paths.TranscriptJsonl("s-1")).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "acme term", "Me"), CancellationToken.None);
        var (vm, _, _) = await MakeVmAsync();
        await vm.OnNavigatedToAsync();                       // populate the matter-label lookup

        vm.QueryText = "acme";
        await (vm.PendingSearch ?? Task.CompletedTask);

        var card = Assert.Single(vm.Results);
        Assert.Equal("2026-06-01 14:30", card.DateDisplay);  // stored +00:00 offset
        Assert.Equal("M-1-AC-1 Acme Litigation", card.MattersDisplay);
    }

    [Fact]
    public void Clicking_a_speaker_name_only_row_opens_the_read_view_without_a_target_seq()
    {
        // B4-6: the Seq -1 "just open, no target" click-through (a speaker-name hit with no spoken
        // line) had no dedicated test. The command must forward seq -1 verbatim; row shows no stamp.
        var index = new SearchIndexService(_paths, () => new Settings(), TimeProvider.System, 0);
        var maintenance = new MaintenanceService(_paths, new FakeSettings(new Settings()),
            new NoopBin(), TimeProvider.System);
        var vm = new SearchPageViewModel(index, maintenance, new RecordingErrors(),
            dispatch: a => a(), new UtcZoneTimeProvider(), debounceMs: 0);
        (string, int, string)? opened = null;
        vm.OpenSnippetRequested += (sid, seq, term) => opened = (sid, seq, term);

        var row = new SearchSnippetRow("s-x", Seq: -1, "zamora", Stamp: "", "Zeb Zamora", "",
            MatchesOriginalOnly: false);
        vm.OpenSnippetCommand.Execute(row);

        Assert.Equal(("s-x", -1, "zamora"), opened);
        Assert.Equal("", row.StampDisplay);                  // no timestamp for a name-only hit
    }

    [Fact]
    public async Task Facets_narrow_by_matter_app_and_date_range()
    {
        var t = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
        await new MatterStore(_paths.MattersDir).SaveAsync(
            new Matter { Id = "M-1", Name = "Acme Litigation" }, CancellationToken.None);
        await WriteSessionAsync("s-w", "Webex one", t, app: AppKind.Webex,
            matterIds: new[] { "M-1" }, texts: new[] { "shared term" });
        await WriteSessionAsync("s-t", "Teams one", t.AddDays(3), app: AppKind.Teams,
            matterIds: new[] { "M-2" }, texts: new[] { "shared term" });
        var (vm, _, _) = await MakeVmAsync();
        await vm.OnNavigatedToAsync();
        Assert.Contains(vm.MatterOptions, o => o.Id == "M-1");            // facet options from the index
        Assert.Contains(vm.AppOptions, o => o.Id == "Teams");             // AppKind names + "All apps"
        Assert.Contains(vm.AppOptions, o => o.Id is null);

        vm.QueryText = "shared";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Equal(2, vm.Results.Count);

        vm.AppFilterId = "Teams";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Equal("s-t", Assert.Single(vm.Results).SessionId);

        vm.AppFilterId = null;
        vm.MatterFilterId = "M-1";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Equal("s-w", Assert.Single(vm.Results).SessionId);

        vm.MatterFilterId = null;
        vm.FromDate = new DateTime(2026, 6, 3);            // UTC-pinned zone: day boundaries are UTC
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Equal("s-t", Assert.Single(vm.Results).SessionId);

        vm.FromDate = null;
        vm.ToDate = new DateTime(2026, 6, 1);              // "To" includes the whole picked day
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Equal("s-w", Assert.Single(vm.Results).SessionId);
    }

    [Fact]
    public async Task Empty_query_and_no_result_states()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-1", "Alpha", t, texts: new[] { "hello world" });
        var (vm, _, _) = await MakeVmAsync();

        Assert.True(vm.ShowNoQuery);
        vm.QueryText = "zzzznothing";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.False(vm.ShowNoQuery);
        Assert.True(vm.ShowNoResults);
        Assert.Empty(vm.Results);

        vm.QueryText = "";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.True(vm.ShowNoQuery);
        Assert.False(vm.ShowNoResults);
        Assert.Empty(vm.Results);
    }

    [Fact]
    public async Task Indexing_state_clears_on_ReadyChanged_and_the_pending_query_reruns()
    {
        var t = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        await WriteSessionAsync("s-1", "Alpha", t, texts: new[] { "needle in here" });
        var (vm, index, _) = await MakeVmAsync(initialize: false);

        Assert.True(vm.IsIndexing);
        vm.QueryText = "needle";
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.Empty(vm.Results);                          // index not built yet
        Assert.False(vm.ShowNoResults);                    // "indexing..." is not "no results"

        await index.InitializeAsync(CancellationToken.None);   // fires ReadyChanged -> re-query
        await (vm.PendingSearch ?? Task.CompletedTask);
        Assert.False(vm.IsIndexing);
        Assert.Single(vm.Results);
    }
}
