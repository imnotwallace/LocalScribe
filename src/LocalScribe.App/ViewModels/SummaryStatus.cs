namespace LocalScribe.App.ViewModels;

/// <summary>A session's summary standing for the overview columns (design Phase 3/4): None =
/// never generated, Done = latest version current, Stale = latest version predates a transcript
/// change. Presentation-only - the truth lives in SummaryStore (latest version + Stale flag).</summary>
public enum SummaryStatus { None, Done, Stale }

/// <summary>Reads one session's summary standing. A seam (not a service class) so page VMs stay
/// WPF-free and tests can stub it without a store; the App composition binds it to the single
/// composed SummaryStore instance (comp.Summaries - never a second store, house rule).</summary>
public delegate Task<SummaryStatus> SummaryStatusProvider(string sessionId, CancellationToken ct);
