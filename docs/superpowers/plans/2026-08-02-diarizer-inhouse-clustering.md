# Diarizer In-House Clustering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace sherpa-onnx FastClustering with deterministic in-house clustering on per-segment CAM++ embeddings inside LocalScribe.Diarizer, DER-gated against the private gold reference, with the App/Core wire contract byte-identical.

**Architecture:** The Diarizer pipeline becomes harvest (sherpa run with a tiny clustering threshold, labels discarded, boundaries kept) -> per-segment re-embed (in-process `SherpaEmbeddingRunner`) -> in-house clustering (new pure code in `LocalScribe.Core/Diarisation`: duration-weighted k-means, cosine silhouette auto-count, two-pass bridge attach) -> emit the existing `DiarisationResultPayload`. Spec: `docs/superpowers/specs/2026-08-02-diarizer-inhouse-clustering-design.md`.

**Tech Stack:** .NET 10 (`net10.0-windows`), xUnit (`tests/LocalScribe.Core.Tests`, flat files, global namespace, `Xunit` global using), sherpa-onnx 1.13.3 (Diarizer only), python 3 for offline eval (`tools/diar-eval/`).

## Global Constraints

- Wire contract byte-identical: stdin one camelCase JSON job; stdout zero+ `{"progress":<0..1>}` lines then EXACTLY ONE terminal line; exit 0 iff result emitted; error codes only `MODEL_MISSING` / `BAD_AUDIO` / `HELPER_CRASH`. The terminal result line must contain the substring `"segments"` and must NOT contain `"error"` or `"progress"` anywhere (App routes by substring).
- `clusterCount` = count of DISTINCT cluster ids present (never max+1). Segments emitted ordered by StartMs. Cluster ids contiguous 0-based, ordered by first temporal appearance.
- `emitEmbeddings` output stays byte-identical in shape: per-cluster mean CAM++ vectors keyed by bare id string, `embeddingMethod` `"campplus-zh-en"` (`EmbeddingMethods.CampPlus`), 30 s cap via `EmbeddingSamples.Slice`. Never change the embedding model or method string.
- New diarisation `Method` string: `"localscribe-cluster-v1:pyannote-seg-3.0+campplus-zh-en"` (const `DiarisationMethods.InHouseV1`).
- ForcedClusterCount semantics: null = auto (algorithm decides, capped 2..6, may return 1 = one voice); N = exactly N clusters whenever N non-empty clusters are producible (App tolerates fewer).
- Determinism everywhere: no RNG, every tie-break defined. Two identical runs must emit identical terminal lines.
- PRIVACY (hard): nothing derived from the gold session is ever committed. Gold artifacts live only in `tools/diar-eval/data/` (gitignored) and `models/diar-fixture/` (gitignored via `models/`). No personal names, session ids, or transcript content in committed files or commit messages.
- No "fixed"/success claim without a DER number from the C# end-to-end run (freshly built Debug Diarizer -> `tools/diar-eval/der.py` vs `tools/diar-eval/data/reference_gold.rttm`) in the same message. Never measure with the stale published exe under `src/LocalScribe.App/bin/`. Diarizer republish is HELD - requires explicit user approval.
- House rules: ASCII-only source, 0-warning build (`dotnet build LocalScribe.slnx -c Debug` - there is NO .sln), no Unicode emojis in test scripts, TDD (test first, watch it fail), commit per task on `feat/smoke-followups-2026-07-31`, do not push. If `LocalScribe.App.exe` locks `bin\` (MSB3027), close that specific PID only - NEVER a broad process kill.
- Gates that must stay green after every task: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` (Core 1015+new, App 832). The two pre-existing fixture failures (`DiarisationFixtureTests`, `GoldenCorpusFixtureTests.Golden_pair_wer_stays_at_baseline`) are expected while their corpora are absent.

---

### Task 1: Offline tuning harness (python mirror) and constant selection

**Files:**
- Create: `tools/diar-eval/tune_clustering.py`
- Output (not committed): `tools/diar-eval/data/TUNING.md`

**Interfaces:**
- Consumes: `tools/diar-eval/data/out_auto.jsonl` (218 harvested segments, last line containing `"segments"`), `tools/diar-eval/data/emb_cache.json` (L2-normalized embeddings keyed `"<startMs>_<endMs>"`), `tools/diar-eval/data/reference_gold.rttm`, `tools/diar-eval/der.py` (DER scorer mirroring the C# `DiarisationErrorRate`).
- Produces: `data/TUNING.md` with a DER table and the chosen values for `ReliableMinMs`, `SilhouetteFloor` (later tasks read these; defaults if the file is missing: 1000 / 0.20).

This task mirrors the EXACT algorithm the C# tasks will implement, so its numbers transfer. NOTE: the mirror clusters the cached auto-0.7 boundaries, which approximate the future harvest boundaries; the authoritative numbers come from Task 7's end-to-end run. This folder is untracked - this task makes NO git commit (the only such task).

- [ ] **Step 1: Write `tools/diar-eval/tune_clustering.py`**

```python
#!/usr/bin/env python
"""Offline tuning for the in-house clusterer (spec 2026-08-02). Mirrors the C# algorithm:
duration-weighted k-means (init: longest reliable segment, then farthest-first; empty-cluster
reseed; 200 iters), duration-weighted cosine silhouette, two-pass bridge attach.
Grid-searches ReliableMinMs x SilhouetteFloor, reports DER per candidate via der.py logic.
Reads everything from the gitignored data/ folder; writes data/TUNING.md. No private data
leaves data/."""
import json, math, os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
DATA = os.path.join(HERE, "data")
REF = os.path.join(DATA, "reference_gold.rttm")

def cd(a, b):  # cosine distance on normalized vectors
    return 1.0 - sum(x * y for x, y in zip(a, b))

def norm(v):
    n = math.sqrt(sum(x * x for x in v))
    return None if n == 0 else [x / n for x in v]

segs = None
for l in open(os.path.join(DATA, "out_auto.jsonl")):
    if '"segments"' in l:
        segs = json.loads(l)["segments"]
cache = json.load(open(os.path.join(DATA, "emb_cache.json")))
items = []  # (startMs, endMs, durS, emb-or-None)
for sg in segs:
    v = cache.get(f"{sg['startMs']}_{sg['endMs']}")
    items.append((sg["startMs"], sg["endMs"], (sg["endMs"] - sg["startMs"]) / 1000.0, v))
missing = sum(1 for it in items if it[3] is None)
print(f"segments={len(items)} cached-embeddings-missing={missing}")

def kmeans(vecs, w, k, iters=200):
    # init: longest segment first (max weight, tie -> lower index), then farthest-first
    order0 = max(range(len(vecs)), key=lambda i: (w[i], -i))
    cents = [list(vecs[order0])]
    while len(cents) < k:
        far = max(range(len(vecs)),
                  key=lambda i: (min(cd(vecs[i], c) for c in cents), -i))
        cents.append(list(vecs[far]))
    a = [-1] * len(vecs)
    for _ in range(iters):
        na = [min(range(k), key=lambda c: (cd(cents[c], v), c)) for v in vecs]
        if na == a:
            break
        a = na
        for c in range(k):
            idx = [i for i in range(len(vecs)) if a[i] == c]
            if not idx:  # empty-cluster reseed: farthest point from its own centroid
                far = max(range(len(vecs)),
                          key=lambda i: (cd(cents[a[i]], vecs[i]), -i))
                cents[c] = list(vecs[far])
                continue
            acc = [0.0] * len(vecs[0]); tw = 0.0
            for i in idx:
                for j, x in enumerate(vecs[i]):
                    acc[j] += x * w[i]
                tw += w[i]
            cents[c] = norm([x / tw for x in acc])
    return a, cents

def silhouette(vecs, w, a, k):
    tot_s = 0.0; tot_w = 0.0
    for i in range(len(vecs)):
        own = [j for j in range(len(vecs)) if a[j] == a[i] and j != i]
        if not own:
            s = 0.0
        else:
            wa = sum(w[j] for j in own)
            ai = sum(w[j] * cd(vecs[i], vecs[j]) for j in own) / wa
            bi = None
            for c in range(k):
                if c == a[i]:
                    continue
                oth = [j for j in range(len(vecs)) if a[j] == c]
                if not oth:
                    continue
                wb = sum(w[j] for j in oth)
                d = sum(w[j] * cd(vecs[i], vecs[j]) for j in oth) / wb
                bi = d if bi is None else min(bi, d)
            s = 0.0 if bi is None or max(ai, bi) == 0 else (bi - ai) / max(ai, bi)
        tot_s += w[i] * s; tot_w += w[i]
    return tot_s / tot_w

def cluster(items, forced_k, reliable_min_ms, sil_floor, max_auto=6):
    """Returns (assignments aligned to items, clusterCount, chosen_k, best_sil)."""
    usable = [(i, norm(it[3])) for i, it in enumerate(items) if it[3] is not None]
    usable = [(i, v) for i, v in usable if v is not None]
    rel = [(i, v) for i, v in usable if items[i][1] - items[i][0] >= reliable_min_ms]
    need = max(2, forced_k or 2)
    if len(rel) < need:
        rel = usable  # bar drop
    if not rel:
        return [0] * len(items), (1 if items else 0), 1, None
    vecs = [v for _, v in rel]; w = [items[i][2] for i, _ in rel]
    if forced_k is not None:
        k = min(forced_k, len(rel))
        a, cents = kmeans(vecs, w, k)
        best_sil = None
    else:
        best = None
        for k in range(2, min(max_auto, len(rel)) + 1):
            a_k, c_k = kmeans(vecs, w, k)
            s = silhouette(vecs, w, a_k, k)
            if best is None or s > best[0] + 1e-12:  # tie -> smaller k
                best = (s, k, a_k, c_k)
        best_sil, k, a, cents = best
        if best_sil < sil_floor:
            return [0] * len(items), 1, 1, best_sil
    assign = [None] * len(items)
    for (i, _), c in zip(rel, a):
        assign[i] = c
    rel_set = {i for i, _ in rel}
    for i, it in enumerate(items):  # bridge attach
        if i in rel_set:
            continue
        v = norm(it[3]) if it[3] is not None else None
        if v is None:  # zero-norm/missing -> temporally nearest reliable segment's cluster
            mid = (it[0] + it[1]) / 2
            j = min(rel_set, key=lambda r: (abs((items[r][0] + items[r][1]) / 2 - mid), r))
            assign[i] = assign[j]
        else:
            assign[i] = min(range(len(cents)), key=lambda c: (cd(cents[c], v), c))
    return assign, len(set(assign)), k, best_sil

def der(assign, items):
    hyp = {"segments": [{"startMs": it[0], "endMs": it[1], "cluster": a}
                        for it, a in zip(items, assign)],
           "clusterCount": len(set(assign)), "method": "tune"}
    hyp_path = os.path.join(DATA, "_tune_hyp.jsonl")
    with open(hyp_path, "w") as f:
        f.write(json.dumps(hyp))
    out = subprocess.run([sys.executable, os.path.join(HERE, "der.py"), hyp_path, REF],
                         capture_output=True, text=True).stdout
    for tok in out.split():
        try:
            return float(tok)
        except ValueError:
            continue
    raise RuntimeError(f"der.py output unparseable: {out}")

rows = []
for rmin in (600, 800, 1000, 1250, 1500):
    for floor_ in (0.10, 0.15, 0.20, 0.25, 0.30):
        a2, _, _, _ = cluster(items, 2, rmin, floor_)
        a3, _, _, _ = cluster(items, 3, rmin, floor_)
        aa, cc, kk, sil = cluster(items, None, rmin, floor_)
        rows.append((rmin, floor_, der(a2, items), der(a3, items),
                     kk, sil, der(aa, items)))
        print(rows[-1])

with open(os.path.join(DATA, "TUNING.md"), "w") as f:
    f.write("# Clustering constant tuning (offline mirror, cached auto-0.7 boundaries)\n\n")
    f.write("Baselines: sherpa forced-2 27.3% / sherpa auto 59.3% / single-pass demo 16.4% / floor ~8.1%\n\n")
    f.write("| ReliableMinMs | SilFloor | forced2 DER | forced3 DER | auto k | auto sil | auto DER |\n")
    f.write("|---|---|---|---|---|---|---|\n")
    for r in rows:
        f.write(f"| {r[0]} | {r[1]:.2f} | {r[2]:.4f} | {r[3]:.4f} | {r[4]} | "
                f"{'-' if r[5] is None else f'{r[5]:.3f}'} | {r[6]:.4f} |\n")
    best = min(rows, key=lambda r: r[6] + r[2])
    f.write(f"\nCHOSEN: ReliableMinMs={best[0]}, SilhouetteFloor={best[1]:.2f} "
            f"(auto DER {best[6]:.4f}, forced2 DER {best[2]:.4f}, auto k={best[4]})\n")
print("wrote data/TUNING.md")
```

- [ ] **Step 2: Run it**

Run: `python tools/diar-eval/tune_clustering.py`
Expected: a 25-row grid printed; `data/TUNING.md` written. Sanity: at least one row's auto DER <= 0.17 and auto k in {2,3}; forced-2 DER <= 0.17 for the chosen row (the single-pass demo already achieved 0.164, so the two-pass variant should be in that neighbourhood or better). SilhouetteFloor sanity: the chosen floor must sit clearly BELOW the gold run's auto silhouette (so a real 2-speaker recording never trips the one-voice guard).

- [ ] **Step 3: Record the outcome**

Read `data/TUNING.md`; note CHOSEN ReliableMinMs and SilhouetteFloor. If the grid contradicts the spec defaults (1000 / 0.20), the chosen values are used as the `ClusteringOptions` record defaults in Task 4. If NO row meets the sanity criteria, STOP and report to the user with the table - do not proceed to C# implementation on a failing algorithm.

- [ ] **Step 4: No commit**

`tools/diar-eval/` is untracked by design (private-data adjacency). Nothing to commit; state the chosen constants in the task report.

---

### Task 2: WeightedKMeans (Core, TDD)

**Files:**
- Create: `src/LocalScribe.Core/Diarisation/WeightedKMeans.cs`
- Test: `tests/LocalScribe.Core.Tests/WeightedKMeansTests.cs`

**Interfaces:**
- Consumes: `LocalScribe.Core.Diarisation.VoiceprintMath.Cosine(float[] a, float[] b) -> double` (existing; returns 0.0 on degenerate input).
- Produces:
  - `public sealed record KMeansResult(int[] Assignments, float[][] Centroids);`
  - `public static class WeightedKMeans` with:
    - `public const int MaxIterations = 200;`
    - `public static float[]? NormalizeOrNull(float[] v)` - L2-normalize; null when the norm is 0 or the vector is empty.
    - `public static KMeansResult Fit(IReadOnlyList<float[]> vectors, IReadOnlyList<double> weights, int k)` - vectors MUST be L2-normalized and non-empty, weights positive, `1 <= k <= vectors.Count`; throws `ArgumentException` otherwise.

All distances are cosine distance `1 - VoiceprintMath.Cosine(a, b)`. Init: first centroid = the vector with the LARGEST weight (tie: lowest index); each next centroid = the vector with the largest min-distance to the chosen centroids (tie: lowest index). Iterate: hard-assign each vector to the nearest centroid (tie: lowest centroid index); recompute each centroid as the weight-weighted mean of its members re-normalized; an emptied cluster reseeds to the vector farthest from its currently assigned centroid (tie: lowest index). Stop on assignment fixed-point or after `MaxIterations`.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/WeightedKMeansTests.cs`:

```csharp
using LocalScribe.Core.Diarisation;

public class WeightedKMeansTests
{
    private static float[] V(params float[] xs) => WeightedKMeans.NormalizeOrNull(xs)!;

    [Fact]
    public void Normalize_returns_unit_vector_and_null_on_zero_norm()
    {
        var v = WeightedKMeans.NormalizeOrNull([3f, 4f])!;
        Assert.Equal(0.6f, v[0], 5);
        Assert.Equal(0.8f, v[1], 5);
        Assert.Null(WeightedKMeans.NormalizeOrNull([0f, 0f]));
        Assert.Null(WeightedKMeans.NormalizeOrNull([]));
    }

    [Fact]
    public void Separates_two_obvious_blobs()
    {
        // Two orthogonal directions with small perturbations.
        var vectors = new List<float[]>
        {
            V(1f, 0.05f), V(1f, -0.05f), V(1f, 0.02f),
            V(0.05f, 1f), V(-0.05f, 1f), V(0.02f, 1f),
        };
        var weights = new List<double> { 1, 1, 1, 1, 1, 1 };
        var r = WeightedKMeans.Fit(vectors, weights, 2);
        Assert.Equal(r.Assignments[0], r.Assignments[1]);
        Assert.Equal(r.Assignments[1], r.Assignments[2]);
        Assert.Equal(r.Assignments[3], r.Assignments[4]);
        Assert.Equal(r.Assignments[4], r.Assignments[5]);
        Assert.NotEqual(r.Assignments[0], r.Assignments[3]);
    }

    [Fact]
    public void Is_deterministic_across_runs()
    {
        var vectors = new List<float[]>
        {
            V(1f, 0.3f), V(0.9f, 0.1f), V(0.2f, 1f), V(0.1f, 0.8f), V(0.5f, 0.5f),
        };
        var weights = new List<double> { 2.0, 1.0, 3.0, 1.5, 0.4 };
        var a = WeightedKMeans.Fit(vectors, weights, 2);
        var b = WeightedKMeans.Fit(vectors, weights, 2);
        Assert.Equal(a.Assignments, b.Assignments);
        for (int c = 0; c < 2; c++) Assert.Equal(a.Centroids[c], b.Centroids[c]);
    }

    [Fact]
    public void Heavy_weight_anchors_the_centroid()
    {
        // One heavy on-axis vector + one light off-axis vector in the same cluster:
        // the centroid must sit near the heavy vector.
        var vectors = new List<float[]> { V(1f, 0f), V(0.7f, 0.7f), V(0f, 1f) };
        var weights = new List<double> { 100.0, 1.0, 100.0 };
        var r = WeightedKMeans.Fit(vectors, weights, 2);
        int clusterOfHeavy = r.Assignments[0];
        double dHeavy = 1 - VoiceprintMath.Cosine(r.Centroids[clusterOfHeavy], vectors[0]);
        Assert.True(dHeavy < 0.05, $"centroid drifted from heavy anchor: {dHeavy}");
    }

    [Fact]
    public void K_equals_one_puts_everything_in_cluster_zero()
    {
        var vectors = new List<float[]> { V(1f, 0f), V(0f, 1f) };
        var r = WeightedKMeans.Fit(vectors, [1.0, 1.0], 1);
        Assert.Equal(new[] { 0, 0 }, r.Assignments);
        Assert.Single(r.Centroids);
    }

    [Fact]
    public void K_equals_count_gives_every_vector_its_own_cluster()
    {
        var vectors = new List<float[]> { V(1f, 0f), V(0f, 1f), V(0.7f, -0.7f) };
        var r = WeightedKMeans.Fit(vectors, [1.0, 1.0, 1.0], 3);
        Assert.Equal(3, r.Assignments.Distinct().Count());
    }

    [Fact]
    public void Invalid_arguments_throw()
    {
        var one = new List<float[]> { V(1f, 0f) };
        Assert.Throws<ArgumentException>(() => WeightedKMeans.Fit([], [], 1));
        Assert.Throws<ArgumentException>(() => WeightedKMeans.Fit(one, [1.0], 0));
        Assert.Throws<ArgumentException>(() => WeightedKMeans.Fit(one, [1.0], 2));
        Assert.Throws<ArgumentException>(() => WeightedKMeans.Fit(one, [1.0, 1.0], 1));
        Assert.Throws<ArgumentException>(() => WeightedKMeans.Fit(one, [0.0], 1));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~WeightedKMeansTests"`
Expected: compile FAILURE (`WeightedKMeans` does not exist) - that counts as the failing state.

- [ ] **Step 3: Implement `src/LocalScribe.Core/Diarisation/WeightedKMeans.cs`**

```csharp
namespace LocalScribe.Core.Diarisation;

public sealed record KMeansResult(int[] Assignments, float[][] Centroids);

/// <summary>Deterministic duration-weighted k-means on L2-normalized embeddings, cosine
/// distance (in-house clustering design 2026-08-02). No RNG: init picks the heaviest vector
/// then farthest-first; every tie-break is lowest-index/lowest-cluster. Callers normalize via
/// <see cref="NormalizeOrNull"/> and drop nulls before calling <see cref="Fit"/>.</summary>
public static class WeightedKMeans
{
    public const int MaxIterations = 200;

    public static float[]? NormalizeOrNull(float[] v)
    {
        double norm = 0;
        for (int i = 0; i < v.Length; i++) norm += (double)v[i] * v[i];
        if (v.Length == 0 || norm <= 0) return null;
        double inv = 1.0 / Math.Sqrt(norm);
        var outp = new float[v.Length];
        for (int i = 0; i < v.Length; i++) outp[i] = (float)(v[i] * inv);
        return outp;
    }

    public static KMeansResult Fit(IReadOnlyList<float[]> vectors, IReadOnlyList<double> weights, int k)
    {
        if (vectors.Count == 0) throw new ArgumentException("no vectors", nameof(vectors));
        if (weights.Count != vectors.Count) throw new ArgumentException("weights/vectors length mismatch", nameof(weights));
        if (k < 1 || k > vectors.Count) throw new ArgumentException($"k={k} outside 1..{vectors.Count}", nameof(k));
        for (int i = 0; i < weights.Count; i++)
            if (weights[i] <= 0) throw new ArgumentException("weights must be positive", nameof(weights));

        static double Dist(float[] a, float[] b) => 1.0 - VoiceprintMath.Cosine(a, b);

        // Init: heaviest vector first, then farthest-first. Ties -> lowest index.
        var centroids = new List<float[]>(k);
        int seed = 0;
        for (int i = 1; i < vectors.Count; i++) if (weights[i] > weights[seed]) seed = i;
        centroids.Add((float[])vectors[seed].Clone());
        while (centroids.Count < k)
        {
            int far = 0; double best = double.NegativeInfinity;
            for (int i = 0; i < vectors.Count; i++)
            {
                double d = double.PositiveInfinity;
                foreach (var c in centroids) d = Math.Min(d, Dist(vectors[i], c));
                if (d > best) { best = d; far = i; }
            }
            centroids.Add((float[])vectors[far].Clone());
        }

        var assign = new int[vectors.Count];
        Array.Fill(assign, -1);
        for (int iter = 0; iter < MaxIterations; iter++)
        {
            var next = new int[vectors.Count];
            for (int i = 0; i < vectors.Count; i++)
            {
                int bestC = 0; double bestD = Dist(vectors[i], centroids[0]);
                for (int c = 1; c < k; c++)
                {
                    double d = Dist(vectors[i], centroids[c]);
                    if (d < bestD) { bestD = d; bestC = c; }
                }
                next[i] = bestC;
            }
            if (next.AsSpan().SequenceEqual(assign)) break;
            assign = next;

            for (int c = 0; c < k; c++)
            {
                var acc = new double[vectors[0].Length];
                double tw = 0;
                for (int i = 0; i < vectors.Count; i++)
                {
                    if (assign[i] != c) continue;
                    for (int j = 0; j < acc.Length; j++) acc[j] += vectors[i][j] * weights[i];
                    tw += weights[i];
                }
                if (tw <= 0)
                {
                    // Emptied cluster: reseed to the vector farthest from its own centroid.
                    int far = 0; double best = double.NegativeInfinity;
                    for (int i = 0; i < vectors.Count; i++)
                    {
                        double d = Dist(vectors[i], centroids[assign[i]]);
                        if (d > best) { best = d; far = i; }
                    }
                    centroids[c] = (float[])vectors[far].Clone();
                    continue;
                }
                var mean = new float[acc.Length];
                for (int j = 0; j < acc.Length; j++) mean[j] = (float)(acc[j] / tw);
                centroids[c] = NormalizeOrNull(mean) ?? centroids[c];
            }
        }
        return new KMeansResult(assign, [.. centroids]);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~WeightedKMeansTests"`
Expected: 7 PASS.

- [ ] **Step 5: Full gate + commit**

Run: `dotnet build LocalScribe.slnx -c Debug` (expect 0 warnings) then `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` (all green).

```bash
git add src/LocalScribe.Core/Diarisation/WeightedKMeans.cs tests/LocalScribe.Core.Tests/WeightedKMeansTests.cs
git commit -m "feat(diarisation): deterministic duration-weighted k-means primitive"
```

---

### Task 3: CosineSilhouette (Core, TDD)

**Files:**
- Create: `src/LocalScribe.Core/Diarisation/CosineSilhouette.cs`
- Test: `tests/LocalScribe.Core.Tests/CosineSilhouetteTests.cs`

**Interfaces:**
- Consumes: `VoiceprintMath.Cosine` (existing).
- Produces: `public static class CosineSilhouette` with
  `public static double Weighted(IReadOnlyList<float[]> vectors, IReadOnlyList<double> weights, IReadOnlyList<int> assignments, int clusterCount)` - vectors L2-normalized; returns the weight-weighted mean silhouette in [-1, 1]; a point in a singleton cluster scores 0; returns 0 when every point is a singleton or `clusterCount < 2`.

Per point i: `a(i)` = weighted mean cosine distance to same-cluster points (excluding self); `b(i)` = min over other non-empty clusters of the weighted mean distance to that cluster's points; `s(i) = (b - a) / max(a, b)` (0 when the max is 0). Overall = sum(w_i * s_i) / sum(w_i).

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/CosineSilhouetteTests.cs`:

```csharp
using LocalScribe.Core.Diarisation;

public class CosineSilhouetteTests
{
    private static float[] V(params float[] xs) => WeightedKMeans.NormalizeOrNull(xs)!;

    [Fact]
    public void Well_separated_blobs_score_near_one()
    {
        var vectors = new List<float[]>
        {
            V(1f, 0.02f), V(1f, -0.02f), V(0.02f, 1f), V(-0.02f, 1f),
        };
        double s = CosineSilhouette.Weighted(vectors, [1.0, 1.0, 1.0, 1.0], [0, 0, 1, 1], 2);
        Assert.True(s > 0.8, $"expected near 1, got {s}");
    }

    [Fact]
    public void Random_split_of_one_blob_scores_near_zero_or_negative()
    {
        var vectors = new List<float[]>
        {
            V(1f, 0.01f), V(1f, -0.01f), V(1f, 0.02f), V(1f, -0.02f),
        };
        double s = CosineSilhouette.Weighted(vectors, [1.0, 1.0, 1.0, 1.0], [0, 1, 0, 1], 2);
        Assert.True(s < 0.2, $"expected low, got {s}");
    }

    [Fact]
    public void Singleton_cluster_scores_zero_for_that_point()
    {
        var vectors = new List<float[]> { V(1f, 0f), V(0f, 1f), V(0.01f, 1f) };
        // Point 0 is a singleton: s(0)=0. Points 1,2 are a tight pair far from cluster 0.
        double s = CosineSilhouette.Weighted(vectors, [1.0, 1.0, 1.0], [0, 1, 1], 2);
        Assert.InRange(s, 0.5, 1.0); // (0 + ~1 + ~1) / 3
    }

    [Fact]
    public void Weights_shift_the_mean()
    {
        var vectors = new List<float[]> { V(1f, 0f), V(0f, 1f), V(0.01f, 1f) };
        double light = CosineSilhouette.Weighted(vectors, [1.0, 1.0, 1.0], [0, 1, 1], 2);
        double heavy = CosineSilhouette.Weighted(vectors, [100.0, 1.0, 1.0], [0, 1, 1], 2);
        Assert.True(heavy < light, $"singleton weight 100 should drag the mean: {heavy} !< {light}");
    }

    [Fact]
    public void Single_cluster_returns_zero()
    {
        var vectors = new List<float[]> { V(1f, 0f), V(0f, 1f) };
        Assert.Equal(0.0, CosineSilhouette.Weighted(vectors, [1.0, 1.0], [0, 0], 1));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~CosineSilhouetteTests"`
Expected: compile FAILURE (`CosineSilhouette` does not exist).

- [ ] **Step 3: Implement `src/LocalScribe.Core/Diarisation/CosineSilhouette.cs`**

```csharp
namespace LocalScribe.Core.Diarisation;

/// <summary>Duration-weighted cosine silhouette for auto speaker-count selection (in-house
/// clustering design 2026-08-02). Vectors must be L2-normalized. A singleton point scores 0;
/// clusterCount &lt; 2 scores 0 overall.</summary>
public static class CosineSilhouette
{
    public static double Weighted(
        IReadOnlyList<float[]> vectors,
        IReadOnlyList<double> weights,
        IReadOnlyList<int> assignments,
        int clusterCount)
    {
        if (clusterCount < 2 || vectors.Count == 0) return 0.0;

        static double Dist(float[] a, float[] b) => 1.0 - VoiceprintMath.Cosine(a, b);

        double totalS = 0, totalW = 0;
        for (int i = 0; i < vectors.Count; i++)
        {
            double aSum = 0, aW = 0;
            var bSum = new double[clusterCount];
            var bW = new double[clusterCount];
            for (int j = 0; j < vectors.Count; j++)
            {
                if (j == i) continue;
                double d = Dist(vectors[i], vectors[j]);
                if (assignments[j] == assignments[i]) { aSum += weights[j] * d; aW += weights[j]; }
                else { bSum[assignments[j]] += weights[j] * d; bW[assignments[j]] += weights[j]; }
            }

            double s;
            if (aW <= 0) s = 0.0; // singleton cluster
            else
            {
                double a = aSum / aW;
                double b = double.PositiveInfinity;
                for (int c = 0; c < clusterCount; c++)
                    if (c != assignments[i] && bW[c] > 0) b = Math.Min(b, bSum[c] / bW[c]);
                s = double.IsPositiveInfinity(b) || Math.Max(a, b) == 0
                    ? 0.0
                    : (b - a) / Math.Max(a, b);
            }
            totalS += weights[i] * s;
            totalW += weights[i];
        }
        return totalW <= 0 ? 0.0 : totalS / totalW;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~CosineSilhouetteTests"`
Expected: 5 PASS.

- [ ] **Step 5: Full gate + commit**

Run: `dotnet build LocalScribe.slnx -c Debug` (0 warnings), `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` (green).

```bash
git add src/LocalScribe.Core/Diarisation/CosineSilhouette.cs tests/LocalScribe.Core.Tests/CosineSilhouetteTests.cs
git commit -m "feat(diarisation): duration-weighted cosine silhouette"
```

---

### Task 4: SpeakerClustering forced path (Core, TDD)

**Files:**
- Create: `src/LocalScribe.Core/Diarisation/SpeakerClustering.cs`
- Test: `tests/LocalScribe.Core.Tests/SpeakerClusteringTests.cs`

**Interfaces:**
- Consumes: `WeightedKMeans.Fit`, `WeightedKMeans.NormalizeOrNull`, `CosineSilhouette.Weighted`, `VoiceprintMath.Cosine`.
- Produces (Task 5 extends the same file; Task 6 calls `Cluster` from the Diarizer):
  - `public sealed record TimedEmbedding(long StartMs, long EndMs, float[] Embedding);`
  - `public sealed record ClusteringOptions(int ReliableMinMs = 1000, double SilhouetteFloor = 0.20, int MaxAutoClusters = 6);` - update the two tunable defaults to Task 1's CHOSEN values from `tools/diar-eval/data/TUNING.md` if that file exists and differs.
  - `public sealed record ClusterOutcome(int[] ClusterBySegment, int ClusterCount);`
  - `public static class SpeakerClustering` with `public static ClusterOutcome Cluster(IReadOnlyList<TimedEmbedding> segments, int? forcedClusterCount, ClusteringOptions? options = null)`.

Behaviour (forced `forcedClusterCount = N`, this task; auto = Task 5):
1. Normalize all embeddings (`NormalizeOrNull`); a null (zero-norm) embedding is ALWAYS a bridge.
2. Reliable = normalized AND duration >= `ReliableMinMs`. If reliable count < max(2, N), the bar drops: all normalized segments are reliable. If NO normalized embeddings exist: everything goes to cluster 0, `ClusterCount` = (segments.Count > 0 ? 1 : 0).
3. k = min(N, reliableCount); weighted k-means over reliable segments, weight = duration in seconds.
4. Bridge attach: nearest centroid (tie: lowest cluster index); zero-norm bridges take the cluster of the temporally nearest reliable segment (by midpoint distance, tie: earlier segment).
5. Renumber cluster ids contiguous 0-based by first temporal appearance (order segments by StartMs, then EndMs, then input index). `ClusterBySegment` is aligned to the INPUT order; `ClusterCount` = distinct ids.

- [ ] **Step 1: Write the failing tests**

Create `tests/LocalScribe.Core.Tests/SpeakerClusteringTests.cs`:

```csharp
using LocalScribe.Core.Diarisation;

public class SpeakerClusteringTests
{
    private static float[] V(params float[] xs) => xs;

    // Two voices: direction (1,0) and direction (0,1). Long segments are reliable.
    private static List<TimedEmbedding> TwoVoices() =>
    [
        new(0, 3000, V(1f, 0.05f)),        // A reliable
        new(3000, 3500, V(0.2f, 0.9f)),    // bridge (500ms), actually voice B
        new(3500, 8000, V(0.03f, 1f)),     // B reliable
        new(8000, 12000, V(1f, -0.04f)),   // A reliable
        new(12000, 12400, V(0.9f, 0.1f)),  // bridge (400ms), voice A
        new(12400, 20000, V(0.01f, 0.98f)),// B reliable
    ];

    [Fact]
    public void Forced_two_recovers_both_voices_and_numbers_by_first_appearance()
    {
        var r = SpeakerClustering.Cluster(TwoVoices(), 2);
        Assert.Equal(2, r.ClusterCount);
        // First temporal segment is voice A -> cluster 0; voice B -> cluster 1.
        Assert.Equal(0, r.ClusterBySegment[0]);
        Assert.Equal(1, r.ClusterBySegment[2]);
        Assert.Equal(0, r.ClusterBySegment[3]);
        Assert.Equal(1, r.ClusterBySegment[5]);
        // Bridges attach to the right voice.
        Assert.Equal(1, r.ClusterBySegment[1]);
        Assert.Equal(0, r.ClusterBySegment[4]);
    }

    [Fact]
    public void Forced_result_is_deterministic()
    {
        var a = SpeakerClustering.Cluster(TwoVoices(), 2);
        var b = SpeakerClustering.Cluster(TwoVoices(), 2);
        Assert.Equal(a.ClusterBySegment, b.ClusterBySegment);
        Assert.Equal(a.ClusterCount, b.ClusterCount);
    }

    [Fact]
    public void Zero_norm_bridge_takes_temporally_nearest_reliable_cluster()
    {
        var segs = new List<TimedEmbedding>
        {
            new(0, 3000, V(1f, 0f)),
            new(3000, 3200, V(0f, 0f)),      // zero-norm, nearest reliable is seg 0 (A)
            new(9000, 14000, V(0f, 1f)),
        };
        var r = SpeakerClustering.Cluster(segs, 2);
        Assert.Equal(r.ClusterBySegment[0], r.ClusterBySegment[1]);
    }

    [Fact]
    public void Forced_k_clamps_to_available_segments()
    {
        var segs = new List<TimedEmbedding>
        {
            new(0, 2000, V(1f, 0f)),
            new(2000, 4000, V(0f, 1f)),
        };
        var r = SpeakerClustering.Cluster(segs, 5);
        Assert.Equal(2, r.ClusterCount);
    }

    [Fact]
    public void Bar_drops_when_too_few_reliable_segments()
    {
        // All segments are sub-second bridges; forced 2 must still produce 2 clusters.
        var segs = new List<TimedEmbedding>
        {
            new(0, 500, V(1f, 0.02f)),
            new(500, 900, V(0.9f, 0f)),
            new(900, 1400, V(0.02f, 1f)),
            new(1400, 1800, V(0f, 0.9f)),
        };
        var r = SpeakerClustering.Cluster(segs, 2);
        Assert.Equal(2, r.ClusterCount);
        Assert.Equal(r.ClusterBySegment[0], r.ClusterBySegment[1]);
        Assert.Equal(r.ClusterBySegment[2], r.ClusterBySegment[3]);
        Assert.NotEqual(r.ClusterBySegment[0], r.ClusterBySegment[2]);
    }

    [Fact]
    public void All_zero_norm_embeddings_collapse_to_single_cluster()
    {
        var segs = new List<TimedEmbedding>
        {
            new(0, 2000, V(0f, 0f)),
            new(2000, 4000, V(0f, 0f)),
        };
        var r = SpeakerClustering.Cluster(segs, 2);
        Assert.Equal(1, r.ClusterCount);
        Assert.Equal(new[] { 0, 0 }, r.ClusterBySegment);
    }

    [Fact]
    public void Empty_input_yields_empty_outcome()
    {
        var r = SpeakerClustering.Cluster([], 2);
        Assert.Empty(r.ClusterBySegment);
        Assert.Equal(0, r.ClusterCount);
    }

    [Fact]
    public void Forced_one_puts_everything_in_cluster_zero()
    {
        var r = SpeakerClustering.Cluster(TwoVoices(), 1);
        Assert.Equal(1, r.ClusterCount);
        Assert.All(r.ClusterBySegment, c => Assert.Equal(0, c));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SpeakerClusteringTests"`
Expected: compile FAILURE (`SpeakerClustering` does not exist).

- [ ] **Step 3: Implement `src/LocalScribe.Core/Diarisation/SpeakerClustering.cs` (forced path; auto throws for now)**

```csharp
namespace LocalScribe.Core.Diarisation;

public sealed record TimedEmbedding(long StartMs, long EndMs, float[] Embedding);

/// <summary>Tunables for the in-house clusterer (design 2026-08-02). Defaults come from the
/// offline tuning grid against the gold reference (tools/diar-eval, private data); tests pass
/// explicit values so re-tuning the defaults never breaks unit tests.</summary>
public sealed record ClusteringOptions(
    int ReliableMinMs = 1000,
    double SilhouetteFloor = 0.20,
    int MaxAutoClusters = 6);

public sealed record ClusterOutcome(int[] ClusterBySegment, int ClusterCount);

/// <summary>In-house speaker clustering over per-segment CAM++ embeddings (design 2026-08-02,
/// replacing sherpa FastClustering which collapsed separable embeddings). Deterministic: no RNG,
/// all tie-breaks defined. Two-pass: reliable segments (>= ReliableMinMs, non-degenerate
/// embedding) form centroids; short "bridge" segments attach to the nearest centroid afterwards
/// (their embeddings are duration-starved, not mixed-voice). Cluster ids are renumbered
/// contiguous 0-based by first temporal appearance.</summary>
public static class SpeakerClustering
{
    public static ClusterOutcome Cluster(
        IReadOnlyList<TimedEmbedding> segments,
        int? forcedClusterCount,
        ClusteringOptions? options = null)
    {
        var opt = options ?? new ClusteringOptions();
        if (segments.Count == 0) return new ClusterOutcome([], 0);

        var normalized = new float[segments.Count][];
        var usable = new List<int>();
        for (int i = 0; i < segments.Count; i++)
        {
            var n = WeightedKMeans.NormalizeOrNull(segments[i].Embedding);
            if (n is not null) { normalized[i] = n; usable.Add(i); }
        }
        if (usable.Count == 0)
            return new ClusterOutcome(new int[segments.Count], 1);

        int need = Math.Max(2, forcedClusterCount ?? 2);
        var reliable = usable.Where(i =>
            segments[i].EndMs - segments[i].StartMs >= opt.ReliableMinMs).ToList();
        if (reliable.Count < need) reliable = usable; // bar drop

        var vectors = reliable.Select(i => normalized[i]).ToList();
        var weights = reliable.Select(i =>
            (segments[i].EndMs - segments[i].StartMs) / 1000.0).ToList();

        int k;
        int[] fitAssign;
        float[][] centroids;
        if (forcedClusterCount is int forced)
        {
            k = Math.Clamp(forced, 1, reliable.Count);
            var fit = WeightedKMeans.Fit(vectors, weights, k);
            (fitAssign, centroids) = (fit.Assignments, fit.Centroids);
        }
        else
        {
            (k, fitAssign, centroids) = SelectAutoCount(vectors, weights, opt);
        }

        var raw = new int[segments.Count];
        Array.Fill(raw, -1);
        for (int r = 0; r < reliable.Count; r++) raw[reliable[r]] = fitAssign[r];

        var reliableSet = new HashSet<int>(reliable);
        for (int i = 0; i < segments.Count; i++)
        {
            if (reliableSet.Contains(i)) continue;
            if (normalized[i] is not null)
            {
                int bestC = 0;
                double bestD = 1.0 - VoiceprintMath.Cosine(centroids[0], normalized[i]);
                for (int c = 1; c < centroids.Length; c++)
                {
                    double d = 1.0 - VoiceprintMath.Cosine(centroids[c], normalized[i]);
                    if (d < bestD) { bestD = d; bestC = c; }
                }
                raw[i] = bestC;
            }
            else
            {
                long mid = (segments[i].StartMs + segments[i].EndMs) / 2;
                int nearest = reliable[0];
                long nearestDist = long.MaxValue;
                foreach (int r in reliable)
                {
                    long rMid = (segments[r].StartMs + segments[r].EndMs) / 2;
                    long d = Math.Abs(rMid - mid);
                    if (d < nearestDist || (d == nearestDist && r < nearest))
                        { nearestDist = d; nearest = r; }
                }
                raw[i] = raw[nearest];
            }
        }

        // Renumber contiguous 0-based by first temporal appearance.
        var order = Enumerable.Range(0, segments.Count)
            .OrderBy(i => segments[i].StartMs).ThenBy(i => segments[i].EndMs).ThenBy(i => i);
        var remap = new Dictionary<int, int>();
        foreach (int i in order)
            if (!remap.ContainsKey(raw[i])) remap[raw[i]] = remap.Count;
        var final = new int[segments.Count];
        for (int i = 0; i < segments.Count; i++) final[i] = remap[raw[i]];
        return new ClusterOutcome(final, remap.Count);
    }

    private static (int K, int[] Assignments, float[][] Centroids) SelectAutoCount(
        IReadOnlyList<float[]> vectors, IReadOnlyList<double> weights, ClusteringOptions opt)
        => throw new NotImplementedException("auto path lands in the next task");
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SpeakerClusteringTests"`
Expected: 8 PASS (all tests in this task force a count; none hit the auto path).

- [ ] **Step 5: Full gate + commit**

Run: `dotnet build LocalScribe.slnx -c Debug` (0 warnings), `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` (green).

```bash
git add src/LocalScribe.Core/Diarisation/SpeakerClustering.cs tests/LocalScribe.Core.Tests/SpeakerClusteringTests.cs
git commit -m "feat(diarisation): two-pass forced-count speaker clustering with bridge attach"
```

---

### Task 5: SpeakerClustering auto path (Core, TDD)

**Files:**
- Modify: `src/LocalScribe.Core/Diarisation/SpeakerClustering.cs` (replace the `SelectAutoCount` stub)
- Test: append to `tests/LocalScribe.Core.Tests/SpeakerClusteringTests.cs`

**Interfaces:**
- Consumes: everything Task 4 produced; `CosineSilhouette.Weighted`.
- Produces: the auto branch of `SpeakerClustering.Cluster(segments, null, options)`: scan k in `2..min(MaxAutoClusters, reliableCount)`, run `WeightedKMeans.Fit` per k, score with `CosineSilhouette.Weighted` over the reliable set, pick the best score (tie: smaller k). If the best score < `SilhouetteFloor`, everything is ONE cluster (id 0) - the import OneVoice guard depends on this honesty. If `reliableCount < 2`, one cluster.

- [ ] **Step 1: Write the failing tests (append to SpeakerClusteringTests.cs)**

```csharp
    [Fact]
    public void Auto_finds_two_well_separated_voices()
    {
        var r = SpeakerClustering.Cluster(TwoVoices(), null);
        Assert.Equal(2, r.ClusterCount);
        Assert.Equal(r.ClusterBySegment[0], r.ClusterBySegment[3]);
        Assert.Equal(r.ClusterBySegment[2], r.ClusterBySegment[5]);
        Assert.NotEqual(r.ClusterBySegment[0], r.ClusterBySegment[2]);
    }

    [Fact]
    public void Auto_finds_three_voices()
    {
        var segs = new List<TimedEmbedding>
        {
            new(0, 4000, V(1f, 0.02f, 0f)),
            new(4000, 9000, V(0f, 1f, 0.03f)),
            new(9000, 13000, V(0.98f, 0f, 0.05f)),
            new(13000, 17000, V(0.02f, 0f, 1f)),
            new(17000, 21000, V(0f, 0.97f, 0.02f)),
            new(21000, 26000, V(0f, 0.04f, 0.98f)),
        };
        var r = SpeakerClustering.Cluster(segs, null);
        Assert.Equal(3, r.ClusterCount);
        Assert.Equal(r.ClusterBySegment[0], r.ClusterBySegment[2]);
        Assert.Equal(r.ClusterBySegment[1], r.ClusterBySegment[4]);
        Assert.Equal(r.ClusterBySegment[3], r.ClusterBySegment[5]);
    }

    [Fact]
    public void Auto_returns_one_cluster_for_a_single_voice()
    {
        // One tight blob: best split scores below any sane silhouette floor.
        var segs = new List<TimedEmbedding>
        {
            new(0, 3000, V(1f, 0.010f)),
            new(3000, 7000, V(1f, -0.012f)),
            new(7000, 11000, V(1f, 0.008f)),
            new(11000, 14000, V(1f, -0.006f)),
        };
        var r = SpeakerClustering.Cluster(segs, null);
        Assert.Equal(1, r.ClusterCount);
        Assert.All(r.ClusterBySegment, c => Assert.Equal(0, c));
    }

    [Fact]
    public void Auto_respects_the_max_cluster_cap()
    {
        // Seven orthogonal-ish directions but MaxAutoClusters = 3 caps the scan.
        var segs = new List<TimedEmbedding>();
        for (int i = 0; i < 7; i++)
        {
            var e = new float[7];
            e[i] = 1f;
            segs.Add(new TimedEmbedding(i * 3000, i * 3000 + 2500, e));
        }
        var r = SpeakerClustering.Cluster(segs, null, new ClusteringOptions(MaxAutoClusters: 3));
        Assert.True(r.ClusterCount <= 3, $"cap violated: {r.ClusterCount}");
    }

    [Fact]
    public void Auto_is_deterministic()
    {
        var a = SpeakerClustering.Cluster(TwoVoices(), null);
        var b = SpeakerClustering.Cluster(TwoVoices(), null);
        Assert.Equal(a.ClusterBySegment, b.ClusterBySegment);
        Assert.Equal(a.ClusterCount, b.ClusterCount);
    }

    [Fact]
    public void Auto_with_single_reliable_segment_returns_one_cluster()
    {
        var segs = new List<TimedEmbedding>
        {
            new(0, 5000, V(1f, 0f)),
            new(5000, 5400, V(0f, 1f)), // bridge only
        };
        var r = SpeakerClustering.Cluster(segs, null);
        Assert.Equal(1, r.ClusterCount);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SpeakerClusteringTests"`
Expected: the six new tests FAIL with `NotImplementedException` (auto path stub); the Task 4 eight still PASS.

- [ ] **Step 3: Implement the auto path (replace the `SelectAutoCount` stub)**

```csharp
    private static (int K, int[] Assignments, float[][] Centroids) SelectAutoCount(
        IReadOnlyList<float[]> vectors, IReadOnlyList<double> weights, ClusteringOptions opt)
    {
        int maxK = Math.Min(opt.MaxAutoClusters, vectors.Count);
        if (vectors.Count < 2 || maxK < 2)
            return (1, new int[vectors.Count], [SingleCentroid(vectors, weights)]);

        (double Score, int K, int[] Assignments, float[][] Centroids)? best = null;
        for (int k = 2; k <= maxK; k++)
        {
            var fit = WeightedKMeans.Fit(vectors, weights, k);
            double score = CosineSilhouette.Weighted(vectors, weights, fit.Assignments, k);
            if (best is null || score > best.Value.Score) // tie -> smaller k (first wins)
                best = (score, k, fit.Assignments, fit.Centroids);
        }

        if (best!.Value.Score < opt.SilhouetteFloor)
            return (1, new int[vectors.Count], [SingleCentroid(vectors, weights)]);
        return (best.Value.K, best.Value.Assignments, best.Value.Centroids);
    }

    private static float[] SingleCentroid(IReadOnlyList<float[]> vectors, IReadOnlyList<double> weights)
    {
        var acc = new double[vectors[0].Length];
        double tw = 0;
        for (int i = 0; i < vectors.Count; i++)
        {
            for (int j = 0; j < acc.Length; j++) acc[j] += vectors[i][j] * weights[i];
            tw += weights[i];
        }
        var mean = new float[acc.Length];
        for (int j = 0; j < acc.Length; j++) mean[j] = (float)(acc[j] / tw);
        return WeightedKMeans.NormalizeOrNull(mean) ?? vectors[0];
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/LocalScribe.Core.Tests --filter "FullyQualifiedName~SpeakerClusteringTests"`
Expected: 14 PASS.

- [ ] **Step 5: Full gate + commit**

Run: `dotnet build LocalScribe.slnx -c Debug` (0 warnings), `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` (green).

```bash
git add src/LocalScribe.Core/Diarisation/SpeakerClustering.cs tests/LocalScribe.Core.Tests/SpeakerClusteringTests.cs
git commit -m "feat(diarisation): silhouette auto speaker-count with one-voice guard"
```

---

### Task 6: Diarizer wiring (harvest mode + orchestration)

**Files:**
- Modify: `src/LocalScribe.Core/Diarisation/DiarisationWire.cs` (add `DiarisationMethods`)
- Modify: `src/LocalScribe.Diarizer/SherpaDiarisationRunner.cs` (replace `Run` with `Harvest`)
- Modify: `src/LocalScribe.Diarizer/Program.cs` (orchestrate harvest -> embed -> cluster -> emit)

**Interfaces:**
- Consumes: `SpeakerClustering.Cluster(IReadOnlyList<TimedEmbedding>, int?, ClusteringOptions?)`, `TimedEmbedding(long StartMs, long EndMs, float[] Embedding)`, existing `SherpaEmbeddingRunner.Compute(float[]) -> float[]`, `EmbeddingSamples.Slice(float[], IEnumerable<EmbedRange>)`, `FlacPcmReader.ReadMono16k`.
- Produces: `public static class DiarisationMethods { public const string InHouseV1 = "localscribe-cluster-v1:pyannote-seg-3.0+campplus-zh-en"; }` in `DiarisationWire.cs`; `SherpaDiarisationRunner.Harvest(float[] samples16kMono, string segModelPath, string embModelPath, Action<double> onProgress) -> IReadOnlyList<EmbedRange>`.

These are humble objects (sherpa-backed, no unit tests by convention - the fixture test and Task 7 exercise them for real). The gate here is: clean build, all existing non-fixture tests green, plus a grep proving no test pins the old method string.

- [ ] **Step 1: Add `DiarisationMethods` to `DiarisationWire.cs`**

Append below the `EmbeddingMethods` class:

```csharp
public static class DiarisationMethods
{
    /// <summary>In-house clustering (design 2026-08-02): pyannote boundaries harvested via
    /// sherpa, per-segment CAM++ re-embed, weighted k-means + silhouette auto-count in Core.
    /// Provenance string - flows into speakers.json Method verbatim. Must never contain the
    /// stdout routing substrings ("progress", "error").</summary>
    public const string InHouseV1 = "localscribe-cluster-v1:pyannote-seg-3.0+campplus-zh-en";
}
```

- [ ] **Step 2: Replace `SherpaDiarisationRunner.Run` with `Harvest`**

Replace the full class body (keep the file header comment, update it):

```csharp
using LocalScribe.Core.Diarisation;
using SherpaOnnx;

namespace LocalScribe.Diarizer;

// Humble object over sherpa-onnx OfflineSpeakerDiarization, now used ONLY to harvest
// near-raw pyannote segment boundaries (in-house clustering design 2026-08-02). The tiny
// clustering threshold stops sherpa's AHC from merging, so its adjacent-same-label segment
// merge almost never fires and the emitted boundaries approximate raw pyannote segmentation;
// the cluster labels themselves are discarded. API surface confirmed by the Task 0 spike
// (docs/plans/2026-07-04-stage-5-spike-notes.md).
internal sealed class SherpaDiarisationRunner
{
    /// <summary>Cosine-distance stop threshold chosen to PREVENT merging (near-every segment
    /// keeps its own label). Only near-identical neighbouring embeddings merge, which is
    /// same-speaker by construction and therefore harmless to boundary quality.</summary>
    private const float HarvestThreshold = 0.05f;

    // MinDurationOn/MinDurationOff stay at sherpa defaults (0.3/0.5) - pinned deliberately:
    // raising MinDurationOn was measured NOT to fix the old clustering collapse, and the
    // in-house clusterer handles short segments itself (bridge attach).

    public IReadOnlyList<EmbedRange> Harvest(
        float[] samples16kMono,
        string segModelPath,
        string embModelPath,
        Action<double> onProgress)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = segModelPath;
        config.Embedding.Model = embModelPath;
        config.Clustering.Threshold = HarvestThreshold;

        using var sd = new OfflineSpeakerDiarization(config);

        OfflineSpeakerDiarizationProgressCallback cb = (processed, total, _) =>
        {
            if (total > 0) onProgress(Math.Clamp((double)processed / total, 0, 1));
            return 0;
        };

        return sd.ProcessWithCallback(samples16kMono, cb, IntPtr.Zero)
            .OrderBy(s => s.Start)
            .Select(s => new EmbedRange(
                StartMs: (long)Math.Round(s.Start * 1000),
                EndMs: (long)Math.Round(s.End * 1000)))
            .ToList();
    }
}
```

- [ ] **Step 3: Rework the diarise path in `Program.cs`**

Replace the block from `var runner = new SherpaDiarisationRunner();` through `Emit(result);` (currently lines 57-73) with:

```csharp
    // In-house clustering pipeline (design 2026-08-02): harvest near-raw pyannote boundaries
    // (labels discarded), re-embed each segment in-process, cluster in Core. Progress budget:
    // harvest 0..0.85, embedding 0.85..0.98, cluster+emit 0.98..1.0 - the old pipeline parked
    // at 1.0 during the embedding tail, which the App papered over as "Matching voices...".
    var runner = new SherpaDiarisationRunner();
    var boundaries = runner.Harvest(samples, job.SegmentationModelPath, job.EmbeddingModelPath,
        p => Emit(new DiarisationProgress(p * 0.85)));

    using var embedder = new SherpaEmbeddingRunner(job.EmbeddingModelPath);
    var timed = new List<TimedEmbedding>(boundaries.Count);
    for (int i = 0; i < boundaries.Count; i++)
    {
        var slice = EmbeddingSamples.Slice(samples, [boundaries[i]]);
        timed.Add(new TimedEmbedding(boundaries[i].StartMs, boundaries[i].EndMs,
            slice.Length > 0 ? embedder.Compute(slice) : []));
        if (i % 16 == 15)
            Emit(new DiarisationProgress(0.85 + 0.13 * (i + 1) / boundaries.Count));
    }

    var outcome = SpeakerClustering.Cluster(timed, job.ForcedClusterCount);
    var ordered = Enumerable.Range(0, timed.Count)
        .OrderBy(i => timed[i].StartMs).ThenBy(i => timed[i].EndMs)
        .Select(i => new WireSegment(timed[i].StartMs, timed[i].EndMs, outcome.ClusterBySegment[i]))
        .ToList();
    var result = new DiarisationResultPayload(ordered, outcome.ClusterCount, DiarisationMethods.InHouseV1);

    if (job.EmitEmbeddings)
    {
        var byCluster = new Dictionary<string, float[]>();
        foreach (var group in result.Segments.GroupBy(s => s.Cluster))
        {
            var sliced2 = EmbeddingSamples.Slice(samples,
                group.Select(s => new EmbedRange(s.StartMs, s.EndMs)));
            if (sliced2.Length > 0) byCluster[group.Key.ToString()] = embedder.Compute(sliced2);
        }
        result = result with { ClusterEmbeddings = byCluster, EmbeddingMethod = EmbeddingMethods.CampPlus };
    }
    Emit(new DiarisationProgress(1.0));
    Emit(result);
    return 0;
```

Note: the old standalone `if (job.EmitEmbeddings)` block that constructed its own `SherpaEmbeddingRunner` is subsumed - the pipeline's `embedder` is reused. A zero-length slice yields an empty embedding, which `SpeakerClustering` treats as a zero-norm bridge.

- [ ] **Step 4: Prove no test pins the old method string**

Run: `Grep pattern "sherpa-onnx:pyannote" path F:\LocalScribe --glob "*.cs"`
Expected: zero hits outside `docs/` after Step 2 (the only source hit was the deleted `SherpaDiarisationRunner` line). If a test pins it, update that test to `DiarisationMethods.InHouseV1`.

- [ ] **Step 5: Full gate**

Run: `dotnet build LocalScribe.slnx -c Debug`
Expected: 0 warnings. If `LocalScribe.App.exe` is running and locks bin (MSB3027), close that specific PID only.

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: all green (Core 1015 + 18 new, App 832). Nothing outside the Diarizer consumed `SherpaDiarisationRunner.Run`, and the wire payload shape is unchanged, so no App/Core test churn is expected.

- [ ] **Step 6: Commit**

```bash
git add src/LocalScribe.Core/Diarisation/DiarisationWire.cs src/LocalScribe.Diarizer/SherpaDiarisationRunner.cs src/LocalScribe.Diarizer/Program.cs
git commit -m "feat(diarisation): wire in-house clustering into the Diarizer (harvest + re-embed + cluster)"
```

---

### Task 7: End-to-end DER verification on the gold leg

**Files:**
- Create (not committed, gitignored): `tools/diar-eval/data/job_ep_auto.json`, `job_ep_f2.json`, `job_ep_f3.json`, `out_ep_*.jsonl`
- Possibly modify: `src/LocalScribe.Core/Diarisation/SpeakerClustering.cs` (`ClusteringOptions` defaults) and/or `SherpaDiarisationRunner.cs` (`HarvestThreshold`) if targets are missed.

**Interfaces:**
- Consumes: the freshly built Debug Diarizer exe (`src/LocalScribe.Diarizer/bin/Debug/net10.0-windows/LocalScribe.Diarizer.exe`), `tools/diar-eval/der.py`, `tools/diar-eval/data/reference_gold.rttm`, model paths under `F:/LocalScribe/models/`, the gold leg FLAC (path recorded in `tools/diar-eval/data/job_auto.json` from the origin measurements - reuse its `flacPath` value; NEVER write that path into a committed file).
- Produces: the authoritative DER numbers. SUCCESS CRITERIA (spec): auto DER <= 0.17 with chosen k in {2,3}; forced-2 DER <= 0.17. Stretch <= 0.12. Baselines beaten: sherpa forced-2 0.273, sherpa auto 0.593.

- [ ] **Step 1: Rebuild the Debug Diarizer (the on-disk exe may predate this work or contain sweep scaffolding)**

Run: `dotnet build src/LocalScribe.Diarizer/LocalScribe.Diarizer.csproj -c Debug`
Expected: 0 warnings, fresh `bin/Debug/net10.0-windows/LocalScribe.Diarizer.exe`.

- [ ] **Step 2: Author the three jobs**

Copy `tools/diar-eval/data/job_auto.json` to `job_ep_auto.json`, `job_ep_f2.json`, `job_ep_f3.json` (same `flacPath`, `source`, `segmentationModelPath`, `embeddingModelPath`). Edit: `job_ep_f2.json` adds `"forcedClusterCount": 2`; `job_ep_f3.json` adds `"forcedClusterCount": 3`; `job_ep_auto.json` has no forcedClusterCount.

- [ ] **Step 3: Run the three jobs + score**

Run (PowerShell, from `tools/diar-eval`):
```powershell
$exe = 'F:\LocalScribe\src\LocalScribe.Diarizer\bin\Debug\net10.0-windows\LocalScribe.Diarizer.exe'
foreach ($m in 'auto','f2','f3') {
  Get-Content "data\job_ep_$m.json" -Raw | & $exe > "data\out_ep_$m.jsonl"
  python der.py "data\out_ep_$m.jsonl" data\reference_gold.rttm
}
```
Expected: three DER lines. Record all three plus the auto run's cluster count (from the terminal line's `"clusterCount"`).

- [ ] **Step 4: Determinism check**

Re-run the auto job into `data\out_ep_auto2.jsonl`; the terminal `"segments"` lines of both runs must be byte-identical (PowerShell: compare the last line of each file).

- [ ] **Step 5: Evaluate against the success criteria**

- auto DER <= 0.17 AND auto clusterCount in {2,3}: PASS/FAIL
- forced-2 DER <= 0.17: PASS/FAIL
- forced-3 DER: record (informational - the ~2s third speaker makes this a stress case, not a gate)

If a criterion FAILS: re-run the Task 1 grid against the ACTUAL harvest boundaries - (a) edit `tune_clustering.py`'s input line to read `data/out_ep_auto.jsonl` instead of `data/out_auto.jsonl`, and (b) add cache-miss embedding exactly like `recluster_all.py`'s `embed()` helper (spawn the freshly built Debug exe with an `{"op":"embed",...}` job per missing `"<startMs>_<endMs>"` key, L2-normalize, store back into `emb_cache.json`). Then set the winning values as the `ClusteringOptions` defaults in `SpeakerClustering.cs`, rebuild, and re-run this task. Max two tuning iterations; if still failing, STOP and report the numbers to the user.

- [ ] **Step 6: Commit (only if constants changed) and report**

If `ClusteringOptions`/`HarvestThreshold` changed:
```bash
git add src/LocalScribe.Core/Diarisation/SpeakerClustering.cs src/LocalScribe.Diarizer/SherpaDiarisationRunner.cs
git commit -m "feat(diarisation): tune clustering constants against the gold reference"
```
Either way, the task report MUST state: auto DER + chosen k, forced-2 DER, forced-3 DER, determinism check result. (Rule: no success claim without the DER numbers.)

---

### Task 8: DER regression fixture (auto + forced-2)

**Files:**
- Modify: `tests/LocalScribe.Core.Tests/DiarisationFixtureTests.cs`
- Local-only (gitignored, never committed): `models/diar-fixture/leg.flac`, `models/diar-fixture/reference.rttm`, `models/diar-fixture/baseline.json`, `LocalScribe.Diarizer.exe` copied beside the test binary

**Interfaces:**
- Consumes: the existing private `FixtureProcessDiarisationHelper`, `RttmReader`, `DiarisationErrorRate` classes inside the test file (unchanged); `SherpaHelperDiariser`; `ModelPaths.Resolve/Require`.
- Produces: the permanent opt-in DER gate: one test asserting BOTH the auto path and forced-2 against a per-mode `baseline.json` (`{"autoDer": x, "forced2Der": y}`).

- [ ] **Step 1: Rework the test method**

In `DiarisationFixtureTests.cs`, rename the fixture leg from `remote.flac` to `leg.flac` (the corpus never existed anywhere, so no migration; the leg may be either side - the helper ignores `Source`). Replace the `Der_within_baseline_plus_epsilon` method body:

```csharp
    [Fact]
    public async Task Der_within_baseline_plus_epsilon()
    {
        string legPath = ModelPaths.Resolve(Path.Combine("diar-fixture", "leg.flac"));
        if (!File.Exists(legPath))
            throw new FileNotFoundException(
                "Diarisation fixture missing. Copy a real multi-speaker leg as models/diar-fixture/leg.flac (privileged, never committed).", legPath);

        string fixtureDir = Path.GetDirectoryName(legPath)!;
        string referencePath = Path.Combine(fixtureDir, "reference.rttm");
        if (!File.Exists(referencePath))
            throw new FileNotFoundException(
                "Diarisation fixture reference labels missing. Copy reference.rttm alongside leg.flac into models/diar-fixture/ (privileged, never committed).", referencePath);

        string segModel = ModelPaths.Require(
            Path.Combine("sherpa-onnx-pyannote-segmentation-3-0", "model.onnx"));
        string embModel = ModelPaths.Require(
            "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx");

        string exePath = Path.Combine(AppContext.BaseDirectory, "LocalScribe.Diarizer.exe");
        if (!File.Exists(exePath))
            throw new FileNotFoundException(
                "LocalScribe.Diarizer.exe missing beside the test binary - build src/LocalScribe.Diarizer -c Debug and copy the single .exe here (ORT isolation: never the full output folder).", exePath);

        var engine = new SherpaHelperDiariser(new FixtureProcessDiarisationHelper(exePath));
        var reference = RttmReader.Read(referencePath);

        var autoResult = await engine.DiariseAsync(
            new DiarisationRequest(legPath, SourceKind.Remote, segModel, embModel, ForcedClusterCount: null),
            new Progress<double>(_ => { }), default);
        double autoDer = DiarisationErrorRate.Compute(autoResult.Segments, reference);

        var forcedResult = await engine.DiariseAsync(
            new DiarisationRequest(legPath, SourceKind.Remote, segModel, embModel, ForcedClusterCount: 2),
            new Progress<double>(_ => { }), default);
        double forced2Der = DiarisationErrorRate.Compute(forcedResult.Segments, reference);

        string baselinePath = Path.Combine(fixtureDir, "baseline.json");
        if (!File.Exists(baselinePath))
        {
            await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(
                new { autoDer, forced2Der }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Fail($"Baseline recorded (autoDer={autoDer:F3}, forced2Der={forced2Der:F3}) - re-run to assert.");
        }

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(baselinePath));
        double autoBaseline = doc.RootElement.GetProperty("autoDer").GetDouble();
        double forcedBaseline = doc.RootElement.GetProperty("forced2Der").GetDouble();
        Assert.True(autoDer <= autoBaseline + Epsilon,
            $"auto DER regressed: {autoDer:F3} > {autoBaseline:F3}+{Epsilon}");
        Assert.True(forced2Der <= forcedBaseline + Epsilon,
            $"forced-2 DER regressed: {forced2Der:F3} > {forcedBaseline:F3}+{Epsilon}");
    }
```

Also update the class-level doc comment: the fixture is any privileged multi-speaker leg (`leg.flac`), the harness asserts auto + forced-2 per-mode baselines, and the exe comes from a fresh Debug build (not the publish runbook).

- [ ] **Step 2: Compile-only gate first**

Run: `dotnet test LocalScribe.slnx --filter "Category!=Fixture"`
Expected: green (the fixture test itself is excluded; this proves compilation and zero collateral).

- [ ] **Step 3: Populate the fixture locally (NEVER committed - models/ is gitignored)**

- Copy the gold leg FLAC (the `flacPath` recorded in `tools/diar-eval/data/job_auto.json`) to `models/diar-fixture/leg.flac`.
- Copy `tools/diar-eval/data/reference_gold.rttm` to `models/diar-fixture/reference.rttm`.
- Copy the freshly built `src/LocalScribe.Diarizer/bin/Debug/net10.0-windows/LocalScribe.Diarizer.exe` (single exe ONLY) into `tests/LocalScribe.Core.Tests/bin/Debug/net10.0-windows/`.
- Delete any pre-existing `models/diar-fixture/baseline.json`.

- [ ] **Step 4: Record + assert**

Run: `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~DiarisationFixtureTests"`
Expected first run: FAIL with "Baseline recorded (autoDer=..., forced2Der=...) - re-run to assert". The recorded numbers must match Task 7's within noise (same exe, same inputs, deterministic - they should be identical).
Run again: PASS.

- [ ] **Step 5: Verify nothing private is staged, then commit**

Run: `git status --short` - confirm NOTHING under `models/` appears and no `.flac`/`.rttm`/`.wav` file is listed.

```bash
git add tests/LocalScribe.Core.Tests/DiarisationFixtureTests.cs
git commit -m "test(diarisation): fixture gate asserts auto + forced-2 DER per-mode baselines"
```

---

## Final verification (after all tasks)

1. `dotnet clean LocalScribe.slnx && dotnet build LocalScribe.slnx -c Debug` - 0 warnings.
2. `dotnet test LocalScribe.slnx --filter "Category!=Fixture"` - Core 1015+~18, App 832, all green.
3. `dotnet test LocalScribe.slnx --filter "FullyQualifiedName~DiarisationFixtureTests"` - PASS against baselines.
4. State the final DER numbers (auto + k, forced-2, forced-3) in the completion report - no success claim without them.
5. Remaining USER decisions (do NOT act without approval): republish the Diarizer beside the App (the App still runs the stale Jul-4 exe until then); push the branch; smoke in the real app via Split Speakers.
