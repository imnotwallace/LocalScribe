using System.Text.Json;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Append-only JSONL writer/reader for transcript.jsonl (spec section 1.1). One compact
/// JSON object per physical line, in write order; never rewritten (evidentiary invariant).
/// Tolerates a torn tail from a crash mid-append: reads skip+count malformed lines (the bytes
/// stay on disk), and appends self-heal line termination (spec section 1.1 torn-tail durability).</summary>
public sealed class TranscriptStore
{
    // Compact clone of the shared options: same converters/naming, but single-line output.
    private static readonly JsonSerializerOptions Compact = new(LocalScribeJson.Options) { WriteIndented = false };

    private readonly string _path;
    public TranscriptStore(string jsonlPath) => _path = jsonlPath;

    public async Task AppendAsync(TranscriptLine line, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string json = JsonSerializer.Serialize(line, Compact);
        string prefix = NeedsNewlinePrefix() ? "\n" : "";
        await File.AppendAllTextAsync(_path, prefix + json + "\n", ct);
    }

    public async Task<TranscriptReadResult> ReadAllDetailedAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new TranscriptReadResult(Array.Empty<TranscriptLine>(), 0);
        var lines = new List<TranscriptLine>();
        int malformed = 0;
        foreach (string raw in await File.ReadAllLinesAsync(_path, ct))
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var line = JsonSerializer.Deserialize<TranscriptLine>(raw, Compact);
                if (line is not null) lines.Add(line); else malformed++;
            }
            catch (JsonException) { malformed++; }   // torn tail: skip, never rewrite
        }
        return new TranscriptReadResult(lines, malformed);
    }

    public async Task<IReadOnlyList<TranscriptLine>> ReadAllAsync(CancellationToken ct)
        => (await ReadAllDetailedAsync(ct)).Lines;

    public async Task<int> NextSeqAsync(CancellationToken ct)
    {
        var all = await ReadAllAsync(ct);
        return all.Count == 0 ? 0 : all.Max(l => l.Seq) + 1;
    }

    // True when the file ends without '\n' - a crash tore the previous append (spec section 1.1).
    private bool NeedsNewlinePrefix()
    {
        if (!File.Exists(_path)) return false;
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length == 0) return false;
        fs.Seek(-1, SeekOrigin.End);
        return fs.ReadByte() != '\n';
    }
}

/// <summary>Result of a tolerant JSONL read: parseable lines in write order + how many lines
/// failed to parse (a crash's torn tail). A non-zero count is diagnostic, not fatal.</summary>
public sealed record TranscriptReadResult(IReadOnlyList<TranscriptLine> Lines, int MalformedLineCount);
