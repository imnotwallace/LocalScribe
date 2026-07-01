// src/LocalScribe.Core/Storage/EditStore.cs
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Storage;

/// <summary>Correction-only, non-destructive edit facade (spec section 1.6/section 10). Text corrections go to
/// edits.json; per-segment speaker reassignments go to speakers.json (pinned). Allowed only on
/// finalized/recovered sessions, and only against an existing JSONL segment.</summary>
public sealed class EditStore
{
    public const int Version = 1;
    private readonly string _dir;
    private readonly TimeProvider _time;
    public EditStore(string sessionDir, TimeProvider time) => (_dir, _time) = (sessionDir, time);

    private string EditsPath => Path.Combine(_dir, "edits.json");
    private string SpeakersPath => Path.Combine(_dir, "speakers.json");
    private string SessionPath => Path.Combine(_dir, "session.json");
    private string MetaPath => Path.Combine(_dir, "meta.json");
    private string JsonlPath => Path.Combine(_dir, "transcript.jsonl");

    public async Task ApplyTextCorrectionAsync(int seq, string correctedText, CancellationToken ct)
    {
        await EnsureFinalizedAsync(ct);
        await EnsureSegmentAsync(seq, expectedSource: null, ct);
        var edits = await LoadAsync(ct) ?? new Edits();
        var corrections = new Dictionary<string, Correction>(edits.Corrections)
        {
            [seq.ToString()] = new Correction { Text = correctedText, EditedAtUtc = _time.GetUtcNow() },
        };
        await JsonFile.WriteAsync(EditsPath, edits with { SchemaVersion = Version, Corrections = corrections }, ct);
        await MarkEditedAsync(ct);
    }

    public async Task ReassignSpeakerAsync(int seq, TranscriptSource source, string clusterKey, CancellationToken ct)
    {
        await EnsureFinalizedAsync(ct);
        await EnsureSegmentAsync(seq, expectedSource: source, ct);
        var store = new SpeakersStore(SpeakersPath);
        var speakers = await store.LoadAsync(ct) ?? new Speakers();
        string key = source.ToString();

        var assignments = speakers.Assignments.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        if (!assignments.TryGetValue(key, out var bySeq)) assignments[key] = bySeq = new();
        bySeq[seq.ToString()] = clusterKey;

        var pinned = speakers.Pinned.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value));
        if (!pinned.TryGetValue(key, out var pins)) pinned[key] = pins = new();
        if (!pins.Contains(seq.ToString())) pins.Add(seq.ToString());

        await store.SaveAsync(speakers with { Assignments = assignments, Pinned = pinned }, ct);
        await MarkEditedAsync(ct);
    }

    public async Task<Edits?> LoadAsync(CancellationToken ct)
    {
        var obj = await SchemaGuard.ReadObjectAsync(EditsPath, ct);
        if (obj is null) return null;
        SchemaGuard.RejectIfNewer(SchemaGuard.ReadVersion(obj), Version, "edits.json");
        return await JsonFile.ReadAsync<Edits>(EditsPath, ct);
    }

    private async Task EnsureFinalizedAsync(CancellationToken ct)
    {
        var session = await new SessionStore(SessionPath).ReadAsync(ct);
        if (session is null || session.EndedAtUtc is null)
            throw new InvalidOperationException("Editing is allowed only on finalized or recovered sessions (spec section 1.6).");
    }

    private async Task EnsureSegmentAsync(int seq, TranscriptSource? expectedSource, CancellationToken ct)
    {
        var lines = await new TranscriptStore(JsonlPath).ReadAllAsync(ct);
        var line = lines.FirstOrDefault(l => l.Seq == seq)
            ?? throw new ArgumentException($"No transcript line with seq {seq}.", nameof(seq));
        if (line.Kind != TranscriptKind.Segment)
            throw new ArgumentException($"seq {seq} is a system marker; only segments are correctable (spec section 1.6).", nameof(seq));
        if (expectedSource is { } src && line.Source != src)
            throw new ArgumentException($"seq {seq} belongs to the {line.Source} stream, not {src} (spec section 1.3).", nameof(seq));
    }

    private async Task MarkEditedAsync(CancellationToken ct)
    {
        var metaStore = new MetadataStore(MetaPath);
        var meta = await metaStore.LoadAsync(ct);
        if (meta is null) return;
        await metaStore.SaveAsync(meta with { Edited = true, LastEditedAtUtc = _time.GetUtcNow() }, ct);
    }
}
