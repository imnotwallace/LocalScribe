# Parakeet ASR spike — design

**Date:** 2026-07-26
**Status:** approved design, pre-implementation
**Origin:** deferred spike from the Steno round (2026-07-18), user-approved. Tail of the
OpenWhispr adoption queue (voice fingerprinting, semantic search, MCP server all shipped).

## 1. Goal and framing

Decide, with measurements, whether a Parakeet-class ASR engine (Parakeet TDT v3, int8
ONNX, via sherpa-onnx) should join whisper.cpp as a second engine family on LocalScribe's
CPU live path — or whether we measure and **stop**. Non-adoption is a first-class outcome:
the spike ships a report and a reusable benchmark harness, never production changes.

**Motivating concern (settled in brainstorm):** this box (CUDA) is fine. The worry is
weaker/no-GPU target machines where the live path's CPU floor-fall lands. The spike
therefore measures under *simulated weak hardware*, not this box's native CPU.

## 2. Decisions locked during brainstorm

| Question | Decision |
|---|---|
| Evidence gate | Concern is weaker hardware; spike simulates it (forced CPU, thread caps) |
| Decision rule | **Capability-led**: latency, WER, and word-timestamps all count, weighed via pre-committed per-axis bars |
| Test audio | Real recorded sessions (realism anchor, hand-corrected ground truth) + public corpus (volume + sanity) |
| Adoption scope, if adopted | **Both from day one**: live ladder + per-import choice + re-transcription, full first-class backend |
| Languages | Whisper stays the sole detect authority; Parakeet engages only when the locked/chosen language is in its covered set |
| CPU floor default, if adopted | Parakeet **is** the new CPU floor immediately; mid-session CUDA→Parakeet falls get marker treatment like any downgrade |
| Spike approach | **A — C#-first harness via sherpa-onnx** (Python/onnx-asr held in reserve for Phase-0 triage only) |
| Hosting sketch, if adopted | New sibling helper exe `LocalScribe.Transcriber` (Diarizer pattern), persistent stdio protocol |

## 3. Decision framework

The spike produces a measurement report + recommendation. Three axes, each with a
pre-committed bar (numbers below are the agreed strawmen; adjust only before
measurement starts, never after seeing results):

**Axis 1 — Latency headroom on weak hardware.**
Per-segment turnaround (p50/p95) and RTF headroom, both engines under identical
constrained configs (forced CPU; 4-thread and 2-thread caps), jail-call-like audio
replayed at live cadence.
- *Whisper struggles* = current CPU-floor model falls behind real time (p95 turnaround >
  segment duration) or holds < ~1.5x headroom on the 4-thread config.
- *Parakeet decisive win* = >=2x whisper's throughput headroom AND still real-time with
  margin on the 2-thread config.

**Axis 2 — WER on target audio.**
Scored against hand-corrected excerpts of real sessions (~5-10 min ground truth) plus a
public telephone-degraded corpus subset.
- *Disqualifying regression* = Parakeet worse than the whisper CPU-floor model by > 1
  absolute WER point on the real-session set.
- *Decisive win* = >= 3 absolute points better on telephone-band audio.

**Axis 3 — Word-level timestamps (qualitative, evidenced).**
Overlay Parakeet word timings on a real diarised session; written assessment with
concrete examples: would word anchors materially improve split-overlay precision,
diarisation alignment, and search anchors vs today's segment-level times? No score.

**Recommendation rule:** adopt only if **at least one axis is a decisive win AND no axis
is disqualifying** (WER regression beyond tolerance, latency no better than whisper, or
instability/crashes under the harness). Anything short of that: the report says stop.
The report also carries the costed integration estimate (section 6) so a "yes" is a
costed yes.

## 4. Measurement harness

New console tool `tools/LocalScribe.AsrBench` (own csproj; references Core; **never**
referenced by App/Core; never shipped), on a spike branch.

- **Segment feeder.** Runs source audio through Core's existing VAD segmentation so both
  engines get identical utterance boundaries, then replays segments at live cadence
  (segment N offered at the wall-clock time it would end in a real session). Turnaround
  is measured from segment-available to text-returned. A batch mode (no pacing) also
  runs for raw RTF.
- **Whisper lane.** Drives the production seam directly: `WhisperEngineFactory` with a
  forced-CPU `BackendPlan` and the current CPU-floor model; thread caps applied through
  the `AutoCpuThreads` path via an override.
- **Parakeet lane.** sherpa-onnx `OfflineRecognizer` (same `org.k2fsa.sherpa.onnx`
  package the Diarizer pins at 1.13.3; version-bumped only if Parakeet TDT v3 requires
  it) loading int8 encoder/decoder/joiner + tokens. **Amended during planning
  (2026-07-26):** the lane is *always* a separate child process
  (`LocalScribe.ParakeetLane`, no Core reference) rather than in-process-by-default —
  AsrBench needs Core's Silero VAD (ORT 1.22.0 native) and sherpa bundles its own ORT
  (1.24.4); one output directory holds one `onnxruntime.dll`, so co-hosting them is
  exactly the native collision the isolation constraint forbids. The child reports
  in-engine decode time while the parent records round-trip time, so the
  process-boundary overhead the adoption hosting would pay is measured as the
  difference — no separate `--helper` mode needed, and the lane exercises the exact
  hosting shape adoption would use. Parakeet TDT is an offline model and the live path
  is segment-based, so no streaming decode is needed — Parakeet drops into the exact
  slot whisper occupies.
- **Constraint rig.** Both lanes under identical limits: thread cap (2/4/unconstrained),
  optionally a job-object CPU-rate cap ("cheap laptop" configs). Config matrix x audio
  set driven by a small JSON spec; every run emits a JSONL row (config, engine, weights
  file(s), per-segment timings, transcript) so the report is regenerable and auditable.
- **WER scorer.** In-tool: normalize -> align -> WER; per-sample and aggregate output.
  Word-timestamp dumps from the Parakeet lane feed Axis 3.
- **Model fetch.** `fetch-parakeet.ps1` beside the existing fetch-models script, with SHA
  checks; spike-only, written so adoption can promote it.

Nothing in App or Core changes on this branch except (if needed) small internal
test-seam adjustments under the existing rules. Stop semantics, floor-fall, live path:
untouched — the spike only reads production code.

## 5. Audio corpus and ground truth

Both tiers live **outside the repo** in a gitignored `bench-corpus/` directory. Real
call audio and transcripts are privileged material and must never enter a commit.

- **Tier 1 — real sessions.** User picks 2-4 representative sessions (Webex jail-call
  acoustics, telephone-band far end, crosstalk); ~5-10 min of excerpts get ground truth.
  The bench tool emits a draft transcript with the best available engine (CUDA
  large-v3-turbo) and the user corrects the draft. This set is authoritative for the
  Axis-2 disqualification bar.
- **Tier 2 — public corpus.** LibriSpeech `test-other`, run twice: clean (harness sanity
  check against both engines' published WER — if our whisper numbers diverge badly from
  published ones, the harness is broken and no downstream number counts) and
  telephone-degraded (band-limit 300-3400 Hz, mu-law compand, 8 kHz round-trip) for
  volume with exact references and zero manual work. Freely licensed true telephone
  corpora effectively don't exist (Switchboard/Fisher are LDC-licensed).
- **Output hygiene.** Per-run JSONL for Tier-1 audio stays in the gitignored corpus
  directory. The committed report quotes aggregate numbers and, at most, short quality
  examples from *public* audio — never real-call content.

## 6. Adoption shape (priced by the report, not built by the spike)

- **Hosting.** New sibling helper `LocalScribe.Transcriber` — Diarizer pattern: own
  csproj, own sherpa-onnx/ORT pinned independently of Core's ORT 1.22.0, published
  self-contained beside the app. Unlike the Diarizer's batch invocation it is a
  long-lived child process with a persistent stdio protocol (segment in -> text + word
  timings out), because live cannot pay process-start per utterance. The `--helper`
  measurement validates affordability. Publish layout gains `transcriber\`; the layout
  guard grows accordingly.
- **Engine seam.** `ParakeetHelperEngine : ITranscriptionEngine` + factory behind
  `IEngineFactory`. `TranscriptionWorker`, one-engine-at-a-time, `PendingFinalize`, Stop
  semantics all untouched; Parakeet is another engine the worker recreates on
  lock/downgrade, exactly like a whisper model swap.
- **Provenance.** Engine family becomes explicit: session/segment/engine records gain an
  `engine` field (`whisper`/`parakeet`) alongside `WeightsFile`. Parakeet is multi-file,
  so the bijection invariant *extends*: one canonical name
  (`parakeet-tdt-0.6b-v3-int8`) <-> one file-set manifest (encoder/decoder/joiner/tokens
  + hashes); `WeightsFile` records the manifest name; `ModelFileResolver` learns
  file-set entries. A mid-session fall that lands on Parakeet emits the
  position-correct marker mechanism extended to say *engine* changed, not just weights.
- **Ladder & languages.** CPU rung becomes family-aware: locked language in Parakeet's
  covered set -> Parakeet is the floor; otherwise whisper q8_0 as today. Whisper remains
  sole detect authority (probe-then-commit unchanged). Import picker gains a Parakeet
  entry, eligible once the probe/explicit language is covered.
- **Coexistence.** Nothing retroactive: existing sessions keep their provenance forever.
  Re-transcribing a whisper-cut session on Parakeet is a new `TranscriptVersion` with the
  new engine's provenance; old versions immutable. The versioning machinery already
  models this.

## 7. Phasing and deliverables

- **Phase 0 — feasibility gate (hours).** sherpa-onnx C# loads Parakeet TDT v3 int8 and
  transcribes one WAV correctly under the Diarizer's package version (bump only if
  required). On structural failure: a quick Python `onnx-asr` check solely to attribute
  the blocker (sherpa's vs Parakeet's), then report and stop. No harness on a dead
  engine.
- **Phase 1 — harness + corpus.** Bench tool (feeder, two lanes, constraint rig, WER
  scorer), `fetch-parakeet.ps1`, degradation pipeline. User picks Tier-1 sessions and
  corrects drafted excerpts (only user-blocking step; overlaps harness work). The WER
  scorer and cadence-replay logic get real unit tests (load-bearing for every reported
  number); the rest of the tool is spike-grade.
- **Phase 2 — sanity, then measurement.** Clean-LibriSpeech sanity run first; then the
  full matrix (2 engines x thread configs x corpus tiers x cadence/batch) plus the
  `--helper` overhead run. Engine crashes/hangs during the matrix are recorded as
  findings — instability is disqualifying evidence for Axis 1, not a harness bug to
  quietly retry around (harness bugs get fixed; engine failures get counted).
- **Phase 3 — word-timestamp assessment.** Overlay word timings on a real diarised
  session; written assessment against splits/alignment/search with concrete examples.
- **Phase 4 — report.** `docs/spikes/<run-date>-parakeet-spike-report.md`: per-axis results
  vs the pre-committed bars, recommendation, and the section-6 sketch priced into tasks.

**Stop path:** on a "stop" verdict the branch still merges — harness, fetch script, and
report land on master so re-evaluating the next Parakeet-class model costs a corpus run,
not a rebuild. The `parakeet-onnx-cpu-spike-stub` memory is closed out with the verdict
either way. Adoption, if recommended, is a separate future round with its own SDD plan;
this spike ships no production changes.

## 8. Constraints (unchanged from project rules)

- Fully local; no cloud, no telemetry. Privileged audio/transcripts never committed.
- Native isolation: Core stays on ORT 1.22.0; any Parakeet ORT surface lives in the
  bench tool now and a dedicated helper later — never in Core.
- Evidentiary integrity: any adopted backend participates in provenance visibly; engine
  switches never silently alter or re-transcribe existing sessions.
- No regression to live-path Stop semantics or CPU floor-fall behaviour.
