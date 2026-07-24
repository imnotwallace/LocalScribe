using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.App.Services;
using LocalScribe.Core.Assistant;
namespace LocalScribe.App.ViewModels;

/// <summary>One selector row. Display carries the "(archived)" suffix so the dropdown needs no
/// template trigger; HasRecap rides along so selection can flip the condense indicator without a
/// second store read.</summary>
public sealed record ThreadListItem(string Id, string Name, bool Archived, bool HasRecap)
{
    public string Display => Archived ? Name + " (archived)" : Name;
}

/// <summary>Thread management around one scope's AssistantChatViewModel (addendum 2026-07-25):
/// the dropdown list, New/Rename/Archive/Unarchive, ShowArchived, and the condense indicator.
/// All store writes are full load-modify-save (v2 rule); the wrapped Chat VM keeps sole ownership
/// of asking and of the warm helper - this VM only ever tells it WHICH thread renders.</summary>
public sealed partial class AssistantChatThreadsViewModel : ObservableObject
{
    private readonly AssistantChatStore _store;
    private readonly IUiErrorReporter _reporter;
    private readonly Action<Action> _dispatch;
    private readonly TimeProvider _time;

    public AssistantChatViewModel Chat { get; }
    public ObservableCollection<ThreadListItem> Threads { get; } = [];
    [ObservableProperty] private ThreadListItem? _selectedThread;
    [ObservableProperty] private bool _showArchived;
    [ObservableProperty] private bool _hasRecap;
    [ObservableProperty] private bool _hasAnyHistory;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";

    public IAsyncRelayCommand NewChatCommand { get; }
    public IRelayCommand BeginRenameCommand { get; }
    public IAsyncRelayCommand CommitRenameCommand { get; }
    public IRelayCommand CancelRenameCommand { get; }
    public IAsyncRelayCommand ArchiveCommand { get; }
    public IAsyncRelayCommand UnarchiveCommand { get; }

    public AssistantChatThreadsViewModel(AssistantChatViewModel chat, AssistantChatStore store,
        IUiErrorReporter reporter, Action<Action> dispatch, TimeProvider time)
    {
        (Chat, _store, _reporter, _dispatch, _time) = (chat, store, reporter, dispatch, time);
        NewChatCommand = new AsyncRelayCommand(NewChatAsync);
        BeginRenameCommand = new RelayCommand(() =>
        {
            if (SelectedThread is null) return;
            RenameText = SelectedThread.Name;
            IsRenaming = true;
        }, () => SelectedThread is not null);
        CommitRenameCommand = new AsyncRelayCommand(CommitRenameAsync);
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        ArchiveCommand = new AsyncRelayCommand(
            () => SetArchivedAsync(archived: true), () => SelectedThread is { Archived: false });
        UnarchiveCommand = new AsyncRelayCommand(
            () => SetArchivedAsync(archived: false), () => SelectedThread is { Archived: true });
        // A finished turn may have minted "Chat 1" on an empty store or folded turns into a recap
        // mid-ask (condense) - refresh so the selector and indicator reflect on-disk truth.
        Chat.TurnCompleted += turn => _ = LoadAsync(CancellationToken.None);
    }

    partial void OnSelectedThreadChanged(ThreadListItem? value)
    {
        Chat.IsReadOnly = value?.Archived ?? false;
        HasRecap = value?.HasRecap ?? false;
        IsRenaming = false;
        BeginRenameCommand.NotifyCanExecuteChanged();
        ArchiveCommand.NotifyCanExecuteChanged();
        UnarchiveCommand.NotifyCanExecuteChanged();
        if (value is not null) _ = Chat.SelectThreadAsync(value.Id, CancellationToken.None);
    }

    partial void OnShowArchivedChanged(bool value) => _ = LoadAsync(CancellationToken.None);

    /// <summary>Build the selector from the store, keeping the current selection by id when it
    /// still qualifies; else the first non-archived thread; else none (empty scope - the next ask
    /// mints "Chat 1" service-side and the TurnCompleted refresh adopts it).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            var log = await Task.Run(() => _store.LoadAsync(ct), ct);
            var items = log.Chats
                .Where(c => ShowArchived || !c.Archived)
                .Select(c => new ThreadListItem(c.Id, c.Name, c.Archived, c.Recap is not null))
                .ToList();
            bool anyHistory = log.Chats.Any(c => c.Turns.Count > 0);
            _dispatch(() =>
            {
                string? keep = SelectedThread?.Id;
                Threads.Clear();
                foreach (var i in items) Threads.Add(i);
                HasAnyHistory = anyHistory;
                SelectedThread = items.FirstOrDefault(i => i.Id == keep)
                    ?? items.FirstOrDefault(i => !i.Archived);
            });
        }
        catch (Exception ex) { _reporter.Report("Load assistant chat threads", ex); }
    }

    private async Task NewChatAsync()
    {
        try
        {
            var log = await _store.LoadAsync(CancellationToken.None);
            int max = 0;
            foreach (var c in log.Chats)
                if (c.Name.StartsWith("Chat ", StringComparison.Ordinal)
                    && int.TryParse(c.Name.AsSpan(5), out int n) && n > max) max = n;
            var thread = AssistantChatStore.NewThread("Chat " + (max + 1), _time.GetUtcNow());
            await _store.SaveAsync(log with { Chats = [.. log.Chats, thread] }, CancellationToken.None);
            await LoadAsync(CancellationToken.None);
            _dispatch(() => SelectedThread = Threads.FirstOrDefault(t => t.Id == thread.Id));
        }
        catch (Exception ex) { _reporter.Report("New chat thread", ex); }
    }

    private async Task CommitRenameAsync()
    {
        string name = RenameText.Trim();
        if (SelectedThread is not { } sel || name.Length == 0) { IsRenaming = false; return; }
        await MutateAsync(sel.Id, t => t with { Name = name }, "Rename chat thread");
        IsRenaming = false;
    }

    private async Task SetArchivedAsync(bool archived)
    {
        if (SelectedThread is not { } sel) return;
        await MutateAsync(sel.Id, t => t with { Archived = archived },
            archived ? "Archive chat thread" : "Unarchive chat thread");
    }

    /// <summary>Load-modify-save one thread's metadata; turns are never touched here (they stay
    /// append-only within a thread - the store's own v2 rule).</summary>
    private async Task MutateAsync(string id, Func<AssistantChatThread, AssistantChatThread> mutate,
        string activity)
    {
        try
        {
            var log = await _store.LoadAsync(CancellationToken.None);
            var target = log.Chats.FirstOrDefault(c => c.Id == id);
            if (target is null) return;
            var chats = log.Chats.ToList();
            chats[chats.IndexOf(target)] = mutate(target);
            await _store.SaveAsync(log with { Chats = chats }, CancellationToken.None);
            await LoadAsync(CancellationToken.None);
        }
        catch (Exception ex) { _reporter.Report(activity, ex); }
    }
}
