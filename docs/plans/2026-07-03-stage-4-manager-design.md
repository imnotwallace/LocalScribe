# Stage 4 Design: Main Window - Session/Matter Manager, Settings, Consent

Date: 2026-07-03
Status: Approved (brainstorm dialogue 2026-07-03); supersedes nothing - refines the
master design (docs/plans/2026-06-30-localscribe-design.md, build-sequence step 4)
and the specs (docs/specs/localscribe-specs.md) with the decisions recorded below.

## 1. Scope

Stage 4 delivers the app's first main window and the organizational layer over the
already-complete Stage 2a persistence:

- Sessions page: session browser over `storageRoot/sessions/*` with metadata editor.
- Matters page: Matter CRUD, rosters, two-level Matter -> Session organizer.
- Settings page: full settings UI (spec section 7) plus the app's first settings
  save/propagation path.
- Session read view: read-only transcript windows with audio playback.
- First-run consent notice.
- Startup recovery scan (wires `SessionWriter.RecoverIfNeededAsync`).
- Matters index recompute/rebuild (the Stage 4 self-heal promised by
  `MatterStore.cs:5-7`).
- AppKind derivation from the resolved capture target for manual starts
  (the deferred 3b refinement).
- Infrastructure: single-instance guard, capture exclusion for transcript windows,
  keyed window-state, per-command error surfacing, maintenance/write serialization.

### User decisions (2026-07-03)

| Question | Decision |
|---|---|
| 3b's "Stage 4 and/or 7" group | Full settings UI: Stage 4. First-run consent notice: Stage 4. Global hotkeys: DROPPED (see 1.1). |
| Whole-session delete | Recycle Bin + one confirmation dialog (not high-friction, not omitted). |
| "Archive" verb | Archived flags on BOTH matters and sessions (additive schema fields); hidden from default views behind a "show archived" toggle; nothing leaves disk. |
| Read view depth | Display-only transcript PLUS audio playback; correction editing stays Stage 6. |
| Export in the manager | Omitted entirely until Stage 6 (no stubs); Reveal-in-Explorer is the hand-off path. |
| Screen-share visibility | Manager, read views, AND the existing live view are capture-excluded by default (WDA_EXCLUDEFROMCAPTURE) with a Privacy settings toggle to make them shareable. |
| Window architecture | One main FluentWindow with WPF-UI NavigationView (Sessions / Matters / Settings); each session read view is its own window (open several side by side); live view stays a separate window as in 3b. |

### 1.1 Global hotkeys: dropped

Verified 2026-07-03 against vendor documentation: the schema defaults collide with
the meeting apps themselves - Webex App binds Ctrl+Alt+R (Remove attendee) and
Ctrl+Alt+P (show/hide video view while sharing; a GLOBAL shortcut), and Teams binds
both combos in-app. A Win32 RegisterHotKey consumes the combo system-wide, so
LocalScribe would silently steal those shortcuts from the meeting app (and Webex's
global Ctrl+Alt+P would race ours by launch order). Per user decision, Stage 4 ships
no global hotkeys; `settings.Hotkeys` stays in the schema, unwired and not exposed
in the settings UI. If hotkeys ever return (post-Stage-7 concern), they need
conflict-free combos plus a rebind UI.

### 1.2 Non-goals (unchanged from master design unless noted)

- No full-text search (in-memory title filter only; FTS index remains deferred).
- No correction editing, no vocabulary UI (global or per-Matter) - Stage 6.
- No export (.zip/.docx) and no disabled export stubs - Stage 6.
- No global hotkeys (dropped, 1.1).
- No session folder renames (folder ids frozen at creation; title lives in meta.json).
- No storage-root data migration (root change = restart-required pointer swap; old
  sessions stay in the old root and drop out of the list - explicit warning in UI).
- No transcript-position-synced audio playback (simple transport only).
- No meeting auto-detect (seam stays disabled), no diarisation UI (Stage 5).

## 2. Window architecture

- `MainWindow`: ui:FluentWindow + Mica, WPF-UI NavigationView with three pages -
  Sessions (default), Matters, Settings. Metadata editor is an inline detail pane
  on the Sessions page, not a dialog.
- `ReadViewWindow`: one instance per opened session; plain closable windows.
- Existing `LiveViewWindow` / `OverlayWindow` / tray unchanged in role. Live view
  keeps hide-on-close (a recording must never die with a window); MainWindow and
  read views genuinely close (nothing depends on them).
- Tray: menu gains "Open LocalScribe" as the first item; double-click retargets
  from live view to MainWindow; "Open live view" and "Open sessions folder" remain.
- All transcript-bearing windows (MainWindow, read views, live view) apply
  WDA_EXCLUDEFROMCAPTURE by default via the existing NativeWindowInterop, governed
  by a new Privacy settings toggle (`settings.privacy.excludeWindowsFromCapture`,
  additive, default true). The overlay keeps its own existing setting.
- Window geometry: window-state.json grows a keyed per-window schema (additive v2:
  `overlay`, `main`, `readViewDefault`) via WindowStateStore; failures stay
  silently-null as today.
- MVVM conventions carry over exactly: WPF-free ViewModels (no System.Windows),
  injected `Action<Action>` dispatch, TimeProvider everywhere, Humble-Object XAML,
  CommunityToolkit.Mvvm, ASCII-only source, 0-warning builds.

## 3. Sessions page

### 3.1 Enumeration

- On page load (async) enumerate `storageRoot/sessions/*` directories; for each,
  read session.json + meta.json through the existing stores. No sessions index
  file - files stay the truth. Virtualized list; target: hundreds of sessions
  load in under a second.
- Listing IS the migration event for old roots: SessionStore.ReadAsync
  write-migrates v1/v2 -> v3 and synthesizes meta.json. Accepted (additive,
  lossless). The enumerator passes `selfForMigration: null` - never fabricate
  today's identity into old sessions.
- Folders without a readable session.json are skipped and counted in a footer
  note ("N unreadable folders") - visible, not silent, not blocking.
- Refresh triggers are deterministic (no FileSystemWatcher): page navigation,
  after any edit/delete/recovery/tag change, and SessionController.StateChanged
  reaching Idle (a finalize just happened).

### 3.2 Presentation

- Columns: Title, App/Medium, Date, Duration, badges.
- Badges: Recovered, Edited, Diarised, System mix (session.json
  devices.remote.fellBackToSystemMix == true; tooltip explains possible
  other-app audio bleed), Archived (only visible when showing archived).
- Sort: newest first (StartedAtUtc desc). Filters: in-memory free-text over
  titles; by Matter including "No matter" (empty matterIds); "show archived"
  toggle (default off).
- Dates render in the SESSION'S stored UtcOffsetMinutes (matches folder ids and
  projections); viewer-local time in tooltip; pre-v3 records (null offset) fall
  back to machine-local as SessionWriter already does.
- Recovered sessions' Duration is transcript-derived (RecoverIfNeededAsync
  semantics) not wall-clock; the badge tooltip says so.
- Row actions: open read view, reveal in Explorer, delete (3.4), archive toggle.

### 3.3 Metadata editor (detail pane)

- Edits meta.json ONLY (spec 1.2/1.4 invariant): title, description, medium,
  matterIds (multi-select from the matters index + inline "new matter"),
  participants, localCount/remoteCount, archived.
- Participants: picked from the union of tagged matters' rosters or free text;
  side Local/Remote; isSelf shown; inline-adding a person writes through to the
  chosen Matter's roster (design L547-550).
- Auto-save on field commit - each committed change writes meta.json atomically
  and shows a subtle "Saved" indicator. No Save button; no unsaved-changes state
  at Exit by construction. Every save sets Edited/LastEditedAtUtc? NO - those
  flags mark transcript corrections (EditStore semantics), not metadata edits;
  metadata saves do not flip Edited. (Consistency: EditStore.MarkEditedAsync
  remains the only writer of those flags.)
- Every save queues a projection re-render through the maintenance service
  (section 7.3); matterIds changes additionally update matter sessionCounts
  incrementally.
- The editor is DISABLED for the session currently Recording/Paused/Finalizing
  (tooltip: "available when recording stops") - avoids clobbering the live
  controller's in-memory meta and matches the corrections gate. All finalized/
  recovered sessions are editable, including while another session records.
- Rename changes meta.Title only; folder ids are frozen at creation. Accepted,
  documented drift. The default-title slug duplication in folder ids
  ("..._Webex_webex-2026-07-02-18-45") is cosmetic and out of scope.

### 3.4 Whole-session delete

- One confirmation dialog: shows title, date, duration, matter tags; states that
  audio + transcript + metadata are all included. Confirm sends the session
  FOLDER to the Windows Recycle Bin (recoverable; via shell recycle API) -
  never a permanent unlink.
- Refused (disabled + tooltip) for the session currently Recording/Paused/
  Finalizing.
- After delete: list refresh + incremental sessionCount recompute for affected
  matters.
- This remains the ONLY deletion in the product; content-level delete/hide/
  redact does not exist anywhere (evidentiary invariant, spec 1.1/1.6).

## 4. Matters page

### 4.1 CRUD + organizer

- List from matters.json index: name, reference, sessionCount, archived state
  (archived collapse under the "show archived" toggle).
- Detail pane: name, reference, description, archived toggle, roster editor
  (add/rename/remove members; name + optional role), and the matter's tagged
  sessions (the two-level organizer's second level) with jump-to-session.
- Per-Matter vocabulary editing: DEFERRED to Stage 6 (schema field untouched).
- Matter deletion is BLOCKED while any session references the matter (dialog
  shows the count, suggests archiving). Dangling matterIds therefore only arise
  from external tampering; SessionWriter already renders raw ids for them.
  Un-tagging the last session leaves an empty, deletable matter. Matter delete
  also goes to the Recycle Bin and removes the index entry.

### 4.2 Identifier minting

- Matter id: `M-{yyyy}-{NNN}`, sequential within year, computed as
  max(existing NNN for the year in index)+1 with a matter-folder existence check
  before create (collision-safe; id doubles as the folder name; matches spec
  examples like M-2026-014).
- Roster member / session participant ids: `p-{ascii-name-slug}` with `-2`/`-3`
  numeric suffixes on collision; uniqueness scoped to the owning matter/session;
  `p-self` stays reserved (SessionBootstrap).

### 4.3 Index maintenance (the Stage 4 self-heal)

- Full rebuild = rescan matters/* folders (adopt orphans missing from the index,
  drop entries whose folder vanished) + recompute every sessionCount by scanning
  all session metas' matterIds.
- Runs: (a) at app launch in the background after the recovery scan completes,
  (b) on demand via a "Repair index" action on the Matters page.
- Between rebuilds, tag/untag/delete update counts incrementally.
- All index writes are serialized through the maintenance service (7.3); the
  single-instance guard (7.2) removes the cross-process race.

### 4.4 Rename cascades

- Session meta snapshots are never rewritten on roster/matter renames (spec 10).
- session.txt resolves matter names and roster names live at render time, so a
  Matter rename or roster-member rename triggers a background projection
  re-render of that matter's tagged sessions (bounded; atomic writes; progress
  in the status area; per-session failures reported via InfoBar, not swallowed).
  Truth files untouched.

## 5. Read view window

- Header: title, date (session offset), duration, matters, participants; badges
  as in the list. Footer: model/backend provenance. QA fields (noSpeechProb,
  confidence) never surface (spec 6.1).
- Body: DisplayRows from the canonical TranscriptProjection (same pipeline as
  live view and file renders); markers inline; timestamps per settings; grouped
  same-speaker rows; virtualized.
- Read-only in Stage 4 (corrections are Stage 6). Multiple read views may be
  open simultaneously (per-window VM instances; no shared mutable state).
- Audio playback: local + remote legs play together via two players started and
  seeked as a pair (hear the conversation, not one side). Transport: play/pause,
  seek bar, elapsed/total. Local/Remote mute toggles isolate a leg. Minor drift
  on very long sessions accepted for v1. Missing legs (retention "never", or
  one-source sessions) degrade to whichever file exists; no audio -> transport
  hidden. FLAC/WAV via Media Foundation (Windows 10+ decodes FLAC natively).

## 6. Settings page, consent notice

### 6.1 Settings UI groups (spec section 7 surface)

- Storage: root picker (stores the picked literal path; %VAR% forms preserved if
  already present), sync-provider warning (existing SyncProviderCheck), open
  folder, restart-required note on root change (no data migration; explicit
  warning that existing sessions stay in the old root), and a "Regenerate all
  projections" maintenance button (bulk re-render, background, progress).
- Recording: audioFormat (flac/wav), mic follow/pin, remote mode
  (auto/perProcess/systemMix). audioRetention displays the standing "Keep
  everything" policy (default keep; never auto-delete; days:N legacy-only,
  shown only if migrated in).
- Transcription: model, backend, language.
- Identity: self name/role - snapshotted into FUTURE sessions only (existing
  SessionBootstrap behavior).
- Privacy: excludeWindowsFromCapture toggle (new, default true; section 2),
  overlay preferences (existing fields), logging note (transcript text redacted
  by default - display only until Stage 7 logging lands).
- App: launchAtLogin (WIRED in Stage 4 via HKCU Run key - the field exists
  unconsumed today), timestamps style (relative/wallclock).
- NOT exposed: recordingIndicator (tray consent indicator is immovable - field
  stays in schema, unread by code), Hotkeys (dropped, 1.1), autoDetect (disabled
  seam), vocabulary (Stage 6).

### 6.2 Settings plumbing (first mutation path)

- New ISettingsService (App layer): holds the current Settings record, SaveAsync
  persists via SettingsStore (atomic), raises Changed events with old/new.
- Propagation policy: capture/model/identity settings take effect at next Start
  (SessionController resolves its per-session inputs at StartAsync via the
  service instead of a constructor-captured snapshot - exact seam decided in the
  implementation plan). UI settings (timestamps, capture exclusion) apply
  immediately. storageRoot: restart-required (StoragePaths is constructed once).
- Timestamps/vocabulary-style changes do NOT mass-rewrite projections; files
  refresh lazily on next touch, or in bulk via the maintenance button.

### 6.3 First-run consent notice

- Shown at launch when no acknowledgment is persisted: local-recording summary +
  prominent statement that recording others is the user's legal responsibility
  (jurisdiction-dependent two-party/all-party consent), reusing README draft
  language. Accept persists `consentNotice: { acknowledgedAtUtc, appVersion }`
  as an additive settings.json field (detection is field-absence, not
  file-absence - settings.json starts existing once the settings UI saves).
  Decline exits the app. Never shown again after acceptance; Record is never
  gated post-acceptance (manual-only start remains the consent posture).

## 7. Startup + infrastructure

### 7.1 Recovery scan

- After tray-up, background task enumerates sessions with endedAtUtc == null and
  runs SessionWriter.RecoverIfNeededAsync per session (idempotent; appends
  recovered marker, finalizes, re-renders).
- Surfacing: existing notice pipeline -> tray balloon "Recovered N interrupted
  session(s)"; Sessions page shows "checking for interrupted sessions..." until
  the scan completes; recovered rows carry the badge. Scan failures per-session
  are collected and reported, not swallowed.
- Never blocks Start (new sessions get fresh folders); never blocks the UI.

### 7.2 Single-instance guard

- Named mutex (per-user). Second instance signals the first (activate
  MainWindow) and exits. Rationale: Stage 4 makes matters.json read-modify-write
  load-bearing, and two instances could double-record.

### 7.3 Maintenance service + concurrency

- One app-level maintenance service owns: projection re-renders (per-session
  single-flight queue - an edit, a finalize, and a cascade cannot interleave
  writes on one session), index rebuilds/recomputes (serialized), rename
  cascades, bulk regenerate, recovery scan. All disk mutation from the UI goes
  through it; ViewModels never call SessionWriter directly.
- Editor-vs-finalize race is designed out: live sessions are not editable (3.3),
  meta.json is user-owned (writer split, spec 1.2), and regen is single-flight.

### 7.4 AppKind derivation (3b deferral)

- Manual Starts derive AppKind (Webex/Zoom/Teams/Browser) from the resolved
  remote-capture planner result at StartAsync; falls back to Manual when
  unresolved. Affects NEW sessions only (session.json is system-owned; existing
  records stay as recorded; medium remains user-correctable in the editor).
  Folder ids embed whatever App was resolved at creation - unchanged behavior.

### 7.5 Error surfacing

- Every manager/editor command: per-command try/catch -> inline InfoBar in
  MainWindow (message + retry where sensible). Background operations (scan,
  rebuild, cascades) -> tray balloon + status area. Nothing relies on the
  globally-swallowed DispatcherUnhandledException. Real logging remains Stage 7;
  the InfoBar/notice seam is designed so Stage 7 can attach a logger.

## 8. Schema changes (all additive, per the schema-version policy)

| File | Change |
|---|---|
| meta.json (v1 -> v2) | + `archived: bool` (default false/absent) |
| matter.json (v1 -> v2) | + `archived: bool` (default false/absent) |
| matters.json (v1 -> v2) | + `archived: bool` per entry (mirrors matter.json for list rendering) |
| settings.json (v2 -> v3) | + `consentNotice { acknowledgedAtUtc, appVersion }`, + `privacy.excludeWindowsFromCapture: bool` (default true) |
| window-state.json | keyed per-window entries (`overlay`, `main`, `readViewDefault`); migrate the existing single X/Y pair to `overlay` |

Readers keep reject-higher/migrate-lower semantics; all changes additive.

## 9. Testing

- Unit ([UNIT], no STA, temp roots, injected TimeProvider/dispatch - existing
  conventions): session enumeration incl. migration tolerance + unreadable
  folders + selfForMigration:null; editor auto-save, live-session lock, no
  Edited-flag flips on metadata saves; matter CRUD, id minting (year rollover,
  collisions), delete-blocked-while-referenced; index rebuild (orphan adoption,
  count recompute, vanished folders); rename cascades (projection refresh, truth
  untouched); recovery-scan orchestration (only endedAtUtc==null touched,
  idempotency, failure isolation); delete flow (recycle call seam mocked, live
  session refused, counts recomputed); settings service (save, propagation
  timing, consent persistence); maintenance single-flight semantics.
- Fixture/manual: Stage 4 smoke runbook (B-series style) - real GUI flows over
  the 5 existing Webex sessions on disk: first-run consent, list + migration of
  any pre-v3 fixtures, edit/tag round-trip with projection refresh, matter
  create/archive, read view + dual-leg audio, delete-to-recycle-bin, capture
  exclusion verified inside a Webex screen share, single-instance activation.
- Gates unchanged: dotnet test --filter Category!=Fixture green, 0-warning
  build, conventional commits.

## 10. Deferred / follow-ups created by this design

- Stage 5: diarisation (Split-speakers dialog seeds from localCount/remoteCount).
- Stage 6: correction editing in the read view, vocabulary UI (global +
  per-Matter), .zip/.docx export + shared Session/Matter picker.
- Stage 7: logging behind the InfoBar seam, storage-root migration tooling,
  device hot-swap/watchdog, installer, model-download UX.
- Unscheduled: hotkeys with conflict-free combos + rebind UI (only if wanted);
  transcript-synced audio playback; folder-id slug cleanup.
