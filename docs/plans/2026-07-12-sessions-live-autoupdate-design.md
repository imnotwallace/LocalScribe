# Sessions List Live Auto-Update — Design (2026-07-12)

## Overview

The Sessions manager list does not update itself: after stopping a recording (or during
a startup crash-recovery), a row sits at **"Recovering…"** until the user clicks
**Refresh**. This makes the manager feel stale and is a repeated annoyance. This design
makes the list update itself for the two **completion** cases — a just-stopped
recording finishing its background finalize, and a startup recovery completing — without
a manual refresh, and without disturbing the user's scroll position or selection.

Scope was chosen as **targeted** (completion cases only), not a broadly live list.

## Root cause of the "stuck Recovering…" row

Stop does not finalize synchronously. On the clean path, `StopAsync` finalizes audio,
sets `_finalizing`, kicks a background finalize task, and returns `Idle` immediately
(`SessionController.cs:896-900`):

```csharp
_finalizing = s;
_pendingFinalize = FinalizeInBackgroundAsync(s, durationMs, endedAtUtc);   // not awaited
_session = null;
SetState(SessionState.Idle);
```

The final `session.json` (with `EndedAtUtc`) is written **later**, on that background
task, in `FinalizeInBackgroundAsync → PersistFinalAsync` (`:957`, `:990-1000`), after the
transcription tail drains.

The Sessions list refreshes once, on the `State → Idle` transition
(`SessionsPageViewModel.cs:122-126`) — which fires at the **start** of the background
finalize, **before** `EndedAtUtc` is written. `SessionRowViewModel` computes
`IsPendingRecovery = session.EndedAtUtc is null` (`SessionRowViewModel.cs:77`), so the
just-stopped row renders the **"Recovering…"** chip (the same chip a genuinely
interrupted session gets). Nothing refreshes again when the finalize actually completes,
so the row stays "Recovering…" until a manual Refresh. The `Idle == finalized-on-disk`
assumption behind the refresh trigger (2026-07-03) predates the 2026-07-08 async
drain-then-finalize change that made finalize asynchronous.

Secondary issue: every refresh path funnels through `ApplyFilters`, which does
`Rows.Clear()` then re-adds (`SessionsPageViewModel.cs:181-196`). Clearing the bound
`ObservableCollection` raises a Reset and the virtualized `ui:DataGrid` scrolls back to
top — so any auto-update built on the current refresh methods (`LoadAsync` /
`RefreshRowAsync`, `:357-374`, which also calls `ApplyFilters`) jumps the user's scroll.

## Section 1 — Completion signal (Core, minimal)

There is exactly one moment when "this session's row is now settled on disk": the
`finally` block of `FinalizeInBackgroundAsync` (`SessionController.cs:975-981`), which
runs on **both** the success path (after `PersistFinalAsync` wrote `EndedAtUtc`) and the
failure path (after `ErrorRaised("FINALIZE_FAILED")` at `:969`, where `EndedAtUtc` was
never written). React to that.

- Add one event: `public event Action<string>? SessionFinalizeCompleted;` raised in that
  `finally`, **after** `_finalizing` is cleared. Passing the session id. It fires whether
  finalize succeeded or failed; the handler re-reads disk truth, so one event covers both
  (success → row final; failure → row stays un-ended, honestly pending).
- Expose the in-flight id: `public string? FinalizingSessionId => _finalizing?.Id`
  (surfaced through `SessionViewModel`). Used only for a cosmetic label (Section 4), so a
  benign cross-thread read is acceptable.

No other Core change. The existing `ErrorRaised("FINALIZE_FAILED")` / `Notice`
(`:969-970`) stay as the user-facing failure surface.

## Section 2 — Non-disruptive in-place upsert (App VM)

Add `Task UpsertRowAsync(string id)` to `SessionsPageViewModel` that updates a single row
without a collection Reset (so scroll + selection survive):

- Load the one session item (`MaintenanceService.LoadSessionItemAsync`), build a fresh
  immutable `SessionRowViewModel` (rows never mutate; a refresh replaces the object).
- If the id already exists in `_all`: replace it in `_all`, and if it is currently in
  `Rows`, replace by index — `Rows[i] = newRow` raises an `ObservableCollection` **Replace**
  (not Reset), so the DataGrid keeps its scroll offset and the selection (kept by id) holds.
- If the id is new (a just-stopped session not yet cached): insert into `_all` at the
  correct sorted position (newest-first, matching `LoadAsync`), and insert into `Rows` at
  the matching position **iff** it passes the active filters (`ShowArchived`, `FilterText`,
  `MatterFilterId`).
- If the row now fails the active filters (edge case), remove it from `Rows` in place.
- Rebuild the matter-filter options afterward (`RebuildMatterOptions`) — cheap, and it
  touches only `MatterFilterOptions`, never `Rows`, so it has no scroll impact.
- Marshal all UI mutations through the injected `_dispatch`, and catch everything (the
  wiring is fire-and-forget, mirroring `RefreshRowAsync`), so a stray upsert never escapes
  as an unobserved exception.

`LoadAsync` (full rebuild) is unchanged and remains the path for page navigation and the
Refresh button. `RefreshRowAsync` may be reimplemented on top of `UpsertRowAsync` (both do
a single-row reload; the difference is that `UpsertRowAsync` does not clear `Rows`).

## Section 3 — Wiring (App layer, dispatch-marshaled)

All controller events are already wired as direct delegates in `App.xaml.cs` over the
`CompositionRoot` graph (e.g. `detailEditor.Saved += id => sessionsVm.RefreshRowAsync(id)`
at `App.xaml.cs:220`). Follow that idiom:

- **On Stop (`State → Idle`):** change the trigger at `SessionsPageViewModel.cs:122-126`
  from a full `LoadAsync` to a single-row upsert. The just-stopped id is
  `FinalizingSessionId` (set at `:896`, before the `Idle` transition at `:899`; note
  `CurrentSessionId` is already null because `_session` is cleared at `:898`). If
  `FinalizingSessionId` is non-null → `UpsertRowAsync(id)`; the row appears immediately,
  labeled **"Finalizing…"**, with no scroll jump. If it is null (the rare synchronous
  fault-path Stop at `:902-921`, which reaches `Idle` without a background finalize) →
  fall back to a full `LoadAsync` so the audio-only row still appears.
- **On `SessionFinalizeCompleted(id)`:** `dispatch(() => _ = sessionsVm.UpsertRowAsync(id))`
  → the row flips to final (duration + status), or, on a failed finalize, to pending plus
  the existing failure `Notice`.
- **On each recovery completion:** the startup recovery loop
  (`MaintenanceService.RecoverAllAsync`, ~`:381-400`, driven by `StartupOrchestrator`)
  invokes `UpsertRowAsync(id)` per recovered session — an App-layer per-id callback, **no
  Core change** — so a long scan updates rows one-by-one as each finishes instead of only
  at batch end (today's single `ScanCompleted → RefreshCommand` at `App.xaml.cs:366-370`).
  The batch-end refresh may remain as a final reconcile.

## Section 4 — "Finalizing…" vs "Recovering…" label

`SessionRowViewModel` currently renders any `EndedAtUtc == null` row as **"Recovering…"**
(`SessionRowViewModel.cs:77`, chip in `SessionsPage.xaml`). Split the state so a normal
post-Stop finalize is not mislabeled as a crash recovery:

- The VM passes an `isFinalizing` flag when constructing a pending row, computed as
  `id == FinalizingSessionId`.
- `isFinalizing` → **"Finalizing…"** chip; otherwise (a true `EndedAtUtc == null` with no
  in-flight finalize) → **"Recovering…"** chip.

Both are self-clearing: once `SessionFinalizeCompleted` upserts the row, `EndedAtUtc` is
set (or `FinalizingSessionId` is cleared), so the chip resolves to the final status (or,
on failure, honestly to "Recovering…" — it now genuinely awaits the next recovery scan).

## Section 5 — Testing

- **Core (`SessionController`):** `SessionFinalizeCompleted` fires exactly once on the
  clean finalize path **and** once on the failure path (fake a `PersistFinalAsync` /
  worker fault); `FinalizingSessionId` equals the in-flight id between Stop and the event,
  and is null before Stop and after completion.
- **VM (`SessionsPageViewModel`):** `UpsertRowAsync` replaces an existing row **without a
  collection Reset** (assert via an `ObservableCollection.CollectionChanged` probe that the
  action is Replace, not Reset) and preserves `SelectedRow`; inserts a brand-new row at the
  correct sorted position and only when it passes active filters; rebuilds matter options
  without clearing `Rows`; a finalize-failed upsert leaves the row pending.
- **Label:** a pending row with `id == FinalizingSessionId` renders "Finalizing…"; a
  pending row that is not the finalizing id renders "Recovering…".

## Out of scope (targeted choice)

- New sessions created by *other* processes appearing live.
- Cross-window edit propagation beyond the existing `RefreshRowAsync` (Session Details
  Save, diarisation save, untag) wiring.
- A polling/`FileSystemWatcher` backstop: the only case the event misses is an app crash
  *mid-finalize*, which leaves the session un-ended on disk and is already finalized by the
  **next startup recovery scan** — so no timer is warranted (and `FileSystemWatcher` is
  explicitly rejected by the Stage-4 manager design).
