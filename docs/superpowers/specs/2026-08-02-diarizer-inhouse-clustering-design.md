# Diarizer in-house clustering (ITEM 4: diarisation quality)

Date: 2026-08-02
Status: approved (design brainstorm 2026-08-02); supersedes the clustering portion of the
stage-5 design's sherpa FastClustering usage. All other stage-5 diarisation decisions stand.

## Problem

sherpa-onnx's built-in FastClustering (AHC) collapses cleanly separable CAM++ speaker
embeddings. Measured on a real 21-minute single-mic interview leg (the gold 3-speaker
fixture session; all identifying data stays out of the repo, see Privacy):

- Forced 2 clusters: 94%/6% collapse (true split is ~19%/81%). DER 27.3%.
- Auto (threshold 0.7): 51 clusters. DER 59.3%.
- The same embeddings, in-house duration-weighted k-means(2): 19.7%/80.3%. DER 16.4%.
- CAM++ separability on clean single-speaker probes: within-speaker cosine distance ~0.25,
  between ~0.73 (ratio 2.93), unsupervised 2-means 100% purity.

So the embeddings and the pyannote segmentation are fine; the clustering stage is the
defect, and it is not threshold-tunable (MinDurationOn sweeps do not help; sherpa 1.13.3
exposes only Clustering {NumClusters, Threshold} + MinDurationOn/Off). Short "bridge"
segments (median 0.5 s, too little audio for a stable CAM++ embedding) chain the AHC
merges. The miss+false-alarm floor of the pyannote boundaries themselves is ~8.1% DER,
so clustering quality is the confusion term.

## Decision summary (user-approved 2026-08-02)

1. Boundaries: harvest near-raw pyannote boundaries from a sherpa run configured not to
   merge (tiny clustering threshold), discard its labels.
2. Auto speaker count: silhouette scan k=2..6 over the same weighted k-means the forced
   path uses, computed on reliable segments only; weak-separation guard returns 1.
3. Bridges: two-pass — cluster reliable segments only, attach bridges to the nearest
   centroid afterwards.
4. DER regression gate: populate the existing privileged models/diar-fixture/ corpus
   locally (models/ is gitignored; the corpus is NEVER committed) and extend
   DiarisationFixtureTests.

## Architecture

Pipeline inside LocalScribe.Diarizer.exe: harvest -> re-embed -> cluster in-house -> emit.

Everything outside the exe is untouched: same stdin job JSON (DiarisationJob), same
stdout contract (zero or more {"progress":p} lines then exactly one terminal line), same
error codes (MODEL_MISSING / BAD_AUDIO / HELPER_CRASH), same exit semantics. Core and App
(SherpaHelperDiariser, ClusterAssigner, SpeakersMerge, SplitSpeakersViewModel,
SpeakerDetectionStep, the fixture test's hand-copied spawn protocol) need no changes.

Pure clustering logic lives in src/LocalScribe.Core/Diarisation (new SpeakerClustering.cs),
following the FlacPcmReader/EmbeddingSamples pattern: the Diarizer already references Core,
and LocalScribe.Core.Tests covers the logic model-free (no new test project, no
onnxruntime-collision hazard). The Diarizer keeps only the sherpa humble objects
(SherpaDiarisationRunner, SherpaEmbeddingRunner) plus orchestration in Program.cs.

## Components

### 1. Boundary harvest (SherpaDiarisationRunner)

- Run OfflineSpeakerDiarization once with Clustering.Threshold = HarvestThreshold (a tiny
  epsilon, e.g. 0.05f; exact value DER-tuned) so AHC almost never merges: nearly every
  pyannote segment keeps its own label and the adjacent-same-label merge that destroys
  boundaries almost never fires. Only near-identical neighbouring embeddings merge, which
  is same-speaker by construction and therefore harmless.
- The harvest run's cluster labels are discarded; only (StartMs, EndMs) boundaries survive.
- ForcedClusterCount no longer reaches sherpa; forced k is applied in-house.
- MinDurationOn/MinDurationOff stay at sherpa defaults (0.3/0.5), pinned in a comment.

### 2. Per-segment embedding (SherpaEmbeddingRunner, in-process)

- One CAM++ extractor loaded per job; one Compute per harvested segment over the already
  decoded in-memory 16 kHz samples. No FLAC re-decode, no process spawns. (~218 segments
  on the measured leg; cheap next to 21 minutes of segmentation.)
- Accepted cost: sherpa embedded segments internally during the harvest and does not
  expose them, so embedding happens twice per job.

### 3. In-house clustering (Core: SpeakerClustering, pure + deterministic)

All distances are cosine on L2-normalized vectors. No RNG anywhere.

- Reliable vs bridge: reliable = duration >= ReliableMinMs (1000 ms initial, DER-tuned).
  If fewer than max(2, forced k) reliable segments exist, the bar drops and all segments
  count as reliable.
- Forced k: duration-weighted k-means over reliable segments. Deterministic init: first
  centroid = the longest reliable segment's embedding, then farthest-first for the rest.
  Hard assignment, duration-weighted centroid update, re-normalize, fixed-point stop,
  200-iteration cap. k clamps to the reliable-segment count (the App tolerates
  fewer-than-forced clusters; SpeakerDetectionStep documents this).
- Auto (forcedClusterCount == null): run the same k-means for each k in
  2..min(6, reliableCount); pick the k with the best duration-weighted mean cosine
  silhouette over reliable segments. One-voice guard: if the best silhouette <
  SilhouetteFloor (0.2 initial, DER-tuned), return 1 cluster — the import path's OneVoice
  guard (commit nothing, write marker) depends on auto being able to say this honestly.
- Bridge attach: each bridge segment is assigned to the nearest centroid. A degenerate
  (zero-norm) embedding falls back to the temporally nearest reliable segment's cluster.
- Renumbering: final cluster ids are contiguous, 0-based, ordered by first temporal
  appearance (speaker 0 = first to talk). Deterministic; default labels
  ("Local Speaker 1", ...) come out in speaking order.
- Every tie-break is defined: lower segment index / earlier start wins; silhouette ties
  prefer the smaller k.

### 4. Emission (Program.cs)

- Same DiarisationResultPayload: segments ordered by StartMs, clusterCount = count of
  DISTINCT ids (never max+1), cluster ids are the raw ints Core namespaces into
  "<Source>:<id>".
- emitEmbeddings path byte-identical to today: group final segments by cluster, slice up
  to 30 s (EmbeddingSamples.MaxSecondsPerCluster), one CAM++ mean per cluster keyed by the
  bare id string, embeddingMethod "campplus-zh-en". Existing voiceprint enrollments remain
  comparable (VoiceprintMatcher is same-method gated).
- New Method string: "localscribe-cluster-v1:pyannote-seg-3.0+campplus-zh-en". Provenance
  flows into speakers.json verbatim; the string contains none of the stdout routing
  substrings ("progress", "error").
- Progress re-budget: harvest maps to 0..0.85, per-segment embedding 0.85..0.98 (emit
  every few segments), clustering 0.98..1.0. Values stay in 0..1; the App needs no change,
  and the current "stuck at 100% while matching voices" artifact goes away.

## Error handling and degenerate inputs

No new error codes. Model-missing and bad-audio checks run before the harvest exactly as
today; unexpected exceptions stay HELPER_CRASH (exit 1). Degenerate cases, all
deterministic:

- Zero harvested segments -> empty segments list, clusterCount 0 (the import <=1-cluster
  guard commits nothing; Split Speakers shows zero rows).
- One segment -> one cluster.
- Forced k > available segments -> as many clusters as segments.
- Cancellation remains kill-the-process-tree from the App; no timeout exists, which the
  progress re-budget mitigates by never parking stdout for long.

## Testing and DER evaluation

- TDD unit tests in LocalScribe.Core.Tests (model-free, synthetic embeddings, house
  conventions: flat files, snake-case fact names, ASCII only, 0 warnings):
  k-means determinism and duration weighting, farthest-first init, silhouette selection
  including the one-voice guard and the 6-cap, bridge attach including the zero-norm
  fallback, renumbering by first appearance, clamping and degenerate cases.
- Offline tuning before wiring: a python mirror of the algorithm runs against the cached
  per-segment embeddings + gold RTTM (both in gitignored local folders) to tune
  HarvestThreshold, ReliableMinMs, and SilhouetteFloor in seconds per candidate. The
  authoritative number is always the C# end-to-end run: freshly built Debug Diarizer ->
  der.py against the gold reference. No "fixed" claim without that DER in the same
  message. (The published Diarizer.exe is a stale Jul-4 build; never measure with it.
  Republishing beside the App is HELD pending explicit user approval.)
- Numbers to beat (measured 2026-08-02 on the gold leg): sherpa forced-2 = 27.3%, sherpa
  auto = 59.3%, offline k=2 demo = 16.4%, boundary miss+FA floor ~= 8.1%.
  Success criteria: auto DER <= 17% with k in {2,3} chosen; forced-2 DER <= 17%.
  Stretch: <= 12%.
- Permanent regression gate: populate models/diar-fixture/ locally (models/ is gitignored;
  the corpus and baselines are NEVER committed) with the gold leg audio + reference RTTM,
  and extend DiarisationFixtureTests ([Trait Category=Fixture], FileNotFoundException with
  instructions when absent, baseline.json auto-record, epsilon 0.05) to assert both the
  auto path and one forced-count run against per-mode baselines.

## Privacy (hard constraint)

Nothing derived from the gold session is ever committed: fixture corpus under gitignored
models/, eval data under gitignored tools/diar-eval/data/ (with belt-and-braces ignore
patterns for *.rttm/*.wav/*.flac/embedding caches inside tools/diar-eval/). This spec and
all commits reference "the gold 3-speaker fixture session" only — no personal names, no
case details, no transcript content. The gold's measured shape, for engineering context
only: three speakers at roughly 80.5% / 19.3% / 0.2% of labelled speech, 311 turns,
~21.7 minutes.

## Out of scope

- Republishing the Diarizer beside the App (HELD; user decides at the end).
- Remote-leg or joint-leg clustering changes (legs stay independently clustered).
- Any change to the embedding model or EmbeddingMethod string, ClusterAssigner,
  SpeakersMerge, SaveDiarisationAsync/RenameSpeakersAsync, or the wire record shapes.
- Streaming/live diarisation, background job queues, run timeouts/watchdogs.
- The twice-deferred missing-exe Win32Exception hardening in ProcessDiarisationHelper.
