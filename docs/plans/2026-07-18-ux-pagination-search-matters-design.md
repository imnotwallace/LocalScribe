# UX round 2026-07-18 — pagination, search entry points, Matters overhaul, console label fix

**Status:** Design approved by user (brainstormed 2026-07-18, incl. visual mockup round for the Matters pane).
**Scope:** Six items: shared pagination (Sessions + Search), search default-facet display bug, transcript Find entry + escalation, Matters right-pane overhaul (header + tabs + Add sessions picker + Open-opens-transcript), Record-console label truncation, tests.

---

## 1. Shared pagination (Sessions list + Search results)

**Decision:** Classic pager — "Page N of M", Prev/Next, page-size picker (25/50/100, **default 50**) — rendered as a footer bar under each list. No pagination infrastructure exists anywhere in the app today; build it once, reuse it.

**Components:**

- `PagerViewModel` (App, new): `CurrentPage` (1-based), `PageSize`, `TotalCount`, derived `PageCount` (min 1), `CanGoPrev/CanGoNext`, `PrevCommand/NextCommand`, `PageSizes = [25, 50, 100]`, and a `PageChanged` event (or callback) the host VM uses to re-slice. `Reset()` returns to page 1. Changing `PageSize` re-clamps `CurrentPage` so the first visible item stays on-page where practical (acceptable simplification: reset to page 1).
- `PagerControl` (App, new XAML UserControl): Prev/Next buttons, "Page N of M" text, page-size ComboBox, hidden when `TotalCount == 0`. Bound to a `PagerViewModel` DataContext.

**Sessions page** (`SessionsPageViewModel`): today `LoadAsync` builds `_all`, and `ApplyFilters` filters in-memory into the bound `Rows`. Insert paging after filtering: filtered list → `Pager.TotalCount` → slice `[(CurrentPage-1) * PageSize, PageSize)` → `Rows`. Any filter change (text, matter facet, show-archived, content search) calls `Pager.Reset()`. Refresh keeps the current page if still in range, else clamps. Selection: if the selected session leaves the current page, selection clears (no cross-page selection tracking). The clipped-bottom-row artifact disappears as a side effect of bounded pages; if any DataGrid clipping remains it is fixed in the same task.

**Search page** (`SearchPageViewModel`): page over **result cards** (one card = one session with its snippets — a session's snippets never split across pages). `SearchQueryEngine.Run` still returns the full ranked match list (ranking is hit-count → recency → id and needs the full set); the VM stores it and slices the current page into `Results`. New query / facet change resets to page 1. No change to `SearchQuery` or the index API.

**Out of scope:** back-end limit/offset in `SearchQueryEngine` / `SessionCatalog`; UI-level virtualization changes.

## 2. Search default-facet display bug (fix, not redesign)

`SearchPageViewModel` already defaults to All matters / All apps / no date bounds (`MatterFilterId == null`, `AppFilterId == null`, `FromDate/ToDate == null`). The combos fail to *display* the null default as their "All …" item — observed as a blank matter combo and "Zoom" showing in the app combo. Fix the ComboBox bindings on `SearchPage.xaml` (SelectedValue/SelectedValuePath resolution for the null-id sentinel — likely switch the "All" sentinel from `null` to a non-null id, e.g. `""`, so WPF selection matching works) so both combos visibly show "All matters" / "All apps" on first open. Add a VM regression test pinning the default facet values, and ensure the displayed selection can never silently diverge from the facet actually applied to the query.

## 3. Transcript Find: visible entry + escalation to global search

Two additions to `ReadViewWindow` (Find bar itself already exists; Ctrl+F, Enter/Shift+Enter/Esc, wrap-around, highlight styling all stay unchanged):

- **Find button** in the read-view header next to Edit. Tooltip: "Find in transcript (Ctrl+F)". Invokes the existing `OpenFind()`.
- **"Search all sessions"** button on the Find bar. Raises a new VM event `SearchAllSessionsRequested(string term)` with the current Find text (empty term allowed → opens Search blank). `App.xaml.cs` wiring: activate/focus the main window, navigate to the Search page, set the query text (facets stay at their defaults — all matters / all apps / all dates — not inherited from the session). The existing debounce runs the search.

This completes the two-way link: Search page → transcript already exists via `ShowFindAt(seq, term)`; this adds transcript → Search page.

## 4. Matters page right-pane overhaul

**Decision (from mockup round):** compact **display-only header** + **four tabs — Details | Sessions | Vocabulary | Advanced — opening on Sessions**. The giant right-pane `ScrollViewer` is removed; each tab manages its own layout.

**Header** (display-only): matter name, reference, client chip (first Client-role member), created date. No edit affordances; editing lives in the Details tab.

**Sessions tab (default):**

- A real grid (same virtualizing DataGrid/ListView pattern as the Sessions page) with columns Title / Date / Duration, footer `PagerControl` (shared pager from §1).
- Toolbar: **Add sessions…** (primary), **Open**, **Details**, **Untag**, plus a filter box over the tagged list (title substring; filter change resets the pager to page 1).
- **Open (and row double-click) opens the transcript read view** — new event `OpenReadViewRequested(sessionId)` on `MattersPageViewModel`, wired in `App.xaml.cs` to the same deduplicated read-view factory the Sessions page uses. This deliberately reverses the earlier "read view stays reachable from the Sessions page only" decision (the code comment at the wiring site must be updated).
- **Details** keeps the current behavior (Session Details window) as the secondary action.
- **Untag** keeps its existing guard (refuses while the session is open in another window).

**Add sessions… picker** (new dialog window):

- Lists sessions **not already tagged** to this matter and not archived: columns title / date / source, filter box, multi-select via checkboxes, OK/Cancel.
- OK applies the tag to all selected sessions through the **same `MaintenanceService.SaveMetaAsync` matter-tag delta path** the Session Details picker uses — one save per session, sequentially — so search-index updates and vocabulary/re-render semantics stay byte-identical to tagging from Session Details. Errors on individual sessions are reported (not silently skipped) and do not abort the remainder.
- After OK: refresh the tagged list; new rows appear per current sort; pager total updates.

**Details tab:** current name / reference / description / roster (members with roles, Add member, Remove) editing UI moved as-is.

**Vocabulary tab:** Terms, Corrections (heard → correct), Re-render tagged sessions — moved as-is.

**Advanced tab:** Repair index, Export matter archive…, Delete matter — moved as-is.

**Non-goals:** no change to matter storage/schema, tagging semantics, vocabulary behavior, or the left-hand matter list.

## 5. Record console — "Microphone" label truncation

On the Record console form, a long device name squeezes the label column ("Microphone" renders as "ophone"). Fix: give the label column a fixed/auto width that never collapses below the label's natural size (`Grid` with `ColumnDefinition Width="Auto"` and the combo in a `*` column, or equivalent), and let the **device combo** content truncate instead — `TextTrimming="CharacterEllipsis"` on its content presenter with the full device name in a tooltip. Audit the sibling rows on the same form ("Remote target", any future rows) for the same pattern.

## 6. Testing

VM-level unit tests (no new UI-automation surface):

- **Pager math:** page count derivation, clamping, Prev/Next enablement, reset-on-filter-change, page-size change behavior; Sessions and Search VMs slice correctly and reset on facet/filter/query changes.
- **Search defaults:** facets default to all-matters/all-apps/no-dates; displayed sentinel maps to the null facet in the executed query.
- **Find handoff:** `SearchAllSessionsRequested` carries the current term; Search VM applies it with default facets.
- **Matters:** sessions-tab paging; Open raises the read-view event (not details); Details raises the details event; Add-sessions picker excludes already-tagged + archived sessions; OK produces the same tag delta per session as the Session Details path; per-session error does not abort the batch.
- Manual smoke list (user): tab layout & header rendering, truncation fix at 3+ device-name lengths, Find button + Search-all-sessions round trip, pager on all three lists, Add sessions picker end-to-end, transcript-open from Matters.

## Locked user decisions (this round)

1. Classic pager (not infinite scroll / load-more), default 50/page, sizes 25/50/100.
2. Read-view Find gets a visible button **and** a "Search all sessions" escalation (not embedded global search).
3. Matters tagged-session primary action opens the **transcript**; Session Details demoted to a secondary "Details" action.
4. Adding sessions to a matter happens via an **"Add sessions…" multi-select picker dialog** on the Matters page.
5. Matters right pane = **B's compact header + A's tabs**, tabs **Details | Sessions | Vocabulary | Advanced**, default **Sessions**; editing lives in the **Details tab** (no edit dialog).
6. Search facet defaults are a **display-binding bug fix**, not a behavior change.
