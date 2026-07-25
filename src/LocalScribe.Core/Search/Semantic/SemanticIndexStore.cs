using System.Text;
using LocalScribe.Core.Search;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Search.Semantic;

/// <summary>Binary per-session sidecar IO for index\semantic\{id}.vec (design 2026-07-25).
/// Binary because ~150k x 256 float32 in JSON would triple size and slow every load; per-session
/// because incremental reindex rewrites one small file and a torn write costs one session, not
/// the corpus. Load returns null for missing/corrupt/truncated/wrong-magic/newer-version files -
/// the service silently re-embeds (SearchIndexStore's self-heal philosophy). Writes are atomic
/// (AtomicFile). Format v1: magic 'LSSV' | int version | string method | string versionId |
/// 4x long stamps | int dim | int count | count x (int startSeq | int startPartIndex |
/// long startMs | int endSeq | long endMs | string text | dim x float).</summary>
public sealed class SemanticIndexStore(StoragePaths paths)
{
    public const int Version = 1;
    private const uint Magic = 0x5653534C;   // "LSSV" little-endian

    public async Task<SemanticSidecar?> LoadAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            string path = paths.SemanticSidecarFile(sessionId);
            if (!File.Exists(path)) return null;
            byte[] bytes = await File.ReadAllBytesAsync(path, ct);
            using var r = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8);
            if (r.ReadUInt32() != Magic) return null;
            if (r.ReadInt32() > Version) return null;            // newer app wrote it
            string method = r.ReadString();
            string versionId = r.ReadString();
            var stamps = new SearchFreshnessStamps
            {
                TranscriptTicks = r.ReadInt64(), EditsTicks = r.ReadInt64(),
                SpeakersTicks = r.ReadInt64(), MetaTicks = r.ReadInt64(),
            };
            int dim = r.ReadInt32();
            int count = r.ReadInt32();
            var chunks = new List<SemanticChunk>(count);
            var vectors = new List<float[]>(count);
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                chunks.Add(new SemanticChunk(r.ReadInt32(), r.ReadInt32(), r.ReadInt64(),
                    r.ReadInt32(), r.ReadInt64(), r.ReadString()));
                float[] v = new float[dim];
                for (int d = 0; d < dim; d++) v[d] = r.ReadSingle();
                vectors.Add(v);
            }
            return new SemanticSidecar(method, versionId, stamps, dim, chunks, vectors);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }                                    // corrupt -> silent re-embed
    }

    public Task SaveAsync(string sessionId, SemanticSidecar sidecar, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(Version);
            w.Write(sidecar.Method);
            w.Write(sidecar.VersionId);
            w.Write(sidecar.Stamps.TranscriptTicks);
            w.Write(sidecar.Stamps.EditsTicks);
            w.Write(sidecar.Stamps.SpeakersTicks);
            w.Write(sidecar.Stamps.MetaTicks);
            w.Write(sidecar.Dim);
            w.Write(sidecar.Chunks.Count);
            for (int i = 0; i < sidecar.Chunks.Count; i++)
            {
                var c = sidecar.Chunks[i];
                w.Write(c.StartSeq); w.Write(c.StartPartIndex); w.Write(c.StartMs);
                w.Write(c.EndSeq); w.Write(c.EndMs); w.Write(c.Text);
                float[] v = sidecar.Vectors[i];
                for (int d = 0; d < sidecar.Dim; d++) w.Write(d < v.Length ? v[d] : 0f);
            }
        }
        return AtomicFile.WriteAllBytesAsync(paths.SemanticSidecarFile(sessionId), ms.ToArray(), ct);
    }

    public void Delete(string sessionId)
    {
        try { File.Delete(paths.SemanticSidecarFile(sessionId)); } catch { }
    }

    public IReadOnlyList<string> ListSessionIds()
        => Directory.Exists(paths.SemanticIndexDir)
            ? Directory.EnumerateFiles(paths.SemanticIndexDir, "*.vec")
                .Select(Path.GetFileNameWithoutExtension).Where(n => n is not null)
                .Select(n => n!).ToList()
            : [];
}
