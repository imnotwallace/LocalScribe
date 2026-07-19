namespace LocalScribe.Core.Assistant;

/// <summary>Locked rule (design 2026-07-18 section 7.1): assistant jobs are BLOCKED while a
/// recording session is active - queued with a VISIBLE waiting state - and only one assistant
/// job runs at a time. One-directional by design: recording is never gated by the assistant
/// (this deliberately does NOT chain into SessionController.ExternalEngineBusy, which blocks
/// recording start). recordingBusy is the same condition CompositionRoot gives
/// RetranscriptionRunner: non-null reason while State != Idle or a finalize is pending.</summary>
public sealed class AssistantGate(Func<string?> recordingBusy, int pollMs = 1000)
{
    private readonly SemaphoreSlim _jobLease = new(1, 1);

    /// <summary>The current block reason, or null when assistant jobs may run.</summary>
    public string? BusyReason => recordingBusy();

    /// <summary>Immediate entry: false while recording is busy OR another assistant job
    /// holds the lease. Dispose the lease to release.</summary>
    public bool TryEnter(out IDisposable lease)
    {
        lease = NullLease.Instance;
        if (recordingBusy() is not null) return false;
        if (!_jobLease.Wait(0)) return false;
        if (recordingBusy() is not null) { _jobLease.Release(); return false; }   // raced a Start
        lease = new Lease(_jobLease);
        return true;
    }

    /// <summary>Queued entry (design 7.7: "job requested mid-recording -> visibly queued"):
    /// reports the busy reason via onWaiting, then polls until recording is idle and the
    /// single-job lease frees. Cancellation releases cleanly.</summary>
    public async Task<IDisposable> EnterAsync(Action<string>? onWaiting, CancellationToken ct)
    {
        bool reported = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (TryEnter(out var lease)) return lease;
            if (!reported && BusyReason is string reason) { onWaiting?.Invoke(reason); reported = true; }
            await Task.Delay(pollMs, ct);
        }
    }

    private sealed class Lease(SemaphoreSlim sem) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) sem.Release();
        }
    }

    private sealed class NullLease : IDisposable
    {
        public static readonly NullLease Instance = new();
        public void Dispose() { }
    }
}
