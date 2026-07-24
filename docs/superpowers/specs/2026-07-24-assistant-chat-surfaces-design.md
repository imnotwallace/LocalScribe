# Assistant chat + summary surfacing — design (2026-07-24)

## Problem

The local-assistant shipped functional but effectively undiscoverable and inconsistent:

- **Session chat is buried.** It lives on the Session Details → Assistant tab, a tab-deep panel in a
  secondary window. A user reading a transcript (the primary artifact) has no affordance anywhere
  near it that chat exists, and cannot read the transcript and ask about it at the same time.
- **Matter chat is unintuitive.** The Matters detail tab strip wraps into a broken-looking two-row
  grid, and the matter chat input floats orphaned at the very bottom of the window, detached from
  the "Assistant" tab that owns it.
- **Summaries have no home outside a matter.** A session's summary is viewable only in the buried
  Session Details Assistant tab. A session not tagged to any matter has no natural place to view its
  summary, and there is no way to scan summaries across sessions.
- **Chat is stateless and single-log.** Each session/matter has exactly one append-only `chats.json`;
  every question ever asked accumulates into one unending list, and each question is answered blind
  to the ones before it (single-turn to the model — no conversation memory).
- **A layout bug:** the Sessions action bar is a horizontal `StackPanel` that never wraps, so when the
  nav rail expands (narrowing the content pane) the **Delete…** button runs off-screen.

## Goals

- Read a transcript and chat about it **in the same window, at the same time**.
- One reusable Assistant panel used **identically** for session and matter scope — learn it once.
- A session's summary is viewable wherever you read that session, plus a cross-session overview.
- Chat behaves like a **real threaded conversation** (memory within a thread), organised into
  **multiple named threads** per scope, with graceful handling when a thread outgrows the context.
- Degradation is always surfaced, never silent (the established evidentiary posture).

## Non-goals

- Changing the transcript/evidence model. Transcripts stay append-only and are never edited by this
  work; only AI-derived chat/summary surfaces change.
- A global assistant "inbox" or notifications. Out of scope.
- Cross-session *chat* (asking one question across many sessions) beyond the existing matter scope.

## Decisions (locked with the user, 2026-07-24)

1. **Session chat moves into the transcript window** as a collapsible right-side "Ask" panel. The
   Session Details Assistant tab is **removed** entirely.
2. The panel is a **shared reusable control**: Summary + Chat for a session; Chat-only for a matter.
3. **Multiple named chat threads** per scope (session *and* matter): selector dropdown + New +
   inline Rename + **Archive** (hide, keep on disk) + "Show archived" toggle.
4. **Threaded conversation with memory**: each question is sent with that thread's prior turns.
5. **Overflow → auto-summarize** the oldest turns into a persisted running **recap** the model keeps
   seeing; the transcript context is always kept intact; a visible indicator shows when a condense
   has happened. **New chat** = a fresh, fast context.
6. **Transcript text reflows** to the available width as the panel opens and the splitter drags —
   live, no horizontal scroll. Same for matter-detail content.
7. **Matter chat mirrors** the session panel (Chat-only); the matter Assistant tab is **removed**;
   the matter **Sessions tab gains a Summary column**; the **tab strip is fixed** to a single row.
8. **Cross-session summaries overview**: a **Summary column on the main Sessions list**.
9. **Sessions action-bar bug**: `StackPanel` → `WrapPanel`.
10. **Data model**: `chats.json` v1 → **v2** (named threads + per-thread recap), with **forward
    migration** of existing v1 logs.

## Architecture

One reusable panel, one chat-threading engine, and per-host wiring. Units, each with a single
responsibility and a defined interface:

### Core (LocalScribe.Core.Assistant)

- **`AssistantChatStore` (v2 rewrite).** `chats.json` becomes a list of named threads. Shape:

  ```
  AssistantChatLog { int SchemaVersion=2; IReadOnlyList<AssistantChatThread> Chats }
  AssistantChatThread {
      string Id; string Name; DateTimeOffset CreatedAt; bool Archived;
      string? Recap;                    // condensed older turns (null until first condense)
      string? RecapThroughTurnId;       // last turn folded into Recap (provenance)
      IReadOnlyList<AssistantChatTurn> Turns   // verbatim, chronological append order (as today)
  }
  ```

  `AssistantChatTurn` is unchanged (still additive-safe). New surface: `LoadAsync`,
  `SaveThreadAsync(thread)` (append a turn / update recap / rename / set-archived — a thread is
  rewritten atomically), `NewThread(name)`, and the migration below. The old append-only "no update
  surface" rule is relaxed for **thread metadata and recap only**; individual turns remain
  append-only within a thread.

- **v1 → v2 migration.** `SchemaGuard` currently `RejectIfNewer`. v2 `LoadAsync` must **read v1
  forward**: a v1 `{ Turns:[...] }` opens as a single thread `{ Name:"Chat 1", CreatedAt: <first
  turn or file time>, Turns: <the v1 turns>, Recap:null, Archived:false }`. A v2 reader never
  rejects a v1 file; only a *newer-than-v2* file fails loud. The migration is pure and unit-tested.

- **`AssistantConversation` (new, pure prompt builder).** Given the scope context (speaker preamble +
  transcript/matter context), a thread's `Recap` + verbatim `Turns`, and the new question, builds the
  ChatML multi-turn prompt: system/context → recap (if any) → prior turns as alternating
  user/assistant → the new question. Pure `-> string`, unit-tested. This is where "memory" lives.

- **Budget + condense policy (in `AssistantQaService`).** Before each ask, estimate tokens
  (`TokenBudget`). Priority order that must always hold: **transcript/scope context is kept whole**;
  the remaining budget holds `Recap` + as many recent verbatim turns as fit. When history+context
  would overflow, **condense**: summarize the oldest not-yet-recapped turns into `Recap` via one
  helper call (the same gated engine path summaries use), advance `RecapThroughTurnId`, drop those
  turns from the verbatim window, persist the thread, and repeat until it fits. The condense call is
  gated (one engine at a time), cancellable by a recording start, and **persists nothing on failure**.
  When the scope context alone already exceeds budget the existing too-long error still applies.

- **`AssistantQaService` threading.** `AskAsync` gains the active thread: it builds the prompt via
  `AssistantConversation` (context + recap + prior turns + question), runs the condense policy first,
  then appends the answered turn to that thread. Warm-session KV reuse still keys on the byte-identical
  **scope-context prefix** (shared across all threads of one scope), so switching threads never
  reloads the transcript — only that thread's own history/recap re-prefills per ask.

### App (LocalScribe.App)

- **`AssistantSidePanel` (new control).** A right-docked, collapsible panel with a `GridSplitter` and
  an "Ask" toggle (placed in the host toolbar). Hosts an optional **Summary** section (the existing
  summary VM: version switcher, Regenerate, rendered text, stale badge) and a **Chat** section (the
  existing chat panel, wrapped with a thread selector + New/Rename/Archive/Show-archived). Remembers
  open/closed per window; **opens by default when the scope has any summary or chat history**, else
  stays closed so pure reading is unchanged. The condense indicator ("· earlier turns condensed")
  renders in the chat header when `Recap` is non-empty.

- **Chat-thread VM (new/extended).** Wraps `AssistantChatViewModel` with thread management: the
  bindable thread list (non-archived), `SelectedThread`, `NewChatCommand`, `RenameCommand`,
  `ArchiveCommand`, and a `ShowArchived` toggle. Switching `SelectedThread` swaps the rendered turn
  list; the warm helper is untouched (shared scope prefix).

- **`ReadViewWindow` wiring (Phase 2).** Host `AssistantSidePanel` (Summary + Chat) bound to the
  session. Move the chat lifecycle currently in the Session-Details open-path here: service factory
  bound to `sessionId`, `LoadHistoryAsync`, `Shutdown` on window close, `CancelForRecording` on
  record start, `InvalidateContext` on `SessionContentChanged`, and — the improvement —
  **citation clicks scroll *this* window's transcript** (via the existing `ShowFindAt`) instead of
  opening a second read view. The transcript list must wrap to available width (no fixed width, no
  horizontal scroll) so it reflows as the splitter moves.

- **`SessionDetailsWindow` (Phase 2).** Remove the Assistant tab (summary + chat now live in the read
  view). Keep Details/speakers/matters/etc.

- **Matters detail (Phase 3).** Host `AssistantSidePanel` (Chat-only) toggled from the matter header;
  remove the Assistant tab. Add a **Summary column** to the matter's Sessions tab (none/done/stale +
  Generate). Fix the tab strip to a **single non-wrapping row**. Matter content reflows when the panel
  opens.

- **Sessions list (Phase 4).** A **Summary column** (none/done/stale + Generate/open) driven by a
  small **summary-status provider** that reads each session's latest `SummaryVersion` + stale flag
  during the session scan.

- **Sessions action bar (Phase 0).** `StackPanel` → `WrapPanel` (matches the filter row above it).

### One warm helper at a time

At most one warm **chat** helper is resident globally: opening/asking in one scope's chat tears down
any other scope's warm session; a recording start cancels all chat (existing rule). This keeps two
~2.5 GB model processes from ever co-residing on a 4 GB GPU / 16 GB box. Threads within one scope
share the single warm helper.

## Data flow

1. User opens a transcript → `ReadViewWindow` builds the read VM and an `AssistantSidePanel` bound to
   the session; panel opens if a summary/history exists.
2. User clicks "Ask" (or it is already open), picks or creates a thread, types a question.
3. `AssistantQaService.AskAsync`: acquire engine lease (gated behind any recording) → run condense
   policy (may fold older turns into `Recap`, persisting the thread) → build prompt via
   `AssistantConversation` → stream the answer → validate citations → append the turn to the thread.
4. A citation chip click scrolls the current transcript window to the segment.
5. On record start, chat is cancelled (nothing persisted mid-answer); on window close, the warm
   helper is torn down.

## Error handling

- Nothing persists on a failed or cancelled ask or condense (existing rule; extended to the condense
  call).
- A missing model/helper shows the existing model-AND-helper disabled explainer in the panel.
- `chats.json` newer than v2 fails loud; v1 migrates forward silently and losslessly.
- A CUDA-to-CPU fall on a chat turn is already recorded (`AssistantChatTurn.CudaFellToCpu`) and shown
  on the turn's provenance line; a fall on a **condense** call is recorded the same way on the recap's
  provenance (or surfaced as a subtle note).
- Overflow degradation (a condense happened) is surfaced by the panel's condense indicator — never
  silent.

## Phasing

Each phase is independently shippable and separately reviewable.

- **Phase 0 — action-bar wrap.** `StackPanel` → `WrapPanel`. Trivial; ship first.
- **Phase 1 — chat threading engine (backend).** `AssistantChatStore` v2 + migration,
  `AssistantConversation`, threaded `AssistantQaService` with the condense policy. Wired into the
  **existing** chat surfaces so it is fully testable before any UI moves. This is the largest and
  highest-risk phase (data model + conversation memory + budget math).
- **Phase 2 — session panel.** `AssistantSidePanel` + thread-management VM; host it in
  `ReadViewWindow` with Summary + Chat, transcript reflow, in-window citation scroll; remove the
  Session Details Assistant tab.
- **Phase 3 — matters.** Mirror the panel (Chat-only) in the Matters detail; remove the matter
  Assistant tab; Sessions-tab Summary column; tab-strip single-row fix; matter reflow.
- **Phase 4 — cross-session overview.** Summary column on the main Sessions list.

## Testing

- **Store v2 + migration** (Core, unit): v1 flat log opens as one "Chat 1" thread losslessly;
  round-trip of multi-thread v2; archived threads hidden from the active list but retained on disk;
  a newer-than-v2 file fails loud.
- **`AssistantConversation`** (Core, unit): the built prompt includes recap then prior turns then the
  question in ChatML order; empty recap/first-turn cases; ordering stable.
- **Condense policy** (Core, unit with a fake runner): transcript context is always retained; older
  turns fold into `Recap` and drop from the verbatim window only when over budget; `RecapThroughTurnId`
  advances; nothing persists if the condense call errors/cancels.
- **Thread-management VM** (App, unit): New/Rename/Archive/switch; `ShowArchived` toggle; selecting a
  thread swaps the rendered turns; archived thread excluded from the active list.
- **Availability** (App, unit): the panel's disabled explainer reflects model-AND-helper, reusing the
  existing gate.
- **Reflow + layout** (manual/GUI smoke): transcript wraps to width with the panel open and reflows as
  the splitter drags (no horizontal scroll); the Sessions action bar wraps Delete… into view when the
  rail expands; the matter tab strip stays a single row.
- **Real-model smoke** (manual, runbook): a genuine multi-turn thread where a follow-up needs memory
  ("and who agreed to that?") answers correctly; a long thread triggers a condense and the indicator
  appears while answers stay coherent; switching threads is instant (no transcript reload).

## Risks

- **Condense latency.** On CPU/4 GB, a condense is an extra multi-token model call — tens of seconds —
  pausing a long thread mid-conversation. Mitigated: it fires only when a thread actually overflows,
  and the indicator explains the pause. Short threads never pay it.
- **Budget correctness.** If the budget math under-counts, a prompt could exceed the window and the
  helper errors. Mitigated: the transcript context is sized first (existing `TokenBudget` path), the
  condense loop is conservative, and the too-long error remains the floor.
- **v2 migration is load-bearing.** A bug that mis-reads v1 loses chat history. Mitigated: pure,
  unit-tested migration; v1 files are only ever read, never rewritten in place until the user asks a
  new question in that (now-migrated) thread.
- **Scope creep across four phases.** Mitigated by strict phase boundaries; Phase 1 delivers the
  engine behind the existing UI, so value lands even if later phases slip.
