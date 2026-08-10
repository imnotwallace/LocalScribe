using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalScribe.App.Services;

/// <summary>Download progress: bytes so far out of the pinned total.</summary>
public sealed record ComponentFetchProgress(long Bytes, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)Bytes / TotalBytes, 0, 1) : 0;
}

/// <summary>Drives one component download over the out-of-process fetch helper (Tier 1 plan D,
/// T1-10, 2026-08-05) and turns its JSONL stdout into progress reports or an exception.
///
/// This is the SherpaHelperDiariser half of the pattern: all of the contract, none of the
/// process. It FAILS CLOSED - a missing result line, a non-zero exit or an error line all throw,
/// so the panel can never mark a component installed on the strength of a helper that merely
/// stopped talking.</summary>
public sealed class ComponentFetchClient(IComponentFetchHelper helper)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task FetchAsync(ComponentPin pin, string destPath,
        IProgress<ComponentFetchProgress> progress, CancellationToken ct)
    {
        string job = JsonSerializer.Serialize(new
        {
            url = pin.Url, destPath, sha256 = pin.Sha256, expectedBytes = pin.Bytes,
        }, Json);

        bool sawResult = false;
        string? error = null;
        int exit = await helper.RunAsync(job, line =>
        {
            try
            {
                var root = JsonDocument.Parse(line).RootElement;
                switch (root.GetProperty("type").GetString())
                {
                    case "progress":
                        progress.Report(new ComponentFetchProgress(
                            root.GetProperty("bytes").GetInt64(),
                            root.GetProperty("totalBytes").GetInt64()));
                        break;
                    case "result": sawResult = true; break;
                    case "error": error = root.GetProperty("message").GetString(); break;
                }
            }
            catch (Exception)
            {
                // The child writes only JSON, but a native runtime warning could still land on
                // stdout. Losing a multi-gigabyte download to one stray line would be absurd.
            }
        }, ct);

        if (error is not null) throw new InvalidOperationException(error);
        if (exit != 0)
            throw new InvalidOperationException(
                "The download helper exited with code " + exit + " without reporting a reason.");
        if (!sawResult)
            throw new InvalidOperationException(
                "The download helper finished without confirming the file was written.");
    }
}
