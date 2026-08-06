namespace LocalScribe.App.Services;

/// <summary>The process boundary for component downloads (Tier 1 plan D, T1-10, 2026-08-05),
/// shaped exactly like IDiarisationHelper: the caller hands over one serialized job and receives
/// the child's stdout line by line. The seam exists so ComponentFetchClient's parsing is testable
/// against a scripted fake while the real child stays a humble, untested process object.</summary>
public interface IComponentFetchHelper
{
    Task<int> RunAsync(string jobJson, Action<string> onStdoutLine, CancellationToken ct);
}
