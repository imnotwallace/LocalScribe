using LocalScribe.Core.Assistant;

namespace LocalScribe.Core.Tests;

public class AssistantGateTests
{
    [Fact]
    public void TryEnter_fails_while_recording_and_succeeds_when_idle()
    {
        string? busy = "Waiting for the recording to finish...";
        var gate = new AssistantGate(() => busy);
        Assert.False(gate.TryEnter(out _));
        Assert.Equal(busy, gate.BusyReason);

        busy = null;
        Assert.True(gate.TryEnter(out var lease));
        lease.Dispose();
    }

    [Fact]
    public void One_assistant_job_at_a_time()
    {
        var gate = new AssistantGate(() => null);
        Assert.True(gate.TryEnter(out var first));
        Assert.False(gate.TryEnter(out _));      // second concurrent job refused
        first.Dispose();
        Assert.True(gate.TryEnter(out var second));
        second.Dispose();
    }

    [Fact]
    public async Task EnterAsync_queues_visibly_until_recording_finishes()
    {
        // Design 7.1/7.7: job requested mid-recording -> visibly queued, runs when idle.
        string? busy = "Waiting for the recording to finish...";
        var waits = new List<string>();
        var gate = new AssistantGate(() => busy, pollMs: 10);

        var entering = gate.EnterAsync(waits.Add, CancellationToken.None);
        await Task.Delay(80);
        Assert.False(entering.IsCompleted);      // still queued while "recording"
        Assert.NotEmpty(waits);                  // and VISIBLY so

        busy = null;                             // recording stops
        using var lease = await entering.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Waiting for the recording to finish...", waits[0]);
    }

    [Fact]
    public async Task EnterAsync_cancellation_releases_cleanly()
    {
        var gate = new AssistantGate(() => "busy forever", pollMs: 10);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.EnterAsync(null, cts.Token));
        Assert.False(gate.TryEnter(out _));      // recording still "busy" - refused for THAT reason
        var idle = new AssistantGate(() => null);
        Assert.True(idle.TryEnter(out var lease));   // and a fresh idle gate is enterable
        lease.Dispose();
    }
}
