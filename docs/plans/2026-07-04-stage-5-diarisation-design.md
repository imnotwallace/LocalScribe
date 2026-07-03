# Stage 5 Design: Split-speakers (On-demand Diarisation)

Date: 2026-07-04
Status: Approved (brainstorm dialogue 2026-07-04); supersedes nothing - refines the
master design (docs/plans/2026-06-30-localscribe-design.md, build-sequence step 5)
and the specs (docs/specs/localscribe-specs.md sections 1.3/1.4/6.1/10) with the
decisions recorded below.

## 1. Scope

Stage 5 delivers on-demand speaker diarisation - "Split speakers" - the last v1
capability gap. Live capture already labels turns structurally as local ("me") vs
remote ("them"); Stage 5 refines a source leg that carries more than one declared
speaker (the mixed remote leg is the primary case) into distinct, user-named
speakers, computed post-hoc from the retained per-source FLAC and stored as a
non-destructive overlay.

Delivered in Stage 5:

- `IDiarisationEngine` seam + a process-isolated sherpa-onnx adapter that clusters
  a single source leg into speaker segments.
- `LocalScribe.Diarizer.exe`: a new out-of-process helper that owns the sherpa-onnx
  NuGet (and therefore its own ONNX Runtime), decodes the FLAC leg, runs
  segmentation + embedding + clustering, and streams progress + results as JSON.
- The Split-speakers dialog: source selection, run-with-progress-and-cancel, and a
  per-cluster naming step with transcript preview and audio snippet playback.
- `speakers.json` v1 written through the existing `SpeakersStore`, preserving pinned
  manual reassignments verbatim; `session.json` `Diarised` flag flipped; projections
  regenerated - all in one `MaintenanceService` gate hold.
- SHA-pinned Apache/MIT-only models fetched by an extended `tools/fetch-models.ps1`.
- Model-free Core/App unit tests plus an opt-in (`Category=Fixture`) DER regression
  harness.

### User decisions (2026-07-04)

| Question | Decision |
|---|---|
| Stage 5 identity | Split-speakers diarisation (canonical roadmap stage), not export/editing/quality-baseline. |
| Automation level | Strictly manual. No "diarise now?" nudge; the dialog is launched by the user. |
| Progress UX | In-dialog progress bar + Cancel; the user stays in the dialog until results appear or they cancel. No background job queue. |
| Cluster identification | Per-cluster transcript preview PLUS a play button that seeks a representative snippet from the aligned FLAC leg. |
| Integration architecture | Approach D: sherpa-onnx NuGet inside a dedicated out-of-process helper `.exe`, behind a humble-object seam. |
| Embedding model | 3D-Speaker `campplus_sv_zh_en_16k-common_advanced` (Apache-2.0, non-VoxCeleb) as default, to sidestep the unsettled VoxCeleb/CC-BY-4.0 dataset-license question. |

### 1.1 Why process isolation (the load-bearing constraint)

LocalScribe.Core already loads **Microsoft.ML.OnnxRuntime 1.22.0 in-process** for
Silero VAD on the live capture path (`LocalScribe.Core.csproj:18`;
`SileroVadModel.cs:17` constructs a bare `new InferenceSession(onnxPath)`). The
sherpa-onnx Windows NuGet ships its **own** native `onnxruntime.dll` - version
**1.24.4** for the current release (1.13.3) - and `sherpa-onnx-c-api.dll` imports
`onnxruntime.dll` **by name**, so exactly one `onnxruntime.dll` instance can serve a
process. Referencing the sherpa NuGet in-process therefore collides with the host's
1.22.0 runtime:

- Both packages emit a same-named `onnxruntime.dll` to the same output path; which
  one wins the MSBuild/NuGet file conflict is non-deterministic and SDK-version
  dependent (silent winner or a `NETSDK1152` duplicate-publish error).
- If Microsoft's 1.22.0 DLL wins, sherpa (built against 1.24.4) fails at init with an
  ORT C-API version-range error.
- If sherpa's custom 1.24.4 DLL wins, **Silero VAD - the live evidentiary capture
  path - would run on an unvetted, non-Microsoft ORT build.**

This is documented upstream (sherpa-onnx issue #1771) with no supported side-by-side
story. Running sherpa in a **separate process** gives it its own `onnxruntime.dll`
and eliminates the collision entirely, leaving the capture path on pristine 1.22.0.
This constraint is the reason the design is out-of-process rather than a simple
in-proc adapter; it is not negotiable without a build spike that proves in-proc
coexistence safe (see Task list, Section 8).

Process isolation also happens to solve two other problems: sherpa's C# diarisation
API has **no cooperative cancellation** (the progress callback's return value is
ignored upstream), so killing the child process is the only clean mid-run abort; and
a native fault in diarisation cannot take down the evidentiary app.

### 1.2 Non-goals (unchanged from master design unless noted)

- No live/streaming diarisation - locked non-goal; Stage 5 is strictly post-hoc,
  on-demand.
- No cross-session acoustic voiceprints - hard non-goal; roster reuse is name
  metadata only, no audio identity crosses sessions.
- No "diarise now?" nudge or any auto-run (user decision: strictly manual).
- No background diarisation job queue (user decision: in-dialog run).
- No production model-download UX or first-run bundled model - Stage 7;
  `tools/fetch-models.ps1` stays dev-only tooling.
- No content deletion, redaction, hiding, or reordering - the evidentiary invariant
  (Section 7) holds absolutely.
- No changes to the live capture, VAD, or transcription paths.
- Diarisation does not gate, block, or alter the machine-original transcript;
  speaker names are an additive overlay.

## 2. Architecture and components

### 2.1 The helper process - `LocalScribe.Diarizer.exe` (new project)

A small console executable, `src/LocalScribe.Diarizer/`, that is the **only** code in
the solution referencing `org.k2fsa.sherpa.onnx` 1.13.3 (+ the win-x64 / win-arm64
runtime packages). It never touches `Microsoft.ML.OnnxRuntime`. Responsibilities:

1. Read a single job spec (paths to the FLAC leg, the two model files, requested mode
   - auto vs forced-count-N) as JSON on stdin (or argv; stdin preferred to keep model
   paths off the process command line).
2. Decode the FLAC leg to `float[]` via `CUETools.Codecs.FLAKE.FlakeReader` (the same
   LGPL assembly already shipped for encoding; pure-managed, ARM64-safe, a
   same-library round-trip with the `FlakeWriter` that wrote the file). Assert
   `SampleRate == 16000` and mono before feeding sherpa.
3. Configure and run `OfflineSpeakerDiarization` (pyannote segmentation + CAM++
   embedding + fast clustering), mapping sherpa's `numProcessedChunks /
   numTotalChunks` progress callback to newline-delimited `{"progress": <0..1>}`
   lines on stdout.
4. Emit a final `{"segments":[{"startMs":..,"endMs":..,"cluster":<int>}],
   "clusterCount":<int>, "method":"sherpa-onnx:pyannote-seg-3.0+campplus-zh-en"}`
   object, then exit 0. The `method` key maps 1:1 onto `DiarisationResult.Method` and
   `speakers.json` `Method`. On any failure, emit `{"error":"<code>","detail":"<msg>"}`
   and exit non-zero, where `<code>` is one of `MODEL_MISSING`, `BAD_AUDIO`,
   `HELPER_CRASH` (the seam maps `MODEL_MISSING` onto the user-facing
   `MODEL_DOWNLOAD_FAILED`, Section 5).

The helper is deliberately dumb: no storage, no schema, no soft-prior policy of its
own beyond "auto threshold" vs "force N clusters". It is a pure audio-in /
segments-out worker. Its stdout JSON contract is **owned by us** (not a
reverse-engineered CLI format), which is the reason to build a helper rather than
shell out to sherpa's stock CLI.

### 2.2 The Core seam - `IDiarisationEngine` + `SherpaHelperDiariser`

In `LocalScribe.Core` (WPF-free, humble object over the native/ML touch):

```
public interface IDiarisationEngine
{
    Task<DiarisationResult> DiariseAsync(
        DiarisationRequest request,
        IProgress<double> progress,
        CancellationToken ct);
}

public sealed record DiarisationRequest(
    string FlacPath,
    SourceKind Source,
    string SegmentationModelPath,
    string EmbeddingModelPath,
    int? ForcedClusterCount);   // null = auto (threshold); N = force exactly N

public sealed record DiarisationResult(
    IReadOnlyList<DiarisedSegment> Segments,   // startMs, endMs, cluster
    int ClusterCount,
    string Method);
```

`SherpaHelperDiariser` locates `LocalScribe.Diarizer.exe` beside the app, spawns it,
writes the job spec to stdin, parses stdout line-by-line (progress vs final),
forwards progress to `IProgress<double>`, and on `ct` cancellation **kills the child
process** and throws `OperationCanceledException`. Non-zero exit or an `{"error":...}`
line becomes a typed `DiarisationException` carrying the code (`MODEL_MISSING`,
`BAD_AUDIO`, `HELPER_CRASH`); the App layer maps `MODEL_MISSING` to the spec's
user-facing `MODEL_DOWNLOAD_FAILED` (Section 5). No sherpa or ORT type leaks past this
seam; Core does not reference the sherpa NuGet.

This `IDiarisationEngine` shape (a `DiarisationRequest` over a retained FLAC leg with
`IProgress`/`CancellationToken`) deliberately supersedes the master design's earlier
`DiariseAsync(segments, options)` / `SherpaOnnxDiariser` sketch, which predated the
process-isolation decision; the spec/master amendment (Task 10) records the change.

A fake `IDiarisationEngine` drives all App/dialog and Core-orchestration tests; the
real helper is exercised only in the opt-in fixture test (Section 6).

### 2.3 Models

| Role | Model | License | Size | Source |
|---|---|---|---|---|
| Segmentation | `sherpa-onnx-pyannote-segmentation-3-0` | MIT (pyannote/segmentation-3.0, CNRS) | ~6.6 MB tar | k2-fsa release `speaker-segmentation-models` |
| Embedding | `3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced` | Apache-2.0 (3D-Speaker) | ~27 MB | k2-fsa release `speaker-recongition-models` |

Both operate at 16 kHz mono, matching the FLAC legs exactly. The embedding model is
the non-VoxCeleb bilingual CAM++ (user decision), chosen to avoid the unsettled
question of whether VoxCeleb-trained weights inherit the dataset's CC-BY-4.0 license.
The English-only VoxCeleb CAM++ is the higher-accuracy alternative but is **not** the
default; the `DiarisationRequest` carries explicit model paths, so swapping the
embedding model later is a config change, not a code change.

SHA-256 values will be pinned in the implementation plan (Task 1) and verified at
fetch time. The embedding release ships a vendor `checksum.txt`; the pyannote
segmentation release publishes no vendor hash, so its pin is a self-computed SHA-256
of the tarball recorded once in the plan. GitHub release assets under a reused tag are
mutable, so the pin is authoritative and fetch fails closed on mismatch (do not trust
`checksum.txt` fetched at download time - it lives in the same mutable release). The
MIT LICENSE text bundled in the pyannote tarball must be carried into the app's
third-party notices (attribution obligation).

`tools/fetch-models.ps1` gains two `@{ Name; Url; Sha256 }` entries and grows real
SHA verification (fail-closed on mismatch) for the diarisation models. Production
download UX (retry, resumable, in-app) remains Stage 7. Runtime model discovery for
whisper only enumerates `ggml-*.bin`, so dropping `.onnx` files in `models/` does not
pollute the transcription model picker.

## 3. Data model and write path

### 3.1 `speakers.json` v1 (schema already exists; Stage 5 is its first writer)

`SpeakersStore` and the `Speakers` record already exist (`SchemaVersion`, `Names`
clusterKey->display, `Assignments` source->seq->clusterKey, `Pinned` source->seq
list, `DiarisedSources`, `Method`, `DiarisedAtUtc`, `Confidence`). Stage 5 populates
them for the first time. Clustering is **per source, independent** - Local and Remote
are never clustered jointly. Cluster keys follow the existing `<prefix>:<clusterId>`
convention that `NameResolver` already parses.

**Cluster key namespacing and default labels.** The helper returns a 0-based cluster
integer per source. Core maps it to a clusterKey `<source>:<clusterId>` where
`<source>` is `Local` / `Remote` (so Local and Remote cluster spaces never collide)
and `<clusterId>` is the raw helper integer. Every produced cluster gets its default
display name **materialised into `Names`** at commit time - `Local Speaker 1`,
`Remote Speaker 1`, ... (1-based, per-side prefix). Defaults are always written
explicitly; the code never leans on `NameResolver`'s bare `Speaker <suffix>` fallback
for a diarised cluster (that fallback is 0-based off the raw suffix and would render
`Speaker 0` where the dialog showed `Speaker 1`, and would collide `Local:0` with
`Remote:0`). This makes the dialog label, the stored name, and every projection agree.

**Pinned preservation.** Manual per-segment speaker reassignments (Stage-6-adjacent,
but the mechanism exists today via `EditStore.ReassignSpeakerAsync`) are recorded in
`Pinned`. A (re-)diarisation run **must** load the existing `speakers.json`, keep
every pinned seq's assignment verbatim, and only (re)write non-pinned assignments for
the sources being diarised. This is the evidentiary non-destructive guarantee at the
speaker-overlay layer.

**No name rebinding across runs.** Cluster IDs are *not* stable across diarisation
runs (clustering is non-deterministic and cluster order can shift), so a stored
`Names['Remote:0'] = 'Alice'` must never be silently reused for whatever lands in
cluster 0 on a re-run - that would misattribute evidence. On re-diarise, Core
therefore **drops every non-pinned `Names` entry for the sources being re-diarised**
before writing the new run's default labels. Any prior name the user wants to keep is
re-surfaced only as a *suggestion* in the dialog, computed by matching each new
cluster to the previous run's cluster with the greatest overlap of shared `seq`s (a
best-effort hint the user re-confirms), never as an automatic binding. Pinned per-seq
assignments (and their effective names) are always preserved verbatim.

### 3.2 The single-gate write

The diarisation commit runs inside one `MaintenanceService.RunForSessionAsync(id,
...)` hold (the app's single-flight per-session write seam, so it cannot interleave
with an open read-view load or another mutation):

1. Load existing `speakers.json` (or empty), merge: set `Assignments` for the
   diarised sources from the mapped clusters, preserve `Pinned` verbatim, drop
   non-pinned `Names` for the re-diarised sources (see "No name rebinding" above),
   then materialise `Names` for every produced cluster to the user's typed name or the
   per-side default (`Local Speaker N` / `Remote Speaker N`), set `DiarisedSources`,
   `Method`, `DiarisedAtUtc`. `Confidence` is **left empty** in v1 - the sherpa
   helper produces no per-cluster confidence value, and spec 1.3 marks the field
   optional; do not fabricate one. (If a confidence source is added later it must flow
   through the helper JSON contract and `DiarisationResult` first.)
2. Save `speakers.json` via `SpeakersStore` **first** (it is the source of truth).
3. Rewrite `session.json` with `Diarised = true` (the list badge already binds this;
   nothing writes it today). Ordering matters: write `speakers.json` before flipping
   `Diarised`, so a crash between steps never advertises a diarisation whose overlay
   did not land; the flag and projections are always re-derivable from `speakers.json`.
4. `RegenerateProjectionsAsync` so `transcript.md/.txt/session.txt` re-render with
   resolved names, following the canonical apply order (Section 7).

The write happens **only on success/confirm**. Cancel or helper failure leaves all
session files untouched. Diarisation does **not** flip `meta.json` `Edited` /
`LastEditedAtUtc` - those are reserved for manual human corrections; a machine
diarisation pass is not an "edit" in that sense. (Pinned manual reassignments made
later still route through `EditStore` and do flip `Edited`, unchanged.)

**No-delete firewall (evidentiary).** The specs reserve an `afterDiarisation` audio
retention seam whose documented trigger is exactly the speaker-map confirm/lock this
stage introduces. Stage 5 **does not wire it.** The diarise commit performs **no**
audio deletion, per-source removal, or retention cleanup of any kind, regardless of
the session's `AudioRetention` value; `MaintenanceService.DiariseAsync` must never
call `SessionDeleter` or any per-source audio removal. This is load-bearing: the
retained legs are primary evidence (permanent-keep is the default and auto-deletion is
never exposed), and re-diarising (Section 4.3) and cluster-snippet playback both
depend on the legs staying on disk. An implementer following the spec's
`afterDiarisation` note literally must be stopped by this clause.

### 3.3 Mapping clusters to transcript segments

The helper returns time segments (`startMs/endMs/cluster`) over the source leg. Core
maps each transcript `seq` (which carries `startMs/endMs` and a source) to the
diarised cluster with the **greatest time overlap** with that seq's span. Ties (equal
overlap with two clusters) break deterministically toward the **lower clusterId**, so
the mapping is reproducible. A seq that **no** cluster overlaps (a VAD gap the
diariser left unassigned, or a boundary-straddling seq) is **left unassigned** in
`Assignments` - it must not be forced into a cluster. `NameResolver` already falls
back cleanly for an unassigned seq to the single-participant / structural me-them
label (verified fallback path), so uncovered seqs render as they do today; the mapping
code must not assume full coverage.

`AlignedAudioWriter` guarantees a frame stamped `StartMs` begins at sample
`StartMs * sampleRate / 1000` (`sampleRate` is the writer's injected rate, 16000 for
our legs; gaps silence-padded), so transcript time and FLAC time share one axis;
sub-frame drift is accepted and irrelevant at diarisation-window granularity.

## 4. The Split-speakers dialog

A capture-excluded (`WDA_EXCLUDEFROMCAPTURE`, like every transcript-bearing window)
dialog window, owned by the launching read view or main window, MVVM with a WPF-free
view model driven by `IDiarisationEngine`, injected dispatch, and `TimeProvider`.

### 4.1 Entry points and gating

- **Read-view toolbar** (primary): a "Split speakers..." button. The read view is
  display-only today; this is its first affordance.
- **Sessions-page context menu**: a fourth item alongside Open / Reveal / Archive.

A source is **splittable** when all three hold: the session is finalized or recovered
(`EndedAtUtc != null`); that source's declared count (`meta.json` `LocalCount` /
`RemoteCount`) is **> 1**; and that source has a retained FLAC leg on disk (it appears
in `session.json` `RetainedAudioSources` and the file probes present, mirroring
`PlaybackViewModel.Resolve`). The entry points (button / context-menu item) are
enabled only when **at least one** source is splittable; otherwise they are disabled
with a reason tooltip (not finalized / no side declares >1 speaker / audio not
retained). This honours the locked spec gate: a side declared as a single participant
has no Split pass, and there is **no "diarise anyway" path** for a default 1/1 session
(counts default to 1/1, not "unset"; if the user believes a side has more speakers,
they raise its declared count in the metadata editor first).

### 4.2 Flow

1. **Source selection.** Only splittable sources (per 4.1) are offered - Local,
   Remote, or both. A count-1 side is not shown; a count->1 side with no retained leg
   is not shown. Because selection is already restricted to sources with a retained
   leg, the run cannot hit a "no audio" failure for a selected source.

2. **Run.** Progress bar (from the helper's chunk progress) + Cancel. Declared count
   is a **soft prior**: the first pass runs sherpa in **auto (threshold)** mode. If
   the auto cluster count matches that source's declared count, proceed. If it
   diverges, the dialog surfaces both numbers and offers a one-click **"Use N
   speakers"** re-run that forces `NumClusters = N` (sherpa exposes only a hard forced
   count, so the soft-prior behaviour is ours: auto first, forced re-run on demand).
   **Force-N is offered only for clean per-process legs** (Webex/Zoom per-process
   capture). For a **system-mix** leg (predicate below) force-N is suppressed with a
   note - compelling the diariser to collapse into exactly N clusters would merge
   non-meeting/background audio into a legitimate named speaker; over-clustering
   (junk kept as separate, un-named clusters the user simply ignores) is the safer
   outcome there. Cancel kills the helper; nothing is written.

3. **Name.** Each discovered cluster is listed as `Local Speaker 1` /
   `Remote Speaker 1`, ... (the default label that will be stored, Section 3.1) with:
   - a few representative utterances from the transcript (text preview), and
   - a **play button** that plays a short representative snippet from that cluster's
     time span on the source FLAC leg (seek by `startMs`, using the existing dual
     audio player path; decode is off the UI thread).

   The user types a name per cluster (blank = keep the default `... Speaker N` label)
   and confirms/locks. Confirm triggers the Section 3.2 single-gate write. To
   **ignore** a spurious cluster the user simply leaves it at its default label: its
   segments stay in the transcript and every projection exactly as diarised - "ignore"
   means "do not name", never hide/exclude/redact (Section 7 invariant).

   A **system-mix** source shows a banner ("this leg may contain non-meeting audio, so
   some clusters may be spurious - name or leave them"). The detection predicate is the
   existing repo flag: `Devices.Remote.Mode == RemoteMode.SystemMix ||
   Devices.Remote.FellBackToSystemMix` (the `SystemMix` property already computed in
   `ReadViewViewModel` / `SessionRowViewModel`), so the common Teams/browser
   auto-fallback case is covered, not just an explicit system-mix selection.

### 4.3 Re-diarising

Running Split speakers again on an already-diarised (still-retained) session is
allowed and non-destructive: pinned reassignments are preserved verbatim, and prior
names are re-surfaced only as best-effort suggestions matched by shared-`seq` overlap
(Section 3.1, "No name rebinding") which the user re-confirms - names are never
silently rebound to a shifted cluster. The `Diarised` badge and `DiarisedAtUtc`
update. Re-diarise depends on the source leg still being retained on disk (the
no-delete firewall in Section 3.2 guarantees this).

## 5. Error handling

| Condition | Behaviour |
|---|---|
| Cancel mid-run | Kill helper process; no write; dialog returns to source selection. |
| Helper crash / non-zero exit | Typed `DiarisationException`; dialog shows the error; session unchanged. |
| Model file missing | Surfaced to the user as the spec's `MODEL_DOWNLOAD_FAILED` (spec 8.2 taxonomy + master-design directive) with the manual-model-path hint; the helper may emit an internal `MODEL_MISSING` stdout token that `SherpaHelperDiariser` maps onto `MODEL_DOWNLOAD_FAILED`. Production download UX is Stage 7. |
| Audio missing for a source | Cannot occur for a **selected** source: selection is gated on the leg being retained (4.1/4.2). If the leg vanishes between selection and run (deleted underneath us), the helper returns `BAD_AUDIO` and it surfaces as an error. |
| Wrong sample rate / not mono | Helper asserts and returns `BAD_AUDIO`; surfaced as an error (should not happen for our own legs). |
| Empty / silent leg (no clusters) | Result with 0 clusters; dialog reports "no distinct speakers found"; no write. |
| Seq with no covering cluster | Left unassigned in `Assignments`; `NameResolver` falls back to the single-participant / me-them label. Not an error - mapping must not assume full coverage (Section 3.3). |

`speakers.json` writes go through `SpeakersStore` + `SchemaGuard` (reject-newer),
consistent with the rest of the storage layer.

## 6. Testing

Model-free by default (PR gate stays `dotnet test --filter Category!=Fixture`,
0-warning build):

- **Core unit:** helper stdout contract parsing (progress lines, final object, error
  object incl. the `MODEL_MISSING`->`MODEL_DOWNLOAD_FAILED` map); `SherpaHelperDiariser`
  cancellation -> process kill -> `OperationCanceledException`; cluster->seq mapping
  (max-overlap, low-clusterId tie-break, **uncovered seq left unassigned** ->
  resolver fallback, no full-coverage assumption); `speakers.json` merge preserves
  `Pinned` verbatim while rewriting non-pinned assignments; **re-diarise drops
  non-pinned `Names`** so a stale name cannot rebind to a shifted cluster; per-side
  default labels are materialised (`Local Speaker 1` / `Remote Speaker 1`, 1-based, no
  cross-source collision, no reliance on the 0-based resolver fallback); write order
  (speakers.json before `Diarised` flip); **the diarise commit performs no audio
  deletion** for any `AudioRetention` value; projection re-render resolves names in
  canonical order; soft-prior decision (auto vs force-N) logic.
- **App/dialog:** source gating on `count > 1 AND leg retained` (no Split on a 1/1
  session, no per-source offer without a retained leg); run -> progress -> name ->
  confirm path against a fake `IDiarisationEngine`; cancel path; re-diarise preserves
  pins and re-surfaces prior names only as user-confirmed suggestions; force-N
  suppressed for a system-mix leg; system-mix banner shown when
  `Mode == SystemMix || FellBackToSystemMix`.
- **Fixture (opt-in, `Category=Fixture`):** a DER regression harness that runs the
  real helper against a recorded multi-speaker clip and asserts DER <= baseline +
  epsilon with a pinned model version. Recording that clip is a user action (the
  existing golden corpus was captured with a single remote party and does not
  exercise diarisation); the harness ships now, the corpus is an open item, exactly
  as the WER golden corpus works today.
- The settings reflection test (`Vm_exposes_no_dropped_setting_surfaces`) is touched
  only if a dormant diarisation setting is added; the default design adds none.

## 7. Binding constraints (must hold)

- **Evidentiary invariant.** `transcript.jsonl` is append-only and never rewritten,
  reordered, tombstoned, or redacted. All speaker information is an additive overlay
  in `speakers.json` keyed by immutable `seq`; the machine-original is always
  recoverable. No delete/hide/redact of content, ever.
- **Pinned preservation.** Manual reassignments in `speakers.json` `Pinned` survive
  every (re-)diarisation verbatim.
- **No name rebinding.** Non-pinned `Names` are dropped for a re-diarised source
  before new labels are written; a prior name is never auto-bound to a shifted cluster
  (Section 3.1). Misattributing a segment to the wrong named person is an evidentiary
  failure, so name carry-over is a user-confirmed suggestion only.
- **No-delete firewall.** The diarise commit performs no audio deletion or retention
  cleanup regardless of `AudioRetention`; the specs' `afterDiarisation` retention seam
  is explicitly NOT wired (Section 3.2). Retained legs are primary evidence.
- **Per-source independent clustering.** Local and Remote legs are clustered
  separately; declared counts gate/seed the Split dialog only, never VAD or capture.
- **Canonical projection apply order** for any re-render: jsonl seq order ->
  vocabulary -> `edits.json` overrides -> render-layer dedup -> name resolution
  (spec 1.3) -> same-speaker grouping. Participant header names come from the
  session's `meta.json` roster snapshot, never live roster resolution, and are the
  user-curated roster - **never** raw diarised clusters.
- **One authority per field.** Speaker assignments live in `speakers.json`; text
  corrections live in `edits.json`. Diarisation writes only the speaker overlay.
- **Write path.** The diarise commit is one `MaintenanceService` single-flight gate
  hold (speakers.json + session.json + projection regen), and re-reads `speakers.json`
  inside the gate so it never clobbers a concurrent change. Note the one pre-existing
  exception to "all UI mutation is gated": `EditStore.ReassignSpeakerAsync` (the
  manual pin path) writes `speakers.json` + `meta.json` **un-gated** today. That is
  out of Stage 5's scope to change, but it means an un-gated manual pin racing the
  gated diarise commit is a residual lost-update window; Stage 5 does not widen it, and
  a future stage should route `ReassignSpeakerAsync` through `MaintenanceService` too.
- **Offline / licensing.** No audio leaves the machine; the model download is the
  only permitted network touch, SHA-pinned, Apache/MIT-only (the embedding default is
  non-VoxCeleb to avoid CC-BY-4.0 provenance risk). sherpa-onnx is Apache-2.0.
  `CUETools.Codecs.FLAKE` (LGPL) stays an unmodified, separately-linked assembly -
  never IL-merged or trimmed.
- **Confidence is warn-only** and never gates output; QA fields are never surfaced in
  projections.
- **Portability.** All shipped libraries ARM64-safe; the helper is published for
  win-x64 and win-arm64. No CUDA coupling on the diarisation path (CPU ONNX).
- **Engineering gates.** net10.0-windows; ASCII-only source; WPF-free view models
  (injected dispatch + `TimeProvider`, no `DateTime.Now` / `Guid.NewGuid` in logic);
  InvariantCulture on disk; humble object for the native/ML touch; conventional
  commits; new packages (sherpa-onnx + runtimes) via the explicit per-stage
  allowlist; offline build box uses `-p:NuGetAudit=false`.

## 8. Build sequence (for the implementation plan)

The plan (writing-plans skill) will expand these into locked-interface tasks. Task 0
is a mandatory de-risking spike; the rest follow the established design ->
subagent-driven TDD -> adversarial whole-branch review -> merge + smoke runbook flow.

0. **Spike (gate):** stand up `LocalScribe.Diarizer.exe` referencing the sherpa NuGet;
   confirm it builds and publishes for **win-x64 and win-arm64**, decodes a real 16k
   FLAC leg via `FlakeReader`, runs the two models, and streams progress + segments.
   Confirms the process-isolation architecture end-to-end before committing to it. If
   the spike fails on ARM64, fall back to Approach B (stock CLI sidecar) for that arch
   or re-evaluate.
1. Extend `tools/fetch-models.ps1` with the two SHA-pinned models + fail-closed
   verification; record hashes in the plan.
2. Helper JSON contract + `FlakeReader` decode + sherpa run + progress/segments out.
3. `IDiarisationEngine` / `DiarisationRequest` / `DiarisationResult` +
   `SherpaHelperDiariser` (spawn, parse, progress, kill-on-cancel, typed errors).
4. Cluster->seq mapping (max-overlap, low-clusterId tie-break, uncovered seq left
   unassigned) and the `speakers.json` merge (pin-preserving, non-pinned-Names reset,
   per-side default labels, no-delete firewall) + `session.json` `Diarised` flip. This
   is a **new** `MaintenanceService.DiariseAsync` method that internally uses the
   existing `RunForSessionAsync` single-flight gate (the gate exists; the diarise
   method does not).
5. Projection regeneration wired to the diarise commit; verify canonical apply order.
6. Split-speakers dialog: source selection + gating.
7. Dialog: run + progress + cancel + soft-prior force-N re-run.
8. Dialog: per-cluster transcript preview + snippet playback + naming + confirm.
9. Entry points (read-view toolbar, sessions context menu) + `Diarised` badge now
   lit.
10. DER fixture harness (`Category=Fixture`) + docs (README roadmap tick, specs
    amendments, smoke runbook for the interactive/GPU-free diarise-a-real-call check).

## 9. Open items (carried, not blockers)

- Multi-speaker DER corpus recording (user action; privileged, never committed).
- VoxCeleb-weights legal sign-off if the English-only embedding is ever made the
  default.
- Benchmark real-time factor on the target laptop CPU to size progress/timeout
  expectations (offline, so > real-time is acceptable if surfaced).
- ARM64 functional coverage of the sherpa binaries (exercised in the Task 0 spike and
  the smoke runbook).
