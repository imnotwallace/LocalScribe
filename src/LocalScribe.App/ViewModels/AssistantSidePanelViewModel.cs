using CommunityToolkit.Mvvm.ComponentModel;
namespace LocalScribe.App.ViewModels;

/// <summary>Composite state for the reusable Assistant side panel (addendum 2026-07-25): an
/// optional Summary section (session scope only - matter scope passes null and the Expander
/// collapses away), the thread-managed chat, the open/closed bit the host persists per window
/// family, and a coverage-text slot the matter host forwards its disclosure into. Deliberately
/// logic-free: every behavior lives on the wrapped VMs so both hosts stay identical.</summary>
public sealed partial class AssistantSidePanelViewModel : ObservableObject
{
    public AssistantSidePanelViewModel(AssistantTabViewModel? summary,
        AssistantChatThreadsViewModel threads)
        => (Summary, Threads) = (summary, threads);

    public AssistantTabViewModel? Summary { get; }
    public bool HasSummarySection => Summary is not null;
    public AssistantChatThreadsViewModel Threads { get; }
    public AssistantChatViewModel Chat => Threads.Chat;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _coverageText = "";

    public async Task LoadAsync(string? summarySessionId, CancellationToken ct)
    {
        if (Summary is not null && summarySessionId is not null)
            await Summary.LoadAsync(summarySessionId, ct);
        await Threads.LoadAsync(ct);
    }
}
