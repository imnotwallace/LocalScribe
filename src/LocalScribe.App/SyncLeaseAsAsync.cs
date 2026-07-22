namespace LocalScribe.App;

/// <summary>Matter-QA round: AssistantGate.EnterAsync returns a synchronous IDisposable lease
/// (design 7.1's engine gate), but AssistantQaService's acquireEngineLease seam wants
/// Task&lt;IAsyncDisposable&gt; (App.xaml.cs' openSessionDetails factory). This adapts the sync
/// lease without changing AssistantGate's contract (kept in its own file, per the Task 9 brief,
/// so App.xaml.cs does not grow past its line-count guidance for a one-off adapter type).</summary>
internal sealed class SyncLeaseAsAsync(IDisposable inner) : IAsyncDisposable
{
    public ValueTask DisposeAsync() { inner.Dispose(); return ValueTask.CompletedTask; }
}
