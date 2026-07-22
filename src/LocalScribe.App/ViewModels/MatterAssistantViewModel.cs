using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Assistant;
namespace LocalScribe.App.ViewModels;

/// <summary>One tagged session's summary status on the Matters Assistant tab (design
/// 2026-07-18 section 7.6): generated / stale / missing-with-generate-offer.</summary>
public sealed record MatterSummaryStatusRow(string SessionId, string Title, string DateDisplay,
    string StatusText, bool HasSummary, bool IsStale);

/// <summary>The Matters Assistant tab state (design 2026-07-18 sections 7.5-7.6): matter chat
/// over per-session SUMMARIES (never transcripts, hard-scoped to one matter by construction)
/// plus the per-session summary status list and the EXPLICIT coverage disclosure after each
/// answer - included/omitted/no-summary are always listed, never silently truncated. One
/// instance per selected matter; Shutdown on matter switch tears the warm helper down.</summary>
public sealed partial class MatterAssistantViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<MatterSummarySource>>> _loadSummarySources;
    private readonly IUiErrorReporter _reporter;
    private readonly Action<Action> _dispatch;

    public string MatterId { get; }
    public ObservableCollection<MatterSummaryStatusRow> SummaryRows { get; } = [];
    /// <summary>"" until the first answer; then "Last answer used summaries from N of M tagged
    /// sessions." plus the omitted and no-summary-yet lists BY TITLE (design 7.5).</summary>
    [ObservableProperty] private string _coverageText = "";
    public AssistantChatViewModel Chat { get; }
    public IRelayCommand<MatterSummaryStatusRow> GenerateSummaryCommand { get; }
    /// <summary>Raised with the session id whose summary should be (re)generated. The App
    /// composition routes it to the foundation's summary-generation surface.</summary>
    public event Action<string>? SummaryGenerationRequested;

    public MatterAssistantViewModel(string matterId,
        Func<CancellationToken, Task<IReadOnlyList<MatterSummarySource>>> loadSummarySources,
        Func<AssistantQaService?> chatServiceFactory, AssistantChatStore store,
        IUiErrorReporter reporter, Action<Action> dispatch, Func<string?>? busyReason = null)
    {
        MatterId = matterId;
        (_loadSummarySources, _reporter, _dispatch) = (loadSummarySources, reporter, dispatch);
        Chat = new AssistantChatViewModel(chatServiceFactory, store, reporter, dispatch, busyReason);
        Chat.TurnCompleted += UpdateCoverage;
        GenerateSummaryCommand = new RelayCommand<MatterSummaryStatusRow>(row =>
        {
            if (row is not null) SummaryGenerationRequested?.Invoke(row.SessionId);
        });
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        try
        {
            var sources = await _loadSummarySources(ct);
            var rows = sources
                .OrderByDescending(s => s.StartedAtLocal)
                .ThenByDescending(s => s.SessionId, StringComparer.Ordinal)
                .Select(RowFor).ToList();
            _dispatch(() =>
            {
                SummaryRows.Clear();
                foreach (var r in rows) SummaryRows.Add(r);
            });
        }
        catch (Exception ex) { _reporter.Report("Load matter summary status", ex); }
    }

    private static MatterSummaryStatusRow RowFor(MatterSummarySource s) => new(
        s.SessionId, s.Title,
        s.StartedAtLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        string.IsNullOrWhiteSpace(s.SummaryMarkdown) ? "No summary yet"
            : s.Stale ? "Summary out of date" : "Summary ready",
        !string.IsNullOrWhiteSpace(s.SummaryMarkdown), s.Stale);

    private void UpdateCoverage(AssistantChatTurn turn)
    {
        string Names(IReadOnlyList<string> ids) => string.Join(", ",
            ids.Select(id => SummaryRows.FirstOrDefault(r => r.SessionId == id)?.Title ?? id));
        // The turn's three coverage lists are an EXHAUSTIVE partition of the matter's tagged
        // sessions built at ask time (MatterQaContextBuilder), so their sum is the authoritative
        // "of N" total for THIS answer. Never derive it from the cached SummaryRows.Count, which can
        // be empty (not yet refreshed) or stale (a session tagged / a summary regenerated while the
        // chat stayed open) - that misrepresents scope size (e.g. a nonsensical "1 of 0"), a 7.5
        // evidentiary defect.
        int total = turn.IncludedSessionIds.Count + turn.OmittedSessionIds.Count
            + turn.MissingSummarySessionIds.Count;
        var parts = new List<string>
        {
            "Last answer used summaries from " + turn.IncludedSessionIds.Count + " of "
                + total + " tagged sessions.",
        };
        if (turn.OmittedSessionIds.Count > 0)
            parts.Add("Omitted (context budget): " + Names(turn.OmittedSessionIds) + ".");
        if (turn.MissingSummarySessionIds.Count > 0)
            parts.Add("No summary yet: " + Names(turn.MissingSummarySessionIds) + ".");
        _dispatch(() => CoverageText = string.Join(" ", parts));
    }

    /// <summary>Matter switch / page teardown: the scope change tears the warm helper down.</summary>
    public void Shutdown() => Chat.Shutdown();
}
