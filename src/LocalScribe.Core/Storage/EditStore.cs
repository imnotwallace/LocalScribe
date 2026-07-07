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

    /// <summary>Batched text-correction save (Stage 6.1): apply all corrections and remove all
    /// reverted entries in ONE edits.json write and ONE meta.Edited flip. An empty/whitespace
    /// correction is rejected - a correction must correct, never blank content (spec section 1.6
    /// evidentiary invariant; content removal does not exist in this model). Reverting a seq that
    /// has no correction is a quiet no-op. Returns false - and writes nothing, flips nothing -
    /// when the whole batch was a no-op.</summary>
    public async Task<bool> ApplyTextEditsAsync(IReadOnlyDictionary<int, string> corrections,
        IReadOnlyCollection<int> reverts, CancellationToken ct)
    {
        foreach (var (seq, text) in corrections)
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException(
                    $"correction for seq {seq} is empty; transcript content is never removed (spec section 1.6).",
                    nameof(corrections));
            if (reverts.Contains(seq))
                throw new ArgumentException($"seq {seq} is both corrected and reverted.", nameof(reverts));
        }
        await EnsureFinalizedAsync(ct);
        await EnsureSegmentsAsync(corrections.Keys, expectedSource: null, ct);

        var edits = await LoadAsync(ct) ?? new Edits();
        var map = new Dictionary<string, Correction>(edits.Corrections);
        bool changed = false;
        foreach (var (seq, text) in corrections)
        {
            map[seq.ToString()] = new Correction { Text = text, EditedAtUtc = _time.GetUtcNow() };
            changed = true;
        }
        foreach (int seq in reverts)
            if (map.Remove(seq.ToString())) changed = true;
        if (!changed) return false;

        await JsonFile.WriteAsync(EditsPath, edits with { SchemaVersion = Version, Corrections = map }, ct);
        await MarkEditedAsync(ct);
        return true;
    }

    /// <summary>Batched pin (Stage 6.1): pin every seq to ONE clusterKey in a single speakers.json
    /// write + one meta.Edited flip. The key must be source-prefixed ("Remote:2" for Remote) -
    /// cluster ids are per-source (spec section 1.3). Same per-seq semantics as
    /// ReassignSpeakerAsync: assignment set + seq recorded under Pinned, so SpeakersMerge
    /// preserves it verbatim across re-diarisation.</summary>
    public async Task ReassignSpeakersAsync(IReadOnlyCollection<int> seqs, TranscriptSource source,
        string clusterKey, CancellationToken ct)
    {
        if (seqs.Count == 0) return;
        if (!clusterKey.StartsWith(source + ":", StringComparison.Ordinal))
            throw new ArgumentException(
                $"clusterKey '{clusterKey}' is not {source}-prefixed (spec section 1.3).", nameof(clusterKey));
        await EnsureFinalizedAsync(ct);
        await EnsureSegmentsAsync(seqs, expectedSource: source, ct);

        var store = new SpeakersStore(SpeakersPath);
        var speakers = await store.LoadAsync(ct) ?? new Speakers();
        string key = source.ToString();

        var assignments = speakers.Assignments.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        if (!assignments.TryGetValue(key, out var bySeq)) assignments[key] = bySeq = new();
        var pinned = speakers.Pinned.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value));
        if (!pinned.TryGetValue(key, out var pins)) pinned[key] = pins = new();
        foreach (int seq in seqs)
        {
            bySeq[seq.ToString()] = clusterKey;
            if (!pins.Contains(seq.ToString())) pins.Add(seq.ToString());
        }

        await store.SaveAsync(speakers with { Assignments = assignments, Pinned = pinned }, ct);
        await MarkEditedAsync(ct);
    }

    /// <summary>Unpin (Stage 6.1): remove the pin AND its assignment for each seq that is actually
    /// pinned, so the render falls back through the resolution tiers (a re-run of diarisation can
    /// re-cover the line). A seq that is assigned but NOT pinned is deliberately untouched -
    /// unpin must never delete diarisation output. Quiet no-op (false) when speakers.json is
    /// absent or nothing was pinned.</summary>
    public async Task<bool> RemoveSpeakerPinsAsync(IReadOnlyCollection<int> seqs, TranscriptSource source,
        CancellationToken ct)
    {
        await EnsureFinalizedAsync(ct);
        var store = new SpeakersStore(SpeakersPath);
        var speakers = await store.LoadAsync(ct);
        if (speakers is null) return false;
        string key = source.ToString();

        var assignments = speakers.Assignments.ToDictionary(kv => kv.Key, kv => new Dictionary<string, string>(kv.Value));
        var pinned = speakers.Pinned.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value));
        bool changed = false;
        foreach (int seq in seqs)
        {
            string s = seq.ToString();
            if (pinned.TryGetValue(key, out var pins) && pins.Remove(s))
            {
                changed = true;
                if (assignments.TryGetValue(key, out var bySeq)) bySeq.Remove(s);
            }
        }
        if (!changed) return false;

        await store.SaveAsync(speakers with { Assignments = assignments, Pinned = pinned }, ct);
        await MarkEditedAsync(ct);
        return true;
    }

    /// <summary>Write a non-destructive split overlay for one segment (design section 2). The machine
    /// transcript.jsonl line is untouched; the split partitions it into >= 2 human-authored parts.
    /// Any prior text correction on the seq is REMOVED (absorbed into the parts) so display text has
    /// one source of truth. Validators enforce the evidentiary invariants (non-blank children;
    /// first part inherits the machine start; later starts strictly increasing and within the
    /// segment). Flips meta.Edited.</summary>
    public async Task ApplySplitAsync(int seq, TranscriptSource source, IReadOnlyList<SplitPart> parts,
        CancellationToken ct)
    {
        if (parts.Count < 2)
            throw new ArgumentException("a split needs at least two parts.", nameof(parts));
        await EnsureFinalizedAsync(ct);
        await EnsureSegmentAsync(seq, expectedSource: source, ct);

        var lines = await new TranscriptStore(JsonlPath).ReadAllAsync(ct);
        var line = lines.First(l => l.Seq == seq);   // EnsureSegmentAsync already proved it exists

        for (int i = 0; i < parts.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(parts[i].Text))
                throw new ArgumentException(
                    $"split part {i} of seq {seq} is empty; transcript content is never removed (spec section 1.6).",
                    nameof(parts));
        }
        if (parts[0].StartMs != line.StartMs || parts[0].DerivedStart)
            throw new ArgumentException(
                $"first split part of seq {seq} must inherit the machine start {line.StartMs} (not derived).",
                nameof(parts));
        for (int i = 1; i < parts.Count; i++)
        {
            if (!parts[i].DerivedStart)
                throw new ArgumentException($"split part {i} of seq {seq} must be flagged DerivedStart.", nameof(parts));
            if (parts[i].StartMs <= parts[i - 1].StartMs)
                throw new ArgumentException($"split part starts for seq {seq} must strictly increase.", nameof(parts));
            if (parts[i].StartMs <= line.StartMs || parts[i].StartMs > line.EndMs)
                throw new ArgumentException(
                    $"split part {i} start {parts[i].StartMs} for seq {seq} is outside ({line.StartMs}, {line.EndMs}].",
                    nameof(parts));
        }

        var edits = await LoadAsync(ct) ?? new Edits();
        var splits = new Dictionary<string, SplitEntry>(edits.Splits)
        {
            [seq.ToString()] = new SplitEntry { Source = source, EditedAtUtc = _time.GetUtcNow(), Parts = parts },
        };
        var corrections = new Dictionary<string, Correction>(edits.Corrections);
        corrections.Remove(seq.ToString());   // absorbed into parts

        await JsonFile.WriteAsync(EditsPath,
            edits with { SchemaVersion = Version, Corrections = corrections, Splits = splits }, ct);
        await MarkEditedAsync(ct);
    }

    /// <summary>Revert a split (design section 2): remove splits[seq], restoring the single machine
    /// segment. Quiet no-op (false, writes nothing) when the seq was not split. Does NOT resurrect a
    /// prior correction — the machine floor is the revert target.</summary>
    public async Task<bool> RemoveSplitAsync(int seq, CancellationToken ct)
    {
        await EnsureFinalizedAsync(ct);
        var edits = await LoadAsync(ct);
        if (edits is null || !edits.Splits.ContainsKey(seq.ToString())) return false;
        var splits = new Dictionary<string, SplitEntry>(edits.Splits);
        splits.Remove(seq.ToString());
        await JsonFile.WriteAsync(EditsPath, edits with { SchemaVersion = Version, Splits = splits }, ct);
        await MarkEditedAsync(ct);
        return true;
    }

    /// <summary>Batch twin of EnsureSegmentAsync: ONE transcript.jsonl read validates every seq
    /// (exists, is a Segment, matches the expected source). Same exception contract.</summary>
    private async Task EnsureSegmentsAsync(IEnumerable<int> seqs, TranscriptSource? expectedSource,
        CancellationToken ct)
    {
        var wanted = seqs.ToHashSet();
        if (wanted.Count == 0) return;
        var lines = await new TranscriptStore(JsonlPath).ReadAllAsync(ct);
        var bySeq = lines.GroupBy(l => l.Seq).ToDictionary(g => g.Key, g => g.First());
        foreach (int seq in wanted)
        {
            if (!bySeq.TryGetValue(seq, out var line))
                throw new ArgumentException($"No transcript line with seq {seq}.", nameof(seqs));
            if (line.Kind != TranscriptKind.Segment)
                throw new ArgumentException(
                    $"seq {seq} is a system marker; only segments are correctable (spec section 1.6).", nameof(seqs));
            if (expectedSource is { } src && line.Source != src)
                throw new ArgumentException(
                    $"seq {seq} belongs to the {line.Source} stream, not {src} (spec section 1.3).", nameof(seqs));
        }
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
