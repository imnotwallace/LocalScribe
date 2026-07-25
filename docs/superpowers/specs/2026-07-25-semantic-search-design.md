# Semantic Search over Transcripts — Design

**Date:** 2026-07-25
**Status:** Approved (brainstorm complete)
**Origin:** Adopt item #2 from the OpenWhispr competitive analysis (after cross-session voice fingerprinting, merged @ 9a36bdc). Still queued after this: MCP server over the local corpus; Parakeet-via-sherpa ASR spike.

## Goal

Find "where we discussed the settlement figure" without knowing the exact words used. Semantic search **complements** the existing lexical cross-session search (`SearchIndexService` / `SearchIndexBuilder` / `SearchQueryEngine`); it never replaces it.

## Hard constraints

- **Fully local.** No cloud embedding API, ever.
- **Evidentiary firewall.** Read-only over transcripts. The semantic index is derived data — rebuildable, safe to delete wholesale, never evidence. All indexed text derives through `SessionProjectionLoader`, so embedded text always equals displayed corrected text and follows the active version.
- **Native isolation.** Core stays on ONNX Runtime 1.22.0 (VAD). No new native runtime is introduced; the embedder runs in the existing out-of-process Assistant helper.

## Decisions (settled in brainstorm)

| Question | Decision |
|---|---|
| UI shape | One query box; lexical results first as today; distinct **"Related discussion"** section below for semantic-only hits. No interleaved score fusion. |
| Query timing | Lexical stays live (250ms debounce). Semantic fires automatically after the same debounce, arrives late, fills its section with a busy state meanwhile. |
| Recording gate | Bulk embedding indexing **pauses** while a recording is active (AssistantGate spirit); query embedding stays allowed (negligible cost). |
| Staleness UX | Visible coverage note when incomplete: "searched N of M sessions — indexing continues". Never silently imply an exhaustive search. |
| Scale target | Low thousands of sessions (~2,000 sessions / ~120–150k chunks worst case). Brute-force cosine scan; no ANN. |
| Languages | Broadly multilingual (100+ languages). Australia is multicultural; any-language session must be findable with an English query. |
| Host process | **Assistant helper + GGUF embedding model** via LLamaSharp (`LLamaEmbedder`). New `embed` op. Not a new ONNX helper; not in-process. |

Note: the diarizer's `zh_en` CAM++ model is a training-corpus label on a **text-independent** speaker-verification model, not a language policy — it does not constrain or motivate the text-embedding language choice.

## Architecture

A second, parallel derived index mirroring the lexical stack's shape, reusing its metadata.

### Core — new `Search\Semantic\` area

- **`SemanticIndexService`** — orchestrator, mirrors `SearchIndexService`: cold build after startup scan; per-session incremental reindex; self-heal from sidecars; exposes coverage `(fresh, eligible)` and a ready/changed event for the UI. **Eligible** = sessions present in the lexical index (so archive/indexability rules are inherited, never restated); **fresh** = eligible sessions with a current-method, current-stamps sidecar.
- **`SemanticChunker`** — pure; `LoadedProjection` → chunks.
- **`SemanticIndexStore`** — binary per-session sidecars under `index\semantic\`.
- **`SemanticQueryEngine`** — pure; facet filter + cosine scan + ranking + dedup-vs-lexical.
- **`EmbeddingClient`** — Core seam over the Assistant helper's `embed` op (same interface style as `AssistantJobRunner`).

### Metadata authority

The **lexical index remains the metadata authority**. Semantic sidecars hold only vectors + chunk text + anchors, keyed by sessionId. Facets, titles, dates, apps, and participants come from the existing `SearchSessionEntry`. A session absent from the lexical index is absent from semantic — one definition of "searchable", no metadata drift.

## Model and helper protocol

- **Model: EmbeddingGemma-300m**, quantized GGUF (~300MB), 100+ languages, 768-dim with Matryoshka truncation; **store 256-dim**, unit-normalized. Fallback if smoke quality disappoints: Qwen3-Embedding-0.6B.
- **Distribution:** `fetch-models.ps1` addition; registered in `assistant-manifest.json` with a new optional `role: "embedding"` field (absent role defaults to `"chat"` so existing manifests parse unchanged; SHA-256 verification applies as today).
- **Prompts:** EmbeddingGemma's asymmetric prefixes are applied **inside the helper** — queries as `task: search result | query: …`, documents as `title: none | text: …`. Callers never see them.
- **Protocol:** new `embed` op beside chat. Request (one JSON line): `{op:"embed", modelPath, kind:"query"|"document", texts:[…], dim:256}`. Response (one JSON line): `{embeddings:[[…]], method}`. KeepAlive reuses the persistent-stdin machinery: the bulk indexer holds one warm process and streams batches of ~32; the query path lazily warms one on first semantic search; the existing 5-minute inactivity watchdog reclaims it.
- **Backend: CPU, fixed.** Milliseconds per batch at 300M params; avoids VRAM contention with Whisper and the offload-verification dance.
- **Method gating** (voiceprint convention): every vector carries `method` (e.g. `embeddinggemma-300m-q8@256`). Different method → sidecar stale → re-embed. Different-method vectors are never compared.
- **Known risk, checked first in implementation:** LLamaSharp 0.25.0's bundled llama.cpp may predate the gemma-embedding architecture. If so, bump LLamaSharp **inside the helper only** — the isolation boundary exists so this cannot ripple into the App.

## Chunking and position mapping

- **Chunk = greedy pack of consecutive non-marker segments** in projection order, target ~200 tokens (~700 chars approximation), **one-segment overlap** between adjacent chunks. Never spans sessions or versions; at least one segment; a single oversized segment becomes its own chunk (truncate at embed time only past the model's 2K window — effectively never).
- **Speaker labels embedded in chunk text** (`Alice: we could settle at …`) — matches the read view and gives the embedder conversational context.
- Rationale: segment-level is too fine (meaningless 3-word vectors; ~1M of them); `DisplayRow` speaker turns are too variable. Windowed pack ≈ 50–70 chunks per recorded hour → ~120–150k chunks at target scale.
- **Anchors:** each chunk stores `(StartSeq, StartPartIndex, StartMs, EndSeq, EndMs)`. Hit navigation reuses the lexical path exactly: `ReadViewWindow.ShowFindAt(StartSeq, …)` → `RowIndexOfSeq` → scroll. No char offsets. `StartMs` feeds the snippet timestamp.
- **Snippet:** first ~160 chars of chunk text, stored in the sidecar (precedent: lexical cache already duplicates corrected text into `search-index.json`).

## Index storage and freshness

- **One binary sidecar per session: `index\semantic\{sessionId}.vec`.**
  - Header: magic, schema version, `method`, dim, `VersionId`, the same four freshness ticks as lexical (`transcript.jsonl`, `edits.json`, `speakers.json`, `meta.json` last-write ticks via `SearchIndexBuilder.ComputeStamps` semantics).
  - Body per chunk: anchor fields, UTF-8 chunk text, `float[256]`.
  - Atomic write (temp + rename, `AtomicFile` discipline).
- **Binary, not JSON:** ~150k × 256 float32 ≈ 150MB of vectors; JSON would triple it. (`ClusterEmbeddingsStore` JSON is fine at voiceprint scale, not corpus scale.)
- **Per-session files, not one central file:** incremental reindex rewrites one small file; a torn write costs one session; backfill is restart-safe for free.
- **Staleness = lexical rule + method:** reuse a sidecar only if `VersionId` == active version AND stamps match AND `method` matches current model. Otherwise re-embed. Corrupt/unreadable/newer-schema → delete + re-embed, never fault. Orphan sidecars removed at startup sweep.
- **RAM:** all vectors + anchors + snippet text load into a flat in-memory structure at startup (~150–200MB at full target scale). Brute-force cosine via `TensorPrimitives` — tens of milliseconds. Int8 quantization is a schema-version bump away if ever needed (not built now).
- **Deleting `index\semantic\` is always safe** — next startup rebuilds. Same standing as `search-index.json`.

## Query path, blending, facets

1. After the existing 250ms debounce, lexical runs and renders exactly as today.
2. In parallel, query text → warm embed process (`kind:"query"`).
3. `SemanticQueryEngine` filters candidate sessions by the **same facet values** (matter / date / app, evaluated against lexical `SearchSessionEntry` metadata — identical behavior in both sections), cosine-scans surviving sessions' vectors, fills the **Related discussion** section.
4. Lexical never waits on semantic; a slow or failed embed leaves lexical untouched.

- **Ranking:** cosine desc with a **minimum-similarity floor** (start ~0.55, tuning constant; below it the section stays empty rather than padding with junk). Top ~40 chunks → session cards ordered by best-chunk score → snippet rows by score within a card. Same card/snippet UI as lexical.
- **Dedup vs exact:** drop a chunk if its session already appears in lexical results AND the chunk's `[StartSeq, EndSeq]` contains one of that session's lexical hit seqs (never show the same passage twice). A session may appear in both sections pointing at different passages.
- **Coverage note:** when `fresh < eligible`, section header reads "searched N of M sessions — indexing continues". Query-embed failure shows "related search unavailable" in-section; never an error dialog.

## Backfill and scheduling

- **Cold build:** chained onto the `StartupOrchestrator.ScanCompleted` continuation, after the lexical build. Single background worker, one session at a time: projection → chunk → batch-embed (keepAlive helper) → write sidecar. First full backfill at target scale ≈ **20–40 min low-priority CPU**; per-session persistence makes it resumable across restarts.
- **Recording pause:** worker subscribes to the same recording-active signal `AssistantGate` uses; on recording start it finishes the in-flight batch (seconds), then parks; resumes on recording end. Query embedding is exempt.
- **Incremental triggers** (same as lexical): `SessionContentChanged`, `SessionFinalizeCompleted`, `RetranscriptionCompleted`, import completion → enqueue session for re-embed.
- **Settings:** one toggle "Semantic search", default **on** when helper + embedding model present (assistant-style presence gating; absent → feature hidden with the standard missing-helper message). Toggling off parks the worker and hides the section; sidecars remain so re-enable is instant.

## Error handling

- Helper/model missing → feature hidden with message (assistant `MissingMessage` pattern).
- Per-session embed failure → skip, count against coverage, log (mirrors `SessionSkipped`).
- Corrupt sidecar → silent rebuild.
- Helper crash mid-backfill → respawn with backoff; session retried once, then skipped-with-note.
- Query-embed failure → in-section "related search unavailable"; lexical unaffected.

## Testing

- **Pure units** (no model): `SemanticChunker` (packing, overlap, marker exclusion, oversized segment), `SemanticQueryEngine` (facets, floor, ranking, dedup) with synthetic vectors, `SemanticIndexStore` round-trip + corrupt/truncated files, staleness rules (version/stamps/method).
- **Fake `EmbeddingClient`:** deterministic vectors for service-level tests.
- **VM tests:** queued fakes, not sync-dispatch (BeginInvoke stamp-ordering lesson from the assistant-surfaces round).
- **Fixture-gated integration:** one test exercising the real helper `embed` op when the model file is present (existing gated-fixture pattern).
- **Manual smoke:** real-model relevance quality ("settlement figure" finds the discussion; a multilingual query finds a non-English passage) and similarity-floor tuning — like the voiceprint threshold smoke.

## Explicitly out of scope

- ANN structures (HNSW etc.), int8 vector storage, GPU embedding — not needed at target scale; schema-versioned escape hatches exist.
- Blended/interleaved ranking across lexical and semantic.
- Semantic search inside a single open session (read-view Find stays lexical).
- Any change to lexical index format or behavior.
- MCP server exposure (next adopt item; this design's `SemanticQueryEngine` should be reusable there).
