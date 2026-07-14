using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using NAudio.Wave;
namespace LocalScribe.Core.Import;

/// <summary>Production IAudioDecoder (design 2026-07-13 section 4.2). WAV is probed/decoded
/// natively (NAudio, in place, read-only); everything else goes through ffprobe (JSON claims) and
/// an ffmpeg subprocess decoding to pcm_s16le WAV at the stream's native rate/channels - one
/// deterministic decode path across machines (MF codec availability varies by Windows SKU).
/// Subprocess handling mirrors ProcessDiarisationHelper: kill the entire process TREE on
/// cancel/timeout; stderr is captured and surfaced in the failure message for diagnostics.</summary>
public sealed class FfmpegAudioDecoder : IAudioDecoder
{
    private readonly string? _toolsDir;
    private readonly TimeSpan _timeout;

    public FfmpegAudioDecoder(string? toolsDir, TimeSpan? timeout = null)
        => (_toolsDir, _timeout) = (toolsDir, timeout ?? TimeSpan.FromMinutes(15));

    public async Task<AudioProbeResult> ProbeAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Audio file not found.", path);
        if (IsWav(path)) return ProbeWav(path, info);
        string json = await RunToolAsync("ffprobe.exe",
            $"-v error -print_format json -show_format -show_streams \"{path}\"", ct);
        return ParseProbeJson(json, info);
    }

    public async Task<DecodedAudio> DecodeAsync(string path, string workDir, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Audio file not found.", path);
        if (IsWav(path)) return DescribeWav(path);
        string outPath = Path.Combine(workDir, "decoded.wav");
        await RunToolAsync("ffmpeg.exe",
            $"-v error -nostdin -y -i \"{path}\" -vn -acodec pcm_s16le \"{outPath}\"", ct);
        return DescribeWav(outPath);
    }

    private static bool IsWav(string path)
        => Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase);

    // The decoded-output description: for the ffmpeg path this reads ffmpeg's OWN output header
    // (written after it decoded every sample - decoded truth); for the wav-native path it reads
    // the input in place (the data chunk IS the stream). A lying WAV header (data chunk claims
    // more bytes than the file holds) inflates DurationMs here and slips past the duration gate,
    // so ChannelMapper.WriteLegs cross-checks samples-read vs the declared length and aborts the
    // import rather than silently writing truncated legs.
    private static DecodedAudio DescribeWav(string wavPath)
    {
        using var reader = new WaveFileReader(wavPath);
        return new DecodedAudio
        {
            PcmWavPath = wavPath,
            SampleRate = reader.WaveFormat.SampleRate,
            Channels = reader.WaveFormat.Channels,
            DurationMs = (long)reader.TotalTime.TotalMilliseconds,
        };
    }

    private static AudioProbeResult ProbeWav(string path, FileInfo info)
    {
        using var reader = new WaveFileReader(path);
        return new AudioProbeResult
        {
            FormatName = "wav",
            FileSizeBytes = info.Length,
            ClaimedDurationMs = (long)reader.TotalTime.TotalMilliseconds,
            ClaimedChannels = reader.WaveFormat.Channels,
            ClaimedSampleRate = reader.WaveFormat.SampleRate,
            MediaCreatedUtc = null,
            FileCreatedUtc = info.CreationTimeUtc,
            FileModifiedUtc = info.LastWriteTimeUtc,
        };
    }

    private static AudioProbeResult ParseProbeJson(string json, FileInfo info)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string formatName = "";
        long? durationMs = null;
        DateTimeOffset? mediaCreated = null;
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("format_name", out var fn)) formatName = fn.GetString() ?? "";
            if (format.TryGetProperty("duration", out var d) && double.TryParse(d.GetString(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out double sec))
                durationMs = (long)(sec * 1000);
            if (format.TryGetProperty("tags", out var tags)
                && tags.TryGetProperty("creation_time", out var created)
                && DateTimeOffset.TryParse(created.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
                mediaCreated = when;
        }
        int? channels = null, sampleRate = null;
        if (root.TryGetProperty("streams", out var streams))
            foreach (var s in streams.EnumerateArray())
            {
                if (!s.TryGetProperty("codec_type", out var t) || t.GetString() != "audio") continue;
                if (s.TryGetProperty("channels", out var ch)) channels = ch.GetInt32();
                if (s.TryGetProperty("sample_rate", out var sr)
                    && int.TryParse(sr.GetString(), out int srv)) sampleRate = srv;
                if (durationMs is null && s.TryGetProperty("duration", out var sd)
                    && double.TryParse(sd.GetString(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double ssec))
                    durationMs = (long)(ssec * 1000);
                break;                                             // first audio stream only
            }
        return new AudioProbeResult
        {
            FormatName = formatName, FileSizeBytes = info.Length,
            ClaimedDurationMs = durationMs, ClaimedChannels = channels,
            ClaimedSampleRate = sampleRate, MediaCreatedUtc = mediaCreated,
            FileCreatedUtc = info.CreationTimeUtc, FileModifiedUtc = info.LastWriteTimeUtc,
        };
    }

    private async Task<string> RunToolAsync(string exeName, string args, CancellationToken ct)
    {
        string? exe = _toolsDir is null ? null : Path.Combine(_toolsDir, exeName);
        if (exe is null || !File.Exists(exe))
            throw new InvalidOperationException($"FFmpeg not found ({exeName}). " + FfmpegLocator.MissingMessage);
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exeName}");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        await using var reg = timeoutCts.Token.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* may have exited between the check and the kill */ }
        });
        // Drain BOTH pipes concurrently - a full stderr pipe would deadlock the child otherwise.
        var stdout = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderr = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await ObserveAsync(stdout, stderr);   // B3-1: don't leak the in-flight pipe reads
            throw new TimeoutException($"{exeName} timed out after {_timeout} (killed).");
        }
        catch
        {
            await ObserveAsync(stdout, stderr);   // user cancel or other fault: observe before unwinding
            throw;
        }
        string err;
        try { err = await stderr; } catch { err = ""; }
        if (proc.ExitCode != 0)
        {
            await ObserveAsync(stdout);           // observe stdout too before failing on exit code
            throw new InvalidDataException(
                $"{exeName} exited with code {proc.ExitCode}: {(err.Length > 2000 ? err[^2000..] : err)}");
        }
        return await stdout;
    }

    /// <summary>Await the in-flight stdout/stderr reads purely to observe them (discarding results
    /// and faults) so an early throw on the timeout/cancel/non-zero-exit paths cannot leave an
    /// unobserved Task that surfaces later as a TaskScheduler.UnobservedTaskException (B3-1).</summary>
    private static async Task ObserveAsync(params Task[] tasks)
    {
        foreach (var t in tasks)
            try { await t; } catch { /* the caller is already throwing; this read is just drained */ }
    }
}
