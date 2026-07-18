# Steno-round design — call-detection advisory, compact console, local assistant (summaries + Q&A), dedup guard, markdown export, deep links

**Date:** 2026-07-18
**Status:** Approved (brainstormed section-by-section with the user; sections A–D approved; model choice locked after context-budget analysis)
**Provenance:** Derived from a close implementation review of Steno (github.com/ruzin/stenoai, `main` @ 2026-07-18): five research passes over its Electron main process, Python backend (`simple_recorder.py`, `src/summarizer.py`, `src/transcriber.py`, `src/templates.py`, `src/report_store.py`), mic-monitor helper, and renderer. Claims cited in this doc were verified against fetched source, not the README. A parallel audit of LocalScribe's own `PhantomBleedDedup` confirmed the short-utterance gap this round fixes.

---

## 1. Scope

Seven branches off master (started after the in-flight `feat/ux-round-2026-07-18` merges), subagent-driven development, per-task review, whole-branch review, merged `--no-ff` smallest-first:

1. `fix/dedup-short-utterance-guard` (smallest — a real defect fix ships first)
2. `feat/markdown-export`
3. `feat/deep-link`
4. `feat/call-detect-advisory`
5. `feat/console-compact-mode`
6. `feat/llm-foundation-summaries` (helper exe + models + per-session summaries)
7. `feat/matter-qa` (Q&A over session + matter scopes, citations)

Post-merge gate per branch, as established: 0-warning build + full App/Core test suites.

**Non-goals this round (deliberate, recorded):**

- **Template-driven shareable reports** — phase 3 of the assistant work, deferred to a later round ("both, phased" user decision: recall aid first).
- **Parakeet TDT v3 via onnx-asr CPU engine** — deferred spike, stubbed in project memory (`parakeet-onnx-cpu-spike-stub`); benchmark-first, and any adoption must enter the weights-provenance model as a first-class backend.
- **Whole-library Q&A** — rejected: mixing content across clients' matters in one LLM context is a confidentiality hazard. Scopes are session and matter only.
- **Cloud LLM providers** — rejected permanently for transcript content: strictly local-only; no network path.
- **Auto start/stop/pause of recording** — rejected. Detection is advisory-only; manual trigger remains the rule (Steno itself never auto-starts; we additionally never auto-pause).
- **Calendar/pre-meeting scheduling** (Steno has one) — no calendar integration in LocalScribe; out.
- **Meeting auto-detection writing markers** — never; same locked rule as the tray-mute advisory tier.

### Decisions log (user-confirmed)

| Decision | Choice |
|---|---|
| LLM policy | Strictly local-only; transcript content never leaves the machine |
| Summary purpose | Both, phased: private recall aid first (strict grounding + citations); shareable template reports = later phase |
| Detection offer | Always-on-top toast window (custom, no-activate — not a WinRT notification); one click starts recording with detected app as per-process target; consent flow unchanged; ignore = nothing |
| Compact surface | Record console compact mode (same window, template swap, Topmost pill) — no second window |
| Dedup short guard | Exempt short segments from auto-suppression entirely (new mechanism; four golden thresholds + EchoTimeCoverageMin untouched); floor golden-corpus-validated |
| Q&A scope | Session + matter; no cross-matter/library chat |
| Deep link | In this round (`localscribe://record/start|stop`), Steno's sanitization contract |
| LLM runtime | Out-of-process helper exe over stdio (Diarizer.exe pattern); no sockets |
| Default model | **Qwen3-4B-Instruct-2507 q4_K_M, locked, no bake-off** — chosen on IFEval-class instruction-following evidence, llama.cpp/CUDA fit, Apache-2.0, and VRAM headroom; Qwen2.5-7B excluded (dominated: weaker benchmarks, 4.7 GB weights vs this box's VRAM, 32k-native ceiling kills the long-call mitigation); Gemma 4 E2B QAT = optional manifest entry |
| Context budget | 32k operating budget suffices for 1–2 h calls: 1 h ≈ 10–14k tokens (comfortable); 2 h ≈ 20–28k (borderline by design — summaries flow into map-reduce at the 80% gate; Q&A uses raise-or-excerpt, §7.5) |

### Evidentiary rules that bind every feature (locked, from project memory)

- The machine transcript is append-only evidence; nothing this round rewrites, hides, or deletes transcript content. Assistant artifacts are **derived work product stored separately** and never touch transcript files.
- Every assistant output is labeled **"AI-generated draft — not a transcript; verify against the record."** wherever rendered or exported.
- Degradation is surfaced, never silent: omitted context is disclosed in the UI, unverifiable Q&A claims are flagged not dropped, engine/backend falls are recorded.
- Detection/deep-link paths can never bypass the consent flow and never write markers.

---

## 2. `fix/dedup-short-utterance-guard`

**Defect (confirmed in `PhantomBleedDedup.cs`):** whole-string similarity has no length floor — a genuine brief reply ("Yes.", "OK") coextensive within `NearWindowMs` (750 ms) of a similar short line on the other leg is hidden when the ≥3 dB RMS gap holds (or on identical text when RMS is missing, via the 0.975 text-only bar). `EchoTimeCoverageMin = 0.70` only protects fragments inside *longer* lines, not short-vs-short. Steno's transcriber gates the equivalent path at 15 chars (`PER_SEGMENT_BLEED_MIN_CHARS`); our containment path has a 12-char/3-token floor but the whole-string path has none.

**Fix:** `PhantomBleedOptions` gains `MinAutoSuppressChars = 12` and `MinAutoSuppressTokens = 3` (provisional — final values validated against the golden corpus before locking; they mirror the existing containment floor in `TextDistance.ContainmentSimilarity`). Evaluated on the **normalized text of the segment that would be suppressed**, at the top of both `IsBleedOf` (pass 1) and `IsEchoOfLocal` (pass 2): below either floor → never auto-suppressed, regardless of similarity/RMS/coverage. The four golden thresholds and `EchoTimeCoverageMin` are untouched.

**Accepted cost:** a real short echo ("Yes" bleeding to both legs) now renders twice. A visible duplicate is evidentiarily safer than a silent hide of possibly-genuine speech.

**Tests (new; existing ~20 stay green):** short coextensive genuine reply survives in pass 1 and pass 2, each with (a) qualifying RMS gap and (b) missing-RMS identical text; boundary cases at exactly the floor; golden-corpus regression run recorded in the PR.

---

## 3. `feat/markdown-export`

- `MarkdownRenderer` gains a `Write(...)` overload at DocxRenderer parity: consumes the same `SessionProjectionLoader` triple (`Header` / `TextView` / `Rows`), emits metadata block, disclaimer, footer (reuses `Settings.DocxFooterText`), honoring `IncludeTimestamps` / `IncludeMarkers` options. The existing save-time `MarkdownRenderer.Render` → `transcript.md` path is untouched.
- `ExportFormat` gains `Markdown`; the export dialog shows the same option toggles as docx; `SavePathRequest` filter `*.md`.
- `MaintenanceService.ExportMarkdownAsync` mirrors `ExportDocxAsync` exactly: `RunForSessionAsync` session gate, `ExportWithOutputCleanupAsync` (output-file-only cleanup on failure), `SessionProjectionLoader.LoadAsync`, `ExportFileNames.Sanitize` default name.

---

## 4. `feat/deep-link`

**Scheme:** `localscribe://record/start[?name=<text>]` and `localscribe://record/stop`. Registered per-user (HKCU `Software\Classes\localscribe`, `URL Protocol`) — unpackaged-app pattern; registration at startup, idempotent.

**Single-instance routing:** second instance forwards its argv to the running instance and exits. Plan-time check: if a single-instance mechanism already exists, reuse it; otherwise add mutex + local named pipe (OS IPC, not a socket — the zero-network posture holds).

**Parser:** pure static `DeepLinkParser` (Core), unit-tested as an untrusted-input boundary — Steno's contract adopted wholesale: never throws (invalid → typed reject + reason); action allowlist of exactly the two verbs; `name` sanitized (Unicode letters/marks/digits + `. , ( ) @ & ' ! + # -`, other chars → space, whitespace collapsed, capped 120 chars); query strings never logged.

**Semantics (consent + abuse guarded):**
- `start`: runs the exact same command path as a manual start — consent flow as configured, Record console opens, `name` prefills the session title. Start-while-recording → notification toast, no action.
- `stop`: a drive-by webpage can invoke a registered scheme, and stopping an evidentiary recording must not be triggerable silently — so `stop` shows a confirm toast (**[Stop recording] [Keep recording]**, no-activate); only the explicit click stops. Stop-while-idle → notification toast.

---

## 5. `feat/call-detect-advisory`

### 5.1 `CallActivityWatcher` (Core)

Polls WASAPI **capture**-endpoint audio sessions (CoreAudio API already used by the capture stack) every 1.5 s (constant), enumerating sessions with state Active across capture endpoints, resolving PID → process image name, and diffing against the previous tick's set → `CallAppActivity { Exe, Pid, Started|Stopped, Timestamp }` events. Fail-open like `TrayMuteSignalSource`: any COM/enumeration error logs, skips the tick, and can never affect capture. Steno equivalence: their Swift helper's 1 s poll-and-diff over `kAudioHardwarePropertyProcessObjectList`; the shape is proven, we implement the Windows analog in-process (no helper needed in .NET).

### 5.2 `CallDetectionPolicy` (Core, pure, unit-tested)

Decides `Offer | Ignore` from an activity event + state snapshot:
- master toggle (Settings, **default on**);
- allowlist match on exe name, case-insensitive — Settings-editable list, defaults `CiscoCollabHost.exe`, `webex.exe`, `ms-teams.exe`, `Zoom.exe` (the actual Webex capture-session owner exe is verified during smoke and defaults adjusted); browsers excluded by default (addable);
- own process always excluded;
- suppressed while a recording session is active or the Record console is armed;
- per-exe cooldown 60 s (Steno's `MIC_NOTIFICATION_DEBOUNCE_MS`).

### 5.3 Offer toast (App)

Frameless plain `Window` (not Mica — WPF-UI startup-rendering gotcha), `Topmost`, **no-activate** (`ShowActivated=false`, `WS_EX_NOACTIVATE`, `Focusable=false`) so it never steals focus from the live call; ~400×90 bottom-right of the primary work area; shown only after the message pump is up. Content: "Call detected — <App>" + **[Start recording] [Dismiss]**; auto-dismiss 15 s; ignore = nothing, ever.

**[Start recording]** → the normal start path: detected app applied via the existing `RemoteAppOverride` seam as the per-process capture target, consent flow exactly as configured, Record console opens (compact-mode auto-offer per §6 applies).

### 5.4 Call-end advisory

Only while a recording session is active: watched app's capture session goes inactive → 3 s debounce (Steno-verified: Zoom/Teams software-mute keeps the OS stream open, so in-call mute never fires this; session returning within the window cancels) → advisory toast "Call appears to have ended — stop recording?" **[Stop recording] [Keep recording]**. Never auto-stop, never auto-pause; pad-to-session-end and manual stop remain the recording behavior. Detection never writes markers.

---

## 6. `feat/console-compact-mode`

- The Record console window gains a **compact state**: same window object, template swap (visual state / DataTrigger), `Topmost` only while compact, ~420×64.
- Pill contents: recording dot + elapsed; last **finalized** live line (single line, end-trimmed); mute pill (advisory mute/device-mute banners collapse to a colored state on the pill + tooltip — never lost); stop; expand.
- Drag-to-move (`DragMove`), position persisted, clamped to a visible work area on restore (multi-monitor safety).
- Entry: a Compact toggle in the console header; Settings option "collapse console when recording starts" (**default off**). Stop from the pill restores the full console on the finished session.
- Steno patterns adopted: coexist-don't-take-over; warm-up indicator (live-model "Preparing…" state already surfaced by the console carries into the pill). Steno patterns *not* adopted: their pill is in-app-only (no OS-level precedent — this design is ours); "stop is the new pause" is unnecessary (we already have explicit mute/resume semantics).

---

## 7. `feat/llm-foundation-summaries` + `feat/matter-qa` — the local assistant

**Branch split:** branch 6 (`llm-foundation-summaries`) = §7.1 runtime + §7.2 models + §7.3 storage + §7.4 summarization + the Session Details summary portion of §7.6 + Settings section. Branch 7 (`matter-qa`) = §7.5 Q&A (both scopes, citation validation, excerpting) + session/matter chat UI + the Matters Assistant tab.

### 7.1 Runtime: `LocalScribe.Assistant.exe`

- Single-file self-extracting helper (Diarizer.exe publish pattern), hosting **LLamaSharp/llama.cpp** with CUDA and CPU backends. Backend pick: try CUDA, fall to CPU; the backend actually used is recorded in every artifact (floor-fall provenance discipline).
- Spawned per job by the App; protocol = JSON request on stdin (`{op: summarize|answer, modelPath, ctx, payload…}`), JSON-lines on stdout (`chunk | progress | done | error`), UTF-8; cancel = process kill; App-side inactivity watchdog. **The helper never writes files** — the App owns all persistence via `AtomicFile`.
- **Warm helper for chat (KV reuse):** for an open chat session the helper is kept alive with the scope context prefilled once; follow-up questions send only the question (`op: answer` on the live process), so per-question latency is generation-only. Without this, every question re-prefills the transcript — minutes on a CPU-only box (§7.2). Torn down on chat close, idle timeout (5 min), scope change, or any context change (staleness rules); summarize jobs remain spawn-per-job.
- **One heavy engine at a time:** assistant jobs are blocked while a recording session is active — queued with a visible "waiting for recording to finish" state (`ExternalEngineBusy`-style surface).
- No sockets anywhere; stdio only.

### 7.2 Models

- GGUF only; manifest entries `{canonicalName, file, sha256, nativeCtx, license}`; fetched by the established SHA-pinned script flow; never bundled.
- **Default (locked): `Qwen3-4B-Instruct-2507` q4_K_M** (~2.5 GB, Apache-2.0, 262k native ctx). Optional entries: `Gemma 4 E2B QAT` (Gemma ToU) and `Qwen3-1.7B-Instruct` q4_K_M (~1 GB — low-end/CPU-only hardware option; same family and prompts, speed over quality). Qwen2.5-7B deliberately absent (decisions log; on CPU it is strictly worse than the 4B — bandwidth-bound generation scales inversely with weight size).
- **CPU-only envelope (documented so UI copy and plans are honest):** the app must remain fully functional on GPU-less machines. Mid-range laptop CPU (8 threads, dual-channel RAM), Qwen3-4B q4_K_M: ~8–14 tok/s generation; 1-hour-session summary ≈ 4–8 min end-to-end (streamed, background, queued UI); chat first-question prefill ≈ 1.5–4 min, follow-ups seconds via the warm helper (§7.1). Assistant UI presents long jobs as such (progress + "may take several minutes"); never blocks the rest of the app.
- **Context/VRAM policy:** per-job `num_ctx` sized to the job (estimated input tokens + output reserve), not a fixed max; KV cache quantized q8_0. Sizing math recorded here: KV ≈ 147 KB/token fp16 for Qwen3-4B (≈ half at q8) → ≤16k jobs fit a 6 GB GPU beside 2.5 GB weights with headroom; larger windows (≈48–64k) are CPU-RAM-backed — slower but correct. Thinking mode disabled for all extraction jobs (whole token budget to the answer — Steno's `think=False` lesson).

### 7.3 Storage, versioning, labeling

- Per-session sidecar `assistant\summaries.json`: **versioned, append-only** — `{id, createdAt, sourceTranscriptVersion, model{file, sha256, backend}, promptVersion, contentMarkdown, stale}`. Regenerate appends a new version (old versions switchable in the UI); nothing is overwritten (Steno's sidecar + our versioning ethos).
- Transcript change (new version, correction save, split) → summaries marked **stale**; regeneration is an explicit user CTA (Steno's `notes_stale` pattern), never automatic.
- Chat history: `assistant\chats.json` in the session folder (session scope) and the matter folder (matter scope).
- Every rendered/exported assistant artifact carries the AI-generated-draft label. Zip archives include the `assistant\` folder as-is (clearly separated); docx transcript export excludes assistant content.
- `promptVersion` is a bumped constant covering every prompt change, so artifacts are reproducible-in-principle.

### 7.4 Summarization

- Fits-check gate at 80% of `num_ctx` using worst-case **2 chars/token** (Steno's arithmetic — the gate must trigger before overflow, never after). Single call when it fits; else map-reduce: map prompt caps chunk output (~600 tokens), reduce merges; hierarchical reduce max depth 2, then an honest "session too long for the configured model" error.
- Input shaping: leading per-line timestamps stripped (line-anchored regex — a UI concern only); **named speaker labels kept** with a roster preamble (our advantage over Steno's two-bucket `[You]/[Others]`); user-invisible grounding line always appended app-side: extract only what is explicitly stated, do not infer.
- Sections: **Summary / Key topics / Key statements / Follow-ups & commitments** (fixed English headers; body language follows the session).
- Empty model output → error surfaced, nothing persisted (never a blank artifact).

### 7.5 Q&A (session + matter)

- **Strict-extractive** — the prompt *forbids* inference (deliberately opposite to Steno's permissive query prompt) and requires a `[HH:MM:SS]` citation per claim.
- **Citation post-validation:** each cited timestamp must resolve to a real segment (±2 s tolerance) and the claim must fuzzy-match that segment's text (reuse `TextDistance`); unverifiable claims render **visibly flagged** ("uncited"), never silently dropped. Citations click through to the Read view scrolled to the segment (reuse the Matters open-transcript navigation).
- **Session scope:** full projected transcript (active version, corrections applied). If it exceeds the operating budget: first raise `num_ctx` within the RAM policy (§7.2); beyond that, **search-assisted excerpting** — question terms against the existing search index → matching segments ± surrounding context, with the answer header disclosing "answered from matching excerpts, not the full transcript." Disclosed degradation, never silent.
- **Matter scope:** per-session summaries + key points (never transcripts), newest-first within budget; the UI lists exactly which sessions are included/omitted (no silent truncation — improves on Steno's prompt-only disclosure); sessions lacking summaries are offered for generation first. Hard-scoped to one matter by construction.
- Multi-turn UI, **single-turn to the model** in v1 (full context re-sent per question — Steno's local path does the same; theirs is undisclosed, ours is a recorded v1 constraint). History persisted per scope.

### 7.6 UI surface

- **Session Details** gains an **Assistant** tab: summary (version switcher, stale badge, Regenerate CTA, streaming render) + session chat.
- **Matters** page gains an **Assistant** tab: matter chat + per-session summary status (generated / stale / missing → generate).
- **Settings** gains an Assistant section: master toggle, model picker (manifest-installed models), fetch instructions when no model is present. All assistant UI is disabled-with-explainer until a model exists; everything else in the app is unaffected.

### 7.7 Failure posture

Helper crash → visible error, nothing persisted. Job requested mid-recording → visibly queued. Model missing → features off with explainer. CUDA fall to CPU → recorded in artifact provenance, surfaced in the job UI.

---

## 8. Testing strategy

- **Pure/unit:** `CallDetectionPolicy`, `DeepLinkParser`, dedup floor (both passes + boundaries), chunking/budget math, citation validator, prompt builders (snapshot tests keyed to `promptVersion`), manifest resolution, markdown renderer parity.
- **Golden corpus:** dedup floor validation run recorded before locking values.
- **Integration:** helper protocol round-trip with a stub process (echo harness) — protocol framing, cancel, watchdog, crash surface; real-model runs are smoke-only.
- **Smoke runbook (user, per established practice):** real-Webex detection offer + capture-owner exe verification; call-end advisory including in-call mute (must NOT fire); toast no-activate during a call; compact-mode drag/multi-monitor/banner states; deep-link start/stop from a browser incl. the stop-confirm guard; first summary + Q&A citation click-through on a real session; 2 h session map-reduce path; CPU-floor assistant run.

## 9. Steno reference notes (for implementers)

Verified sources worth consulting during implementation: `mic-monitor/mic_monitor.swift` (poll-diff shape), `app/meeting-detect.js` (allowlist + fallback policy, unit-tested), `app/main.js` detection block (debounces, offer flow), `app/shortcut-url.js` (sanitization contract), `src/summarizer.py` (map/reduce prompts, num_ctx discipline, `think=False` retry ladder, empty-result guards), `src/report_store.py` (sidecar versioning), `simple_recorder.py` (`notes_stale` append flow, streaming line protocol). Anti-patterns confirmed and rejected: editable-canonical storage, silent corpus truncation (prompt-only disclosure), cosmetic multi-turn, permissive-inference Q&A, auto-pause of an evidentiary recording.
