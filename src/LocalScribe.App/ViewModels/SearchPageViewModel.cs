// src/LocalScribe.App/ViewModels/SearchPageViewModel.cs
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Model;
using LocalScribe.Core.Projection;
using LocalScribe.Core.Search;
namespace LocalScribe.App.ViewModels;

/// <summary>One snippet row on a search-result card (design 2026-07-13 section 2.2 surface 1):
/// timestamp + speaker + snippet, plus the "(matches original text)" label when the hit lives only
/// in the machine original. Seq/MatchedTerm are the click-through payload (open the read view
/// scrolled to the segment with the term in the find bar); Seq -1 = a speaker-name hit with no
/// spoken line (opens the read view without targeting).</summary>
public sealed record SearchSnippetRow(string SessionId, int Seq, string MatchedTerm, string Stamp,
    string Speaker, string Snippet, bool MatchesOriginalOnly)
{
    public string StampDisplay => Stamp.Length == 0 ? "" : "[" + Stamp + "]";
    public string SnippetDisplay => MatchesOriginalOnly
        ? Snippet + "  (matches original text)" : Snippet;
}

/// <summary>One session card: header fields + its snippet rows, in hit order.</summary>
public sealed record SearchResultCard(string SessionId, string Title, string DateDisplay,
    string App, string MattersDisplay, IReadOnlyList<SearchSnippetRow> Snippets);

/// <summary>Search page (design 2026-07-13 section 2.2 surface 1): debounced (~250 ms) live query
/// over SearchIndexService with matter/date-range/app facets; results as session cards holding
/// snippet rows; empty states for "no query yet" and "no results"; an "indexing..." state while the
/// cold-cache build runs (ReadyChanged re-runs the pending query the moment the index is up).
/// WPF-free; UI mutations marshal via the injected dispatch. Stamps are always session-relative
/// (mm:ss) - deterministic, independent of the wallclock timestamps setting.</summary>
public sealed partial class SearchPageViewModel : ObservableObject
{
    private readonly SearchIndexService _index;
    private readonly MaintenanceService _maintenance;
    private readonly IUiErrorReporter _errors;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;
    private readonly int _debounceMs;
    private CancellationTokenSource? _searchCts;
    private readonly Dictionary<string, (string? Reference, string Name)> _matterLookup =
        new(StringComparer.Ordinal);

    public ObservableCollection<SearchResultCard> Results { get; } = [];
    public ObservableCollection<MatterFilterOption> MatterOptions { get; } = [];

    /// <summary>Pager over result CARDS (design 2026-07-18 section 1): one card = one session
    /// with its snippets, never split across pages. The engine still returns the full ranked
    /// list (ranking needs the whole set); Results holds only the current page.</summary>
    public PagerViewModel Pager { get; } = new();
    private List<SearchResultCard> _allCards = [];

    /// <summary>App facet: "" = all (the WPF-selectable sentinel; null SelectedValue cannot select a
    /// ComboBox item). AppKind's names are the complete source-app vocabulary (SearchSessionEntry.App
    /// is AppKind.ToString()).</summary>
    public IReadOnlyList<MatterFilterOption> AppOptions { get; } =
        new[] { new MatterFilterOption("", "All apps") }
            .Concat(Enum.GetNames<AppKind>().Select(n => new MatterFilterOption(n, n)))
            .ToList();

    [ObservableProperty] private string _queryText = "";
    [ObservableProperty] private string? _matterFilterId = "";
    [ObservableProperty] private string? _appFilterId = "";
    [ObservableProperty] private DateTime? _fromDate;
    [ObservableProperty] private DateTime? _toDate;
    [ObservableProperty] private bool _isIndexing;
    [ObservableProperty] private bool _showNoQuery = true;
    [ObservableProperty] private bool _showNoResults;

    /// <summary>(sessionId, seq, matchedTerm) - the window layer opens/activates the ReadViewWindow
    /// and calls ShowFindAt(seq, term); seq &lt; 0 just opens.</summary>
    public event Action<string, int, string>? OpenSnippetRequested;
    public IRelayCommand<SearchSnippetRow> OpenSnippetCommand { get; }

    /// <summary>Test seam: the in-flight debounced query, if any. Public (this repo has no
    /// InternalsVisibleTo between LocalScribe.App and LocalScribe.App.Tests).</summary>
    public Task? PendingSearch { get; private set; }

    public SearchPageViewModel(SearchIndexService index, MaintenanceService maintenance,
        IUiErrorReporter errors, Action<Action> dispatch, TimeProvider time, int debounceMs = 250)
    {
        (_index, _maintenance, _errors, _dispatch, _time, _debounceMs)
            = (index, maintenance, errors, dispatch, time, debounceMs);
        OpenSnippetCommand = new RelayCommand<SearchSnippetRow>(row =>
        {
            if (row is not null) OpenSnippetRequested?.Invoke(row.SessionId, row.Seq, row.MatchedTerm);
        });
        Pager.Changed += ApplyPage;
        IsIndexing = !index.IsReady;
        // ReadyChanged may fire from the background InitializeAsync: marshal, then re-run the
        // current query so a search typed during "indexing..." resolves the moment the index is up.
        // Both this VM and the index live for the app's lifetime - no unsubscribe needed.
        index.ReadyChanged += () => _dispatch(() =>
        {
            IsIndexing = !_index.IsReady;
            ScheduleSearch();
        });
    }

    partial void OnQueryTextChanged(string value) => ScheduleSearch();
    partial void OnMatterFilterIdChanged(string? value) => ScheduleSearch();
    partial void OnAppFilterIdChanged(string? value) => ScheduleSearch();
    partial void OnFromDateChanged(DateTime? value) => ScheduleSearch();
    partial void OnToDateChanged(DateTime? value) => ScheduleSearch();

    /// <summary>Page-navigation refresh: matter facet options from the matters index (degrading to
    /// an empty list on a fault - the raw-id facet still works), mirroring SessionsPage's rule that
    /// a secondary-index fault never blocks the page. Catches everything (Loaded is async void).</summary>
    public async Task OnNavigatedToAsync()
    {
        IsIndexing = !_index.IsReady;
        try
        {
            var matters = await _maintenance.ListMattersAsync(CancellationToken.None);
            _dispatch(() =>
            {
                _matterLookup.Clear();
                foreach (var m in matters.Matters) _matterLookup[m.Id] = (m.Reference, m.Name);
                string? current = MatterFilterId;
                MatterOptions.Clear();
                MatterOptions.Add(new MatterFilterOption("", "All matters"));
                foreach (var m in matters.Matters)
                    MatterOptions.Add(new MatterFilterOption(m.Id, MatterLabel(m.Id)));
                if (!string.IsNullOrEmpty(current) && MatterOptions.All(o => o.Id != current))
                    MatterFilterId = "";                // stale selection -> All
                else if (MatterFilterId != current)
                    MatterFilterId = current;           // re-assert: a bound ComboBox can null on Clear()
            });
        }
        catch (Exception ex) { _errors.Report("Loading matters", ex); }
    }

    private void ScheduleSearch()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        PendingSearch = RunSearchAsync(cts.Token);
    }

    private async Task RunSearchAsync(CancellationToken ct)
    {
        try
        {
            if (_debounceMs > 0) await Task.Delay(_debounceMs, ct);
            string text = QueryText;
            bool hasQuery = !string.IsNullOrWhiteSpace(text);
            var query = new SearchQuery(text, Facet(MatterFilterId), FacetFromUtc(), FacetToUtc(),
                Facet(AppFilterId));
            IReadOnlyList<SearchResult> results = hasQuery
                ? await Task.Run(() => _index.Query(query), ct)
                : [];
            if (ct.IsCancellationRequested) return;
            _dispatch(() =>
            {
                if (ct.IsCancellationRequested) return;   // superseded by a newer keystroke/facet
                _allCards = results.Select(ToCard).ToList();
                Pager.Reset();                            // a new query always reads from page 1
                Pager.SetTotal(_allCards.Count);
                ApplyPage();
                ShowNoQuery = !hasQuery;
                ShowNoResults = hasQuery && _allCards.Count == 0 && !IsIndexing;
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _errors.Report("Search", ex); }
    }

    private void ApplyPage()
    {
        Results.Clear();
        foreach (var card in Pager.Slice(_allCards)) Results.Add(card);
    }

    private SearchResultCard ToCard(SearchResult r)
    {
        // Session-local date, same fallback rule as every other surface (spec 1.2).
        var startedLocal = r.Session.UtcOffsetMinutes is int offsetMin
            ? r.Session.StartedAtUtc.ToOffset(TimeSpan.FromMinutes(offsetMin))
            : r.Session.StartedAtUtc.ToLocalTime();
        string matters = string.Join(", ", r.Session.MatterIds.Select(MatterLabel));
        var rows = r.Hits.Select(h => new SearchSnippetRow(
            r.Session.SessionId, h.Seq, h.MatchedTerm,
            h.Seq >= 0 ? TimestampFormat.Stamp(h.StartMs, "relative", startedLocal) : "",
            h.Speaker, h.Snippet, h.MatchesOriginalOnly)).ToList();
        return new SearchResultCard(r.Session.SessionId, r.Session.Title,
            startedLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            r.Session.App, matters, rows);
    }

    /// <summary>`{id}-{ref} {name}` / `{id} {name}` / raw id - SessionsPageViewModel.MatterLabel's
    /// exact format, duplicated here because that one is private to its VM.</summary>
    private string MatterLabel(string id)
    {
        if (_matterLookup.TryGetValue(id, out var m))
            return m.Reference is { Length: > 0 } r ? $"{id}-{r} {m.Name}" : $"{id} {m.Name}";
        return id;
    }

    /// <summary>"" (the combo's "All" sentinel) and null both mean "no facet" to the engine.</summary>
    private static string? Facet(string? id) => string.IsNullOrEmpty(id) ? null : id;

    // Date facets: the picked day is interpreted in the viewer's zone (TimeProvider.LocalTimeZone,
    // test-pinnable); From = that day's start (inclusive), To = the NEXT day's start (exclusive
    // upper bound in SearchQueryEngine), so "To" includes the whole picked day.
    private DateTimeOffset? FacetFromUtc() => FromDate is { } d ? LocalDayStartUtc(d) : null;
    private DateTimeOffset? FacetToUtc() => ToDate is { } d ? LocalDayStartUtc(d.AddDays(1)) : null;

    private DateTimeOffset LocalDayStartUtc(DateTime day)
    {
        var local = DateTime.SpecifyKind(day.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, _time.LocalTimeZone.GetUtcOffset(local)).ToUniversalTime();
    }
}
