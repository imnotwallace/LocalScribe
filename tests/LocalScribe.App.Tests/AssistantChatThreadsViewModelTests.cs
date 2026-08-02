using System.IO;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Assistant;
using Xunit;

public class AssistantChatThreadsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    // Best-effort (established idiom across this test project, e.g. AssistantTabViewModelTests):
    // this VM's fire-and-forget reloads (OnSelectedThreadChanged -> Chat.SelectThreadAsync) can
    // still be reading chats.json on a background thread when a test method returns - swallow
    // rather than fail cleanup on that harmless race.
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class FakeReporter : IUiErrorReporter
    {
        public List<(string Context, Exception Ex)> Errors { get; } = new();
        public List<string> Infos { get; } = new();
        public void Report(string context, Exception ex) => Errors.Add((context, ex));
        public void Info(string message) => Infos.Add(message);
    }

    // Minimal turn builder - only Question matters for these assertions, mirroring
    // AssistantChatViewModelTests' Turn() helper (same field shape, filler values elsewhere).
    private static AssistantChatTurn Turn(string question) => new(Guid.NewGuid().ToString("N"),
        new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.Zero), question, "a", [],
        "m.gguf", "cpu", "3", false, null, ["s1"], [], [], 0);

    // Both the VM's own reload path (property-changed handlers fire genuinely-async LoadAsync/
    // SelectThreadAsync calls fire-and-forget) and a test's own explicit awaited call can be
    // in flight over the SAME ObservableCollection at once. A real WPF Dispatcher fully
    // serializes callbacks on one UI thread; a bare "a => a()" does not, so two dispatched
    // closures can genuinely interleave their Clear()/Add() calls on the non-thread-safe
    // ObservableCollection<T> and corrupt it. Locking the dispatch reproduces the Dispatcher's
    // serialization (each callback runs to completion before the next starts) without sleeps -
    // both paths write the SAME final data for a given store state, so serialized-but-unordered
    // execution still converges on the correct end state. Gate is also exposed so a test that
    // still has a background fire-and-forget in flight (e.g. after an explicit redundant call)
    // can read the collection under the same lock rather than racing the enumerator against it.
    private (AssistantChatThreadsViewModel Threads, AssistantChatViewModel Chat,
        AssistantChatStore Store, FakeReporter Reporter, object Gate) Make()
    {
        var store = new AssistantChatStore(Path.Combine(_root, "assistant", "chats.json"));
        var reporter = new FakeReporter();
        var gate = new object();
        Action<Action> dispatch = a => { lock (gate) a(); };
        var chat = new AssistantChatViewModel(() => null, store, reporter, dispatch);
        var vm = new AssistantChatThreadsViewModel(chat, store, reporter, dispatch, TimeProvider.System);
        return (vm, chat, store, reporter, gate);
    }

    [Fact]
    public async Task LoadAsync_lists_non_archived_and_selects_first()
    {
        var (vm, chat, store, _, gate) = Make();
        var a = AssistantChatStore.NewThread("A", DateTimeOffset.UtcNow) with { Turns = [Turn("qA")] };
        var b = AssistantChatStore.NewThread("B", DateTimeOffset.UtcNow) with { Turns = [Turn("qB")] };
        var archived = AssistantChatStore.NewThread("C", DateTimeOffset.UtcNow) with { Archived = true };
        await store.SaveAsync(new AssistantChatLog { Chats = [a, b, archived] }, CancellationToken.None);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Equal(2, vm.Threads.Count);
        Assert.Equal(a.Id, vm.SelectedThread?.Id);

        // LoadAsync's own selection already fires a background Chat.SelectThreadAsync
        // fire-and-forget for thread A; this explicit awaited call is the deterministic one the
        // assert relies on (task note). Both write the SAME final data, but the enumeration
        // below is done under the shared dispatch lock so a still-in-flight background write
        // can never be observed mid-mutation.
        await chat.SelectThreadAsync(a.Id, CancellationToken.None);
        lock (gate)
        {
            Assert.Single(chat.Turns);
            Assert.Equal("qA", chat.Turns[0].Question);
        }
    }

    [Fact]
    public async Task Selecting_a_thread_automatically_swaps_rendered_turns()
    {
        // Regression coverage for OnSelectedThreadChanged's brief-mandated automatic wiring
        // (`_ = Chat.SelectThreadAsync(value.Id, ...)`). Unlike the other tests in this file,
        // this one makes NO explicit Chat.SelectThreadAsync call anywhere - if that automatic
        // line were ever deleted, this test (and only this test) would fail.
        var (vm, chat, store, _, gate) = Make();
        var a = AssistantChatStore.NewThread("A", DateTimeOffset.UtcNow) with { Turns = [Turn("qA")] };
        var b = AssistantChatStore.NewThread("B", DateTimeOffset.UtcNow) with { Turns = [Turn("qB")] };
        await store.SaveAsync(new AssistantChatLog { Chats = [a, b] }, CancellationToken.None);

        await vm.LoadAsync(CancellationToken.None);
        Assert.Equal(a.Id, vm.SelectedThread?.Id);

        // LoadAsync's own selection of A already kicked off a fire-and-forget
        // Chat.SelectThreadAsync(a.Id) via the same automatic wiring under test; ride that out
        // to a known state (chat showing A's single turn) before switching to B, so the
        // assertion below can only be satisfied by the SUBSEQUENT automatic call for B.
        var toA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnA(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs __)
        {
            lock (gate)
            {
                if (chat.Turns.Count == 1 && chat.Turns[0].Question == "qA") toA.TrySetResult();
            }
        }
        chat.Turns.CollectionChanged += OnA;
        await toA.Task.WaitAsync(TimeSpan.FromSeconds(5));
        chat.Turns.CollectionChanged -= OnA;

        var toB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnB(object? _, System.Collections.Specialized.NotifyCollectionChangedEventArgs __)
        {
            lock (gate)
            {
                if (chat.Turns.Count == 1 && chat.Turns[0].Question == "qB") toB.TrySetResult();
            }
        }
        chat.Turns.CollectionChanged += OnB;

        // The AUTOMATIC path under test: assigning SelectedThread (as the thread dropdown does)
        // triggers OnSelectedThreadChanged's fire-and-forget Chat.SelectThreadAsync - no explicit
        // call here.
        vm.SelectedThread = vm.Threads.Single(t => t.Id == b.Id);

        await toB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        chat.Turns.CollectionChanged -= OnB;

        lock (gate)
        {
            Assert.Single(chat.Turns);
            Assert.Equal("qB", chat.Turns[0].Question);
        }
    }

    [Fact]
    public async Task ShowArchived_reveals_archived_with_suffix()
    {
        var (vm, _, store, _, gate) = Make();
        var a = AssistantChatStore.NewThread("A", DateTimeOffset.UtcNow);
        var archived = AssistantChatStore.NewThread("Old", DateTimeOffset.UtcNow) with { Archived = true };
        await store.SaveAsync(new AssistantChatLog { Chats = [a, archived] }, CancellationToken.None);

        await vm.LoadAsync(CancellationToken.None);
        Assert.Single(vm.Threads);

        vm.ShowArchived = true;
        await vm.LoadAsync(CancellationToken.None);   // deterministic reload, not the property's fire-and-forget one

        lock (gate)
        {
            Assert.Equal(2, vm.Threads.Count);
            var item = vm.Threads.Single(t => t.Id == archived.Id);
            Assert.EndsWith(" (archived)", item.Display);
        }
    }

    [Fact]
    public async Task Selecting_archived_sets_chat_readonly()
    {
        var (vm, chat, store, _, _) = Make();
        var live = AssistantChatStore.NewThread("Live", DateTimeOffset.UtcNow);
        var archived = AssistantChatStore.NewThread("Old", DateTimeOffset.UtcNow) with { Archived = true };
        await store.SaveAsync(new AssistantChatLog { Chats = [live, archived] }, CancellationToken.None);

        // Assign ThreadListItem instances directly rather than round-tripping through
        // ShowArchived+LoadAsync's Threads population - this test only cares about the
        // selection -> IsReadOnly wiring (VM's own state), not the selector's contents (covered
        // by ShowArchived_reveals_archived_with_suffix), so it sidesteps that reload entirely.
        vm.SelectedThread = new ThreadListItem(archived.Id, archived.Name, Archived: true, HasRecap: false);
        Assert.True(chat.IsReadOnly);

        vm.SelectedThread = new ThreadListItem(live.Id, live.Name, Archived: false, HasRecap: false);
        Assert.False(chat.IsReadOnly);
    }

    [Fact]
    public async Task NewChat_appends_and_selects()
    {
        var (vm, chat, store, reporter, gate) = Make();
        var t1 = AssistantChatStore.NewThread("Chat 1", DateTimeOffset.UtcNow);
        var t3 = AssistantChatStore.NewThread("Chat 3", DateTimeOffset.UtcNow);
        await store.SaveAsync(new AssistantChatLog { Chats = [t1, t3] }, CancellationToken.None);
        await vm.LoadAsync(CancellationToken.None);

        await vm.NewChatCommand.ExecuteAsync(null);

        Assert.Empty(reporter.Errors);
        var log = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(3, log.Chats.Count);
        Assert.Contains(log.Chats, c => c.Name == "Chat 4");
        Assert.Equal("Chat 4", vm.SelectedThread?.Name);

        await chat.SelectThreadAsync(vm.SelectedThread!.Id, CancellationToken.None);
        lock (gate) { Assert.Empty(chat.Turns); }
    }

    [Fact]
    public async Task Rename_persists()
    {
        var (vm, _, store, reporter, _) = Make();
        var t = AssistantChatStore.NewThread("Old", DateTimeOffset.UtcNow);
        await store.SaveAsync(new AssistantChatLog { Chats = [t] }, CancellationToken.None);
        await vm.LoadAsync(CancellationToken.None);
        string originalId = vm.SelectedThread!.Id;

        vm.BeginRenameCommand.Execute(null);
        vm.RenameText = "Strategy";
        await vm.CommitRenameCommand.ExecuteAsync(null);

        Assert.Empty(reporter.Errors);
        var log = await store.LoadAsync(CancellationToken.None);
        Assert.Equal("Strategy", log.Chats.Single().Name);
        Assert.Equal("Strategy", vm.Threads.Single().Name);
        Assert.Equal(originalId, vm.SelectedThread?.Id);
        Assert.False(vm.IsRenaming);
    }

    [Fact]
    public async Task Archive_hides_and_selects_next()
    {
        var (vm, _, store, reporter, _) = Make();
        var a = AssistantChatStore.NewThread("A", DateTimeOffset.UtcNow);
        var b = AssistantChatStore.NewThread("B", DateTimeOffset.UtcNow);
        await store.SaveAsync(new AssistantChatLog { Chats = [a, b] }, CancellationToken.None);
        await vm.LoadAsync(CancellationToken.None);
        Assert.Equal(a.Id, vm.SelectedThread?.Id);

        await vm.ArchiveCommand.ExecuteAsync(null);

        Assert.Empty(reporter.Errors);
        var log = await store.LoadAsync(CancellationToken.None);
        Assert.True(log.Chats.Single(c => c.Id == a.Id).Archived);
        Assert.Single(vm.Threads);   // ShowArchived is false - the archived one drops out
        Assert.Equal(b.Id, vm.SelectedThread?.Id);
    }

    [Fact]
    public async Task Empty_store_selects_the_no_conversations_sentinel()
    {
        // UX round 2026-08-02 item 3.5: first ever use (no chats.json) left the thread picker
        // blank until the first ask minted "Chat 1". The sentinel is seeded at construction
        // (never a blank first paint) and survives a load over an empty store.
        var (vm, _, _, reporter, _) = Make();

        Assert.NotNull(vm.SelectedThread);                       // at construction, pre-load
        Assert.Contains(vm.SelectedThread, vm.Threads);

        await vm.LoadAsync(CancellationToken.None);

        Assert.Empty(reporter.Errors);
        var only = Assert.Single(vm.Threads);
        Assert.Same(AssistantChatThreadsViewModel.NoThreadsSentinel, only);
        Assert.Same(AssistantChatThreadsViewModel.NoThreadsSentinel, vm.SelectedThread);
        Assert.Equal("(no conversations yet)", only.Display);
        Assert.False(vm.BeginRenameCommand.CanExecute(null));    // no thread to rename
        Assert.False(vm.ArchiveCommand.CanExecute(null));        // nor to archive
    }

    [Fact]
    public async Task Archive_last_thread_falls_back_to_the_sentinel()
    {
        var (vm, _, store, reporter, _) = Make();
        var only = AssistantChatStore.NewThread("Only", DateTimeOffset.UtcNow);
        await store.SaveAsync(new AssistantChatLog { Chats = [only] }, CancellationToken.None);
        await vm.LoadAsync(CancellationToken.None);

        await vm.ArchiveCommand.ExecuteAsync(null);

        Assert.Empty(reporter.Errors);
        var row = Assert.Single(vm.Threads);
        Assert.Same(AssistantChatThreadsViewModel.NoThreadsSentinel, row);
        Assert.Same(AssistantChatThreadsViewModel.NoThreadsSentinel, vm.SelectedThread);
    }

    [Fact]
    public async Task Unarchive_restores_editable()
    {
        var (vm, chat, store, reporter, _) = Make();
        var t = AssistantChatStore.NewThread("Old", DateTimeOffset.UtcNow) with { Archived = true };
        await store.SaveAsync(new AssistantChatLog { Chats = [t] }, CancellationToken.None);
        // Assign the ThreadListItem directly (see Selecting_archived_sets_chat_readonly) rather
        // than round-tripping through ShowArchived+LoadAsync - CanExecute only inspects
        // SelectedThread, so this exercises UnarchiveCommand deterministically.
        vm.SelectedThread = new ThreadListItem(t.Id, t.Name, Archived: true, HasRecap: false);
        Assert.True(chat.IsReadOnly);

        await vm.UnarchiveCommand.ExecuteAsync(null);

        Assert.Empty(reporter.Errors);
        var log = await store.LoadAsync(CancellationToken.None);
        Assert.False(log.Chats.Single().Archived);
        Assert.False(chat.IsReadOnly);
    }

    [Fact]
    public async Task HasRecap_follows_selection()
    {
        var (vm, _, store, _, _) = Make();
        var a = AssistantChatStore.NewThread("A", DateTimeOffset.UtcNow);
        var b = AssistantChatStore.NewThread("B", DateTimeOffset.UtcNow) with { Recap = "r" };
        await store.SaveAsync(new AssistantChatLog { Chats = [a, b] }, CancellationToken.None);
        await vm.LoadAsync(CancellationToken.None);
        Assert.Equal(a.Id, vm.SelectedThread?.Id);
        Assert.False(vm.HasRecap);

        vm.SelectedThread = vm.Threads.Single(t => t.Id == b.Id);
        Assert.True(vm.HasRecap);
    }

    [Fact]
    public async Task HasAnyHistory_counts_archived_turns()
    {
        var (vm, _, store, _, _) = Make();
        var archived = AssistantChatStore.NewThread("Old", DateTimeOffset.UtcNow)
            with { Archived = true, Turns = [Turn("q")] };
        await store.SaveAsync(new AssistantChatLog { Chats = [archived] }, CancellationToken.None);

        await vm.LoadAsync(CancellationToken.None);

        Assert.True(vm.HasAnyHistory);
        var row = Assert.Single(vm.Threads);   // ShowArchived is false, so sentinel keeps selector non-blank (item 3.5)
        Assert.Same(AssistantChatThreadsViewModel.NoThreadsSentinel, row);
    }
}
