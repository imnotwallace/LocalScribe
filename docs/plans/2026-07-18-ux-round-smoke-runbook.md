# UX round 2026-07-18 - manual smoke runbook (user)

Prereq: >50 sessions on disk to exercise multi-page states (or temporarily set the page size to 25).

## P - Pagination
- P1 Sessions page: footer shows "Page 1 of N"; Previous/Next page through; no half-clipped bottom row.
- P2 Page-size picker: 25/50/100 re-slices and rewinds to page 1.
- P3 Typing in "Filter sessions..." rewinds to page 1.
- P4 Stop a recording while on page 2: the page does not jump (finalizing row upserts in place or off-page).
- P5 Search page: query with many hits pages by session card; new query rewinds to page 1.

## S - Search defaults
- S1 Fresh app start -> Search page: combos SHOW "All matters" and "All apps"; both date pickers empty.
- S2 Pick an app facet, navigate away and back: the picked value survives (singleton VM) and still filters.

## F - Find escalation
- F1 Read view: Find button visible next to Edit; opens the bar with the box focused; Ctrl+F unchanged.
- F2 "Search all sessions" with a term: main window opens/activates on Search, term pre-filled, facets All/All/blank.
- F3 Same with the main window closed first (tray path): fresh window lands on Search, not Sessions.
- F4 Search-page snippet click still deep-links into the read view at the right segment.

## M - Matters overhaul
- M1 Select a matter: right pane = header (name, ref, client chip, created) + tabs, opening on Sessions.
- M2 Open / double-click a tagged session -> transcript read view; Details -> Session Details; Untag still confirms.
- M3 Open on a pending-recovery session -> Info toast, no window.
- M4 Add sessions...: lists only untagged+unarchived, filter works, multi-select tags all; grid + "N session(s)" count + Sessions-page chips all refresh.
- M5 Details/Vocabulary/Advanced tabs: old editors work (rename cascades, vocab add/remove, re-render, export, delete-blocked-while-tagged).
- M6 No whole-pane scrollbar; the sessions grid pages instead of scrolling forever.

## C - Record console
- C1 With the LifeCam mic selected: "Microphone" label fully visible; device name ellipsized with tooltip.
- C2 "Remote target" row same; mid-recording capture-scope combo same.
