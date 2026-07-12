# Session Details — Tabs + Themed Close Prompt — Design (2026-07-12)

## Overview

The Session Details window stacks all metadata in one long vertical scroll, so speaker
management (the last card) is only reachable by scrolling. Split the content into two
tabs — **Details** and **Speakers** — with a persistent Save/Discard header above them,
and upgrade the existing unsaved-changes close prompt from a plain Windows `MessageBox`
to a themed Fluent dialog.

This is a **view-only reorganization**: no `MetadataEditorViewModel` change. The buffered
edit model (one working copy, `Save` commits everything, `IsDirty` tracks all fields)
already spans all cards, so tabs are purely presentational.

## Current layout (`SessionDetailsWindow.xaml`)

One `ScrollViewer` (`:31`) → `StackPanel` containing:
- Header row (`:36-48`): "Details" heading + `LockHint`, and `Unsaved changes` / Discard /
  Save on the right.
- `<StackPanel IsEnabled="{Binding IsEditable}">` (`:50`) with three `ui:Card`s:
  - **Session** (`:52-61`) — Title, Description.
  - **Classification** (`:64-124`) — Call type, Matters (search + tagged chips + results +
    inline create).
  - **Speakers** (`:132-233`) — Split-speakers button + `DiariseHint`, a 2-column
    Local/Remote slot grid with per-slot rename/remove and Add / Add-unnamed /
    Add-from-roster controls, and the **Archived** checkbox (`:230`).

## Design

### Layout

Restructure the window body (below the `ui:TitleBar`) into:

1. **Persistent header** (Dock Top, outside the tabs): `LockHint` on the left; `Unsaved
   changes` indicator + **Discard** + **Save** on the right — the existing controls
   (`:37-45,48`) lifted up so they apply to both tabs and stay visible. Drop the in-content
   "Details" heading (`:46`) as redundant with the title bar's "Session details".
2. **`TabControl`** filling the remaining space, two `TabItem`s:
   - **Details** — Session card + Classification card + the **Archived** checkbox
     (relocated here from the Speakers card; it is a session property, not a speaker one).
   - **Speakers** — the Speakers card (Split-speakers button, `DiariseHint`, Local/Remote
     slot grid, add controls).
3. Each tab body is wrapped in its **own** `ScrollViewer` (Details is short; Speakers can
   grow tall with many participants).
4. **`IsEditable` gating** moves onto each tab's content panel (not the `TabControl`), so a
   live/pending session shows both tabs read-only but tab-switching still works.

Tab labels: **"Details"** / **"Speakers"** (concise).

### Themed close prompt

The unsaved-changes close guard already exists and works (`SessionDetailsWindow.xaml.cs:79-108`,
`OnClosing`): a dirty editor prompts Save / Discard / Cancel, force-commits a focused
participant-name box first, and uses the `_closeConfirmed` + `SaveThenCloseAsync` re-entrant
pattern because WPF can't await inside `OnClosing`. Only the **dialog** changes:

- Replace the synchronous `System.Windows.MessageBox.Show(...)` (`:96-99`) with the themed
  `Wpf.Ui.Controls.MessageBox` (`Title` "Unsaved changes", content "Save changes to this
  session before closing?", `PrimaryButtonText` "Save", `SecondaryButtonText` "Discard",
  `CloseButtonText` "Cancel"), shown via `await ShowDialogAsync()`.
- Because the themed dialog is async, fold the decision into an async helper: `OnClosing`
  sets `e.Cancel = true` when `IsDirty && !_closeConfirmed` and calls `_ = ConfirmCloseAsync()`.
  `ConfirmCloseAsync` does the existing focused-box force-commit, awaits the dialog, then:
  **Save** → `await SaveCommand`; if not `IsDirty`, set `_closeConfirmed` and `Close()`
  (a failed/declined save leaves it dirty and open — unchanged semantics); **Discard** →
  `DiscardCommand` + `_closeConfirmed` + `Close()`; **Cancel/None** → do nothing (stay open).
- The dialog is shown on a user close action, well after the message pump is up, so the
  Wpf.Ui rendering gotcha (Mica window shown before the pump) does not apply.

## No view-model change

`MetadataEditorViewModel` is untouched. `IsDirty`, `SaveCommand`, `DiscardCommand`, and
every field/participant binding are VM-level, so they work identically whether the controls
live in one scroll or two tabs.

## Notes / edge cases

- **Pending renames across a tab switch.** The per-slot name box binds `Text` OneTime and
  commits via `LostFocus → RenameParticipant` (`.xaml:164-169,203-208`, `.xaml.cs:128-132`).
  Switching from the Speakers tab moves focus, which fires `LostFocus` and commits the
  rename before the Speakers content unloads — so a half-typed name is not lost on tab
  switch (same mechanism the close guard already relies on). Worth a smoke check.
- **Keyboard save** (`Ctrl+S`, `.xaml:22`) is window-scoped and unaffected by tabs.
- The `Chip` style and matter controls move verbatim into the Details tab.

## Testing / verification

- Manual smoke (the change is XAML + code-behind, not VM logic): both tabs render; Save /
  Discard / dirty indicator work from either tab; Archived appears and persists from the
  Details tab; a read-only (live/pending) session shows both tabs disabled but switchable;
  typing a participant name then switching tabs commits the rename; closing with unsaved
  edits shows the **themed** Save/Discard/Cancel dialog and each button behaves (Save
  commits then closes, Discard closes losing edits, Cancel stays).
- Existing `MetadataEditorViewModel` unit tests remain green (no VM change).
- `XamlHygieneTests` still pass (no new app-global implicit styles).

## Out of scope

- Any change to the metadata edit model, matters picker, or speaker/diarisation logic.
- The Split-speakers button's 1-on-1 gating papercut (tracked separately under the
  Split-speakers relabel item).
