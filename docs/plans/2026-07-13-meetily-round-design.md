# Meetily-round design — search, versioned re-transcription, audio import, console polish

**Date:** 2026-07-13
**Status:** Approved (brainstormed section-by-section with the user; all four sections approved)
**Provenance:** Derived from a verified competitive analysis of Meetily (meetily.ai /
github.com/Zackriya-Solutions/meetily, v0.4.0). Load-bearing claims were adversarially
verified against Meetily's docs and source. The analysis identified four adopt-worthy
features and a set of anti-patterns that this design deliberately avoids.

---

## 1. Scope

Four features, built as four branches (subagent-driven development, per-task review,
whole-branch review), merged `--no-ff` smallest-first:

1. **Record-console polish** (smallest)
2. **Versioned re-transcription**
3. **Audio import**
4. **Cross-session search** (last — must index active versions and imported sessions)

Post-merge gate per branch, as established: 0-warning build + full App/Core test suites.

**Non-goals this round:** batch/matter-wide re-transcription; marker-text search;
auto-carry of corrections across transcript versions; FFmpeg formats beyond a sane
lawyer-receivable set in the picker (the decoder itself accepts whatever FFmpeg decodes);
Stage 7 packaging work itself (only its implications are recorded here); AI summarization.

### Decisions log (user-confirmed)

| Decision | Choice |
|---|---|
| Round contents | All four features |
| Re-transcription default view | New version becomes active/default; badge + one-click switch back; originals always preserved |
| Version storage | Per-version subfolder `versions\vN-<model>-<date>\`; session-folder root stays the untouched original |
| Search UI | Both: dedicated Search page **and** Sessions quick-filter content matching |
| Search scope | Corrected text + original machine text + speaker names; **plus** Ctrl+F find-in-transcript in Read view. Marker text: out |
| Search index | Persisted derived index file, self-healing (matters-index pattern) |
| Import channels | Ask at import time for stereo ("each party on its own channel?"); mono → one leg |
| Import decode | Bundle FFmpeg (LGPL shared build, SHA-pinned fetch script; Stage 7 bundles it) |

### Evidentiary rules that bind every feature (locked, from project memory)

- The machine transcript is append-only evidence; nothing this round rewrites, hides, or
  deletes transcript content. Original session-root files are **never modified or moved**.
- Deleting a *partial derived output* on cancel (unfinished version folder, unfinished
  import session folder) is permitted — it is not evidence yet. Completed outputs are.
- Displayed transcript text is verbatim engine output + explicit non-destructive
  corrections. No silent filtering, no confidence-threshold dropping (verified Meetily
  anti-patterns; never copy).
- Degradation is always surfaced (markers/warnings), never silent.

---

## 2. Cross-session search

### 2.1 Core — new `Search\` namespace in LocalScribe.Core

- **`SearchIndexService`** — owns an in-memory index of all sessions. Per-line entries:
  segment id, timestamp, corrected text, original machine text (stored only where a
  correction differs), speaker name. Session-level fields: session id, title, matter ids,
  date, source app. Indexes each session's **active version** (§3) — for un-versioned
  sessions that is the session root.
- **`SearchIndexStore`** — persisted cache at `<storageRoot>\index\search-index.json`,
  written via `AtomicFile`, schema-stamped (SchemaGuard pattern). Each session entry
  carries freshness stamps: last-write ticks of the **active version's**
  `transcript.jsonl`, `edits.json`, and `speakers.json` (session root for v1), the root
  `meta.json`, plus the active-version id. On load: stale or missing
  entries are re-derived from session files, orphaned entries dropped, corrupt cache →
  silent full rebuild. Same self-healing philosophy as `MattersIndexRebuilder`.
- **Incremental updates** — the existing sessions-live-autoupdate events (finalize,
  edit-save, re-render, re-transcribe, import) trigger single-session re-index; the cache
  file is rewritten debounced.
- **Query semantics** — case-insensitive substring over corrected text, original machine
  text, **and speaker names** (session participants + speaker-overlay names); multiple
  words = AND across a session; ranked by hit count, then recency. Snippet = ±60 chars
  around the first hit in a line; speaker-name-only matches show the session's first line
  for that speaker as the snippet.
  Matches found only in original machine text are labelled *(matches original text)* —
  corrections never hide content from search. Facets: matter, date range, source app.

### 2.2 App — three surfaces

1. **Search page** — new nav-rail item between Sessions and Matters. Query box, facet row
   (matter dropdown, date range, source app), results as session cards each holding
   snippet rows (timestamp + speaker + highlighted snippet). Clicking a snippet opens
   `ReadViewWindow` scrolled to that segment with the match highlighted. Empty states for
   "no query yet" and "no results".
2. **Sessions quick filter** — the existing "Filter sessions…" box additionally consults
   `SearchIndexService` (debounced ~250 ms). Content-matched rows show a single snippet
   line under the title. Title/metadata filtering behavior is unchanged.
3. **Ctrl+F in Read view** — find bar overlay: match count ("2/7"), next/previous
   (Enter / Shift+Enter, buttons), Esc closes, matches highlighted in the transcript list,
   auto-scroll to current match. Searches the **visible (corrected) text** of the loaded
   version only.

### 2.3 Errors & testing

- A session that fails to load during indexing is skipped and logged; it never blocks
  other results. Index build happens off the UI thread; first search on a cold cache
  shows a brief "indexing…" state.
- Core tests: build, self-heal (stale/missing/orphan/corrupt), incremental update, query
  semantics (AND, ranking, original-text labelling), snippet extraction, version/import
  interplay. App tests: all three surfaces' view-models, click-through targeting.

---

## 3. Versioned re-transcription

### 3.1 Storage

- Session-folder **root = v1 (the original)**, never touched. Each run creates
  `versions\v2-<model>-<yyyy-MM-dd>\` (monotonic v2, v3…) containing its own
  `transcript.jsonl`, fresh empty `edits.json`, `speakers.json` (absent until Split),
  and rendered projections (`transcript.md`/`.txt`).
- `session.json` gains `activeVersion` (default `"v1"`) and `versions[]` — per entry:
  id, model, backend, createdAt, whether vocabulary bias was applied. `SessionMigrator`
  bumps the schema; old sessions read as `activeVersion: v1` with an empty list.

### 3.2 Engine

- **`RetranscriptionRunner`** (Core) drives the existing `OfflinePipelineRunner` from
  `local.flac`/`remote.flac` via the existing `FlacPcmReader`, applying **current**
  global + matter vocabulary as prompt bias.
- Guards: blocked while the session is Recording/Finalizing/Recovering; honors the
  one-engine-at-a-time rule (no re-transcription while a live recording runs, and vice
  versa); one re-transcription at a time. Cancel discards the partial `versions\` folder
  (derived output, not yet evidence).

### 3.3 Semantics

- Corrections and speaker overlays are keyed to machine segments → they are
  **per-version**. A new version starts clean; no auto-carry (explicitly out of scope —
  unsafe to remap). Editing and Split speakers always operate on the active version.
- Search indexes the active version (§2). Export: `.zip` archives the whole folder (all
  versions included by construction); `.docx` renders the active version and its footer
  states version id + model.
- Playback is version-independent (same FLAC legs).

### 3.4 UI

- "Re-transcribe…" in the Sessions action bar, the row context menu, and Session Details.
- Dialog: shows current active version (id, model, date); model picker limited to models
  actually present on disk (via `ModelFileResolver`/`ModelLadder`); language selector;
  Start/Cancel.
- Progress: the session row shows a "Re-transcribing…" chip through the existing
  live-autoupdate path; the dialog can be closed without cancelling (work continues).
- Completion: new version becomes active. Read-view header gains a version badge
  ("v2 · small.en") with a dropdown listing all versions (original = "v1 · <model>");
  switching persists `activeVersion` and reloads the projection, edits, and speakers of
  the selected version.

### 3.5 Testing

Version folder creation/naming; `activeVersion` resolution through
`SessionProjectionLoader` (read view, export, search all follow it); migrator round-trip;
guards (recording/finalizing/concurrent); cancel cleanup; per-version isolation of edits
and speaker overlays; docx footer version note.

---

## 4. Audio import

### 4.1 Provenance (chain of custody)

- The original file is copied **unmodified** into the session folder as
  `source\<original-filename>`; `session.json` records `origin: "imported"`, the SHA-256
  of the original bytes, and the original file's timestamps.
- Decoded audio is transcoded to standard FLAC leg(s), so playback, Split speakers,
  re-transcription, export, and search work identically to recorded sessions.
- Decoded-stream truth (verified Meetily bug class, issue #607): sample rate and channel
  count are taken from the **decoded stream**, never the container header; decoded
  duration is cross-checked against the container's claimed duration — a >1% mismatch
  pauses the import after the Decode stage with a Continue/Cancel warning, and if the
  user continues, a transcript marker records the mismatch.

### 4.2 Decode — FFmpeg

- `tools\fetch-ffmpeg.ps1` fetches an SHA-pinned **LGPL shared build** into
  `tools\ffmpeg\` (same pattern as `fetch-models.ps1`). Stage 7 bundles it beside the
  app like `Diarizer.exe`.
- Import runs `ffprobe` (container metadata for the preview + claimed duration), then
  decodes via an `ffmpeg` subprocess to PCM with a timeout. WAV is read natively; FLAC
  via the existing `FlacPcmReader`; everything else goes through FFmpeg — one
  deterministic decode path across machines (MF codec availability varies by Windows SKU).
- FFmpeg absent → the Import button is disabled with a clear message pointing at the
  fetch script (the Diarizer-helper pattern; nothing crashes).
- The decoder sits behind an interface (`IAudioDecoder`) with a fake for unit tests.

### 4.3 Channel mapping

- Stereo file → the dialog asks: "Each party on its own channel?" → **Yes**: L→Local,
  R→Remote (with a swap control) — structural side attribution for free; **No/unsure**:
  downmix to one mono leg. Mono → one leg. More than 2 channels → downmix with a note.
- Single-leg imports rely on Split speakers for attribution, unchanged.

### 4.4 UI

- "Import audio…" button on the Sessions page → dialog: file picker (WAV, FLAC, MP3,
  M4A/AAC, WMA, OGG + All files), ffprobe preview (name, duration, size, format),
  editable title (defaults from filename), **editable recorded-date** (defaults to the
  container's media creation timestamp, falling back to the file's own timestamps;
  legally meaningful, so user-correctable), optional matter
  tagging (same picker as the Record console), the stereo question when applicable.
- Staged progress: Copy → Decode → Transcribe → Save, with Cancel (discards the partial
  session folder). Completion opens the session; Sessions list Source shows
  "Imported — MP3" etc. Imported sessions enter the search index automatically.

### 4.5 Testing

Channel-mapping variants; duration-mismatch marker; SHA-256 + origin recording;
ffmpeg-missing path; cancel cleanup; decoder-fake unit tests; one fixture-gated test with
a real small MP3 through the real FFmpeg.

---

## 5. Record-console polish

All in `LiveViewWindow` + its view-models; Core changes limited to surfacing existing
probe/lag data.

1. **Empty state** — while recording with no text yet: "Listening — transcript appears a
   few seconds after speech." Centered, muted; removed at the first transcript line.
2. **Prominent status** — "Recording" + elapsed timer promoted to a larger red-accented
   header element; Paused equally visible. This is the app's most safety-critical status.
3. **In-window level meters** — two mini meters (Local / Remote) beside the status,
   reusing the overlay pill's level sources; a silent leg visibly flatlines.
4. **Engine chip** — "small.en · CUDA" on the ready card and while recording, with a live
   keep-up indicator from the existing lag detection: "keeping up ✓" normally, red
   "lagging ×1.4" state (absorbs the current lag warning text). Rationale: the verified
   silent-CPU-fallback class — the user must see *before and during* an important call
   which backend is bound and whether transcription keeps up.
5. **Pre-flight target check** — on the ready card, when Remote target is Auto/per-app:
   "Webex detected ✓" or "No call app playing audio — will record system mix", via
   `PreflightProbe`/`WasapiSessionScanner`, refreshed every few seconds until Start.
   Replaces the two redundant grey summary lines currently duplicating the pickers.

Testing: view-model states (empty→first-line transition, lag chip states, probe states,
meter wiring); no new Core surface beyond exposing existing signals.

---

## 6. Stage 7 implications (notes only, no work this round)

- Installer must bundle: `Diarizer.exe`, `ffmpeg` (LGPL build + license text), models
  (or a first-run download step with progress + manual fallback — the standard local-AI
  onboarding pattern).
- README refresh rides along with this round's final merge: Stage 6 is shipped (README
  still says "not yet built"), search is no longer a non-goal, import/versions/search
  join the feature list, storage-layout diagram gains `versions\`, `source\`, `index\`.

## 7. Anti-patterns deliberately avoided (verified against Meetily)

- Destructive re-transcription (theirs replaces the transcript irreversibly) → ours is
  versioned, originals immutable.
- Silent confidence-threshold segment dropping; silent filler-word filtering of displayed
  text → verbatim display, always.
- Container-header trust on import (2× speed / half duration bug) → decoded-stream truth
  + duration cross-check + marker.
- Recovery/derived-data auto-expiry → nothing in this round expires anything.
- Blocking recording start on model checks; blocking the UI post-stop → unchanged
  instant-start/instant-stop behavior; the new pre-flight line is informational only.
