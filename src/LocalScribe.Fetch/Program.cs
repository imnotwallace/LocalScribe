using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalScribe.Fetch;

/// <summary>One download, read as a single JSON object on stdin (Tier 1 plan D, T1-10,
/// 2026-08-05). ExpectedBytes comes from the pin manifest, so the helper knows when a resumed
/// file is already complete without asking the server.</summary>
public sealed record FetchJob(string Url, string DestPath, string Sha256, long ExpectedBytes);

public sealed record ProgressLine(string Type, long Bytes, long TotalBytes);
public sealed record ResultLine(string Type, string Path);
public sealed record ErrorLine(string Type, string Message);

/// <summary>The component downloader, out of process (Tier 1 plan D, T1-10, 2026-08-05).
///
/// It is a separate executable, not a class in the app, because a grep for the network stack over
/// LocalScribe.App and LocalScribe.Core must keep returning zero matches - that is the product's
/// privacy claim in its most checkable form, and it is worth an extra process. The app spawns
/// this on an explicit Download click and never otherwise, following the same stdio-child shape
/// as LocalScribe.Diarizer (ProcessDiarisationHelper: job on stdin, one JSON line per event on
/// stdout, whole process tree killed on cancel).
///
/// Behaviour is a deliberate port of tools/fetch-models.ps1, the repo's only existing download
/// code, so the two cannot drift: Get-RemoteFile's retry-with-backoff and RESUME (large model
/// blobs get throttled and dropped, and restarting a 2.5 GB transfer from zero on every blip is
/// not acceptable) and Assert-Sha256's FAIL-CLOSED verification, which deletes a mismatching file
/// rather than leaving it where the app's presence probe would count it as installed.</summary>
public static class Program
{
    private const int MaxAttempts = 4;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> Main()
    {
        // The parent writes one job and closes stdin, so ReadToEnd terminates - the same
        // handshake ProcessDiarisationHelper uses.
        string jobLine = await Console.In.ReadToEndAsync();
        FetchJob? job;
        try { job = JsonSerializer.Deserialize<FetchJob>(jobLine, Json); }
        catch (Exception ex) { Emit(new ErrorLine("error", "bad job: " + ex.Message)); return 2; }
        // `job.Sha256 is not { Length: 64 }` and NOT `job.Sha256.Length != 64`: FetchJob's Sha256
        // is a non-nullable string, but that annotation is a COMPILE-TIME claim only - the JSON
        // deserializer leaves it null when the property is absent from the payload. A job carrying
        // url and destPath but no sha256 would then die of an unhandled NullReferenceException
        // OUTSIDE the try below, printing a stack trace instead of the one guarantee the wire
        // contract makes for a malformed job - and that guarantee is what makes a
        // verification-free download impossible.
        if (job is null || string.IsNullOrWhiteSpace(job.Url) || string.IsNullOrWhiteSpace(job.DestPath)
            || job.Sha256 is not { Length: 64 })
        {
            Emit(new ErrorLine("error", "bad job: url, destPath and a 64-character sha256 are required"));
            return 2;
        }

        try
        {
            await DownloadAsync(job);
            Emit(new ResultLine("result", job.DestPath));
            return 0;
        }
        catch (Exception ex)
        {
            Emit(new ErrorLine("error", ex.Message));
            return 1;
        }
    }

    private static async Task DownloadAsync(FetchJob job)
    {
        string? dir = Path.GetDirectoryName(job.DestPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Timeout.InfiniteTimeSpan: the default 100 s ceiling applies to the WHOLE response
        // including the body, so a multi-gigabyte model would abort mid-stream on any connection.
        // Stall protection is the parent's job - it kills the process tree on cancel.
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        for (int attempt = 1; ; attempt++)
        {
            try { await OneAttemptAsync(http, job); break; }
            catch (Exception) when (attempt < MaxAttempts)
            {
                // Get-RemoteFile's backoff, capped at 30 s. Whatever bytes landed stay on disk and
                // the next attempt resumes from them.
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt))));
            }
        }

        // Assert-Sha256, fail closed: a corrupt or tampered blob is DELETED and the job fails,
        // never left where ComponentProbe would report it installed.
        byte[] hash;
        await using (var fs = File.OpenRead(job.DestPath)) hash = await SHA256.HashDataAsync(fs);
        string actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actual, job.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(job.DestPath);
            throw new InvalidDataException(
                "SHA256 mismatch for " + Path.GetFileName(job.DestPath) + " - file deleted");
        }
    }

    private static async Task OneAttemptAsync(HttpClient http, FetchJob job)
    {
        long have = File.Exists(job.DestPath) ? new FileInfo(job.DestPath).Length : 0;
        if (job.ExpectedBytes > 0 && have >= job.ExpectedBytes) return;   // already complete

        using var request = new HttpRequestMessage(HttpMethod.Get, job.Url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        // 416 on a resume request is the "already complete" signal, not a failure - the same
        // case Get-RemoteFile documents and discards.
        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) return;
        response.EnsureSuccessStatusCode();

        // A server that IGNORES the range header answers 200 with the whole body. Appending that
        // to the partial file would silently concatenate two copies into a file whose hash then
        // fails for a reason no one could diagnose - so only a real 206 appends.
        bool append = have > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        long total = job.ExpectedBytes > 0
            ? job.ExpectedBytes
            : (response.Content.Headers.ContentLength ?? 0) + (append ? have : 0);

        await using var body = await response.Content.ReadAsStreamAsync();
        await using var file = new FileStream(job.DestPath,
            append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);

        long written = append ? have : 0;
        long lastPercent = -1;
        byte[] buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read));
            written += read;
            // One line per whole percent, NOT per chunk: at 80 KB a chunk a 2.5 GB model would
            // emit ~32,000 stdout lines and the parent marshals every one onto the UI thread.
            long percent = total > 0 ? written * 100 / total : 0;
            if (percent != lastPercent)
            {
                lastPercent = percent;
                Emit(new ProgressLine("progress", written, total));
            }
        }
    }

    private static void Emit<T>(T line)
    {
        Console.Out.WriteLine(JsonSerializer.Serialize(line, Json));
        Console.Out.Flush();
    }
}
