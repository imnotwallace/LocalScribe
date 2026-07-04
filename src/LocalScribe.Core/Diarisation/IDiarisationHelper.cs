namespace LocalScribe.Core.Diarisation;

// Process-boundary seam. Production impl (App) spawns LocalScribe.Diarizer.exe and,
// on cancellation, kills it. Tests supply a fake that emits canned stdout lines.
public interface IDiarisationHelper
{
    Task<int> RunAsync(DiarisationJob job, Action<string> onStdoutLine, CancellationToken ct);
}
