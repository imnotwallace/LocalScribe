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
- Window geometry: window-state.json grows keyed per-window entries (`overlay`,
  `main`, `readViewDefault`) replacing today's unversioned bare {x,y} object;
  migration is shape-detection on read (a bare pair becomes the `overlay`
  entry). No schemaVersion - this file is throwaway UI state and failures stay
  silently-null as today. `readViewDefault` is written by the last read view
  closed; each additional simultaneously-open read view opens offset (+24px
  cascade) from it, screen-clamped via the existing ScreenClamp.
- MVVM conventions carry over exactly: WPF-free ViewModels (no System.Windows),
  injected `Action<Action>` dispatch, TimeProvider everywhere, Humble-Object XAML,
  CommunityToolkit.Mvvm, ASCII-only source, 0-warning builds.

## 3. Sessions page

### 3.1 Enumeration

- On page load (async) enumerate `storageRoot/sessions/*` directories; for each,
  read session.json + meta.json through the existing stores. No sessions index
  file - files stay the truth. Virtualized list; target: hundreds of sessions
  load in under a second.
- First read is the migration event for old roots (the startup recovery scan or
  the first listing, whichever comes first): SessionStore.ReadAsync
  write-migrates v1/v2 -> v3 and synthesizes meta.json. Accepted (additive,
  lossless). All read passes use `selfForMigration: null` - never fabricate
  today's identity into old sessions. (Refines the specs' v2->v3 migration rule,
  which said participants = [self from settings]; specs.md edit queued in
  section 10.)
- Because a read can write, every per-session folder access from the UI and the
  recovery scan routes through the maintenance service's per-session
  single-flight queue (7.3) - enumeration cannot interleave with recovery, a
  finalize, or an edit on the same session.
- Sessions still awaiting recovery (endedAtUtc == null, scan not yet reached
  them) render as a "Recovering..." row state - duration blank, editor and
  delete disabled - and flip to normal rows with the Recovered badge as the
  scan completes them.
- Folders without a readable session.json are skipped and counted in a footer
  note ("N unreadable folders") - visible, not silent, not blocking.
- Refresh triggers are deterministic (no FileSystemWatcher): page navigation,
  after any edit/delete/recovery/tag change, and SessionController.StateChanged
  reaching Idle (a finalize just happened).

### 3.2 Presentation

- Columns: Title, App/Medium, Date, Duration, badges.
- Badges: Recovered, Edited, Diarised (session.json `diarised`; stays false
  until Stage 5 writes it), System mix, Archived (only visible when showing
  archived).
- System mix badge condition: devices.remote.mode == systemMix OR
  devices.remote.fellBackToSystemMix == true - an explicitly chosen system-mix
  has identical other-app audio-bleed characteristics to a fallback, and
  Teams/browser sessions are exactly the ones likely to pin systemMix. Tooltip
  distinguishes chosen vs degraded-fallback. Mid-session degradation (a Resume
  falling back after Start) exists only as a transcript marker today and the
  list never reads transcripts, so that case surfaces in the read view instead
  (section 5).
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
  at Exit by construction. Metadata saves do NOT flip Edited/LastEditedAtUtc -
  those flags mark transcript corrections and pinned speaker reassignments
  (EditStore.MarkEditedAsync stays their only writer). This deliberately
  refines specs section 1.4, which had the flags marking any user edit
  including metadata; specs.md edit queued (section 10).
- Participant linkage model: a participant picked from a roster COPIES the
  member's id and name into the session snapshot; the shared id is provenance
  only, never a live link - every render (list, read view, session.txt) uses
  the snapshot name. Free-text participants mint a session-scoped id (4.2).
- Every save queues a projection re-render through the maintenance service
  (section 7.3); matterIds changes additionally update matter sessionCounts
  incrementally.
- The editor is DISABLED for the session currently Recording/Paused/Finalizing
  (tooltip: "available when recording stops") and for sessions still awaiting
  recovery (3.1). Rationale: keeps an editor save from racing the finalize- or
  recovery-time projection regeneration in the same session folder, and matches
  the EditStore finalized-only corrections gate. (The controller itself never
  rewrites meta.json - finalize re-reads it from disk - so this is a
  consistency/UX gate, not a data-loss guard.) All finalized/recovered sessions
  are editable, including while another session records.
- Rename changes meta.Title only; folder ids are frozen at creation. Accepted,
  documented drift. The default-title slug duplication in folder ids
  ("..._Webex_webex-2026-07-02-18-45") is cosmetic and out of scope.

### 3.4 Whole-session delete

- One confirmation dialog: shows title, date, duration, matter tags; states that
  audio + transcript + metadata are all included. Confirm sends the session
  FOLDER to the Windows Recycle Bin (recoverable; via shell recycle API) -
  never a permanent unlink.
- Refused (disabled + tooltip) for the session currently Recording/Paused/
  Finalizing and for sessions awaiting recovery (3.1).
- Any open read views of the session are closed first - releases the audio file
  handles so the recycle operation cannot fail on a sharing violation.
- After delete: list refresh + incremental sessionCount recompute for affected
  matters.
- This remains the only deletion of session/transcript data in the product;
  content-level delete/hide/redact does not exist anywhere (evidentiary
  invariant, spec 1.1/1.6). Matter deletion - organizational data only, blocked
  while any session references it - is defined separately in 4.1.

## 4. Matters page

### 4.1 CRUD + organizer

- List from matters.json index: name, reference, sessionCount, archived state
  (archived collapse under the "show archived" toggle).
- Detail pane: name, reference, description, archived toggle, roster editor
  (add/rename/remove members; name + optional role), and the matter's tagged
  sessions (the two-level organizer's second level) with jump-to-session.
- Roster edits never touch sessions: session participants are self-contained
  snapshot copies (3.3), so renaming or removing a roster member only changes
  who is offered in future picks. Existing sessions keep the name as recorded;
  removal cannot dangle anything because rendering never dereferences the
  roster.
- Archive semantics: archiving a matter does NOT cascade to its sessions.
  Archived matters leave the default matter list and the Sessions-page Matter
  filter; existing tags on sessions keep rendering normally, and the metadata
  editor's matter multi-select offers archived matters only when its own "show
  archived" is on. The show-archived toggles are independent, non-persisted,
  per-page UI state.
- Per-Matter vocabulary editing: DEFERRED to Stage 6 (schema field untouched).
- Matter deletion is BLOCKED while any session references the matter (dialog
  shows the count, suggests archiving). Dangling matterIds therefore only arise
  from external tampering; SessionWriter already renders raw ids for them.
  Un-tagging the last session leaves an empty, deletable matter. Matter delete
  also goes to the Recycle Bin and removes the index entry. Deleting an empty
  matter is organizational-data deletion only: blocked-while-referenced
  guarantees no session content references it, so the evidentiary invariant
  (spec 1.1, coarse whole-session delete as the only session-data deletion) is
  untouched.

### 4.2 Identifier minting

- Matter id: `M-{yyyy}-{NNN}`, sequential within year, computed as
  max(existing NNN for the year in index)+1; if the index entry or matter
  folder already exists, increment NNN until both are free (id doubles as the
  folder name; matches spec examples like M-2026-014).
- Roster member / session participant ids: `p-{ascii-name-slug}` with `-2`/`-3`
  numeric suffixes on collision; uniqueness scoped to the owning matter/session;
  `p-self` stays reserved (SessionBootstrap). Picking a roster member into a
  session copies id + name into the snapshot (provenance, not a live link);
  free-text participants mint their id within the session's scope.

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
- Participant names are snapshot-stable everywhere: projections render from the
  meta.json snapshot (SessionWriter.cs:61-62) and the transcript display-name
  chain (speakers.json -> declared participant -> Me/Them) never reads the
  roster. A roster-member rename therefore does NOT propagate to any existing
  session - it only changes future picks. This keeps old privileged records
  stable (the evidentiary rationale of spec 10) and resolves specs 6.2's
  "resolved live from the current rosters" wording in favor of snapshots;
  specs.md amendment queued (section 10). Roster edits trigger NO cascade.
- Matter names/references DO resolve live: session.txt renders "Name
  (Reference)" via MatterStore at render time (SessionWriter.cs:38-44). A
  Matter rename or reference change therefore triggers a background projection
  re-render of that matter's tagged sessions (bounded; atomic writes; progress
  in the status area; per-session failures reported via InfoBar, not
  swallowed). Truth files untouched.

## 5. Read view window

- Header: title, date (session offset), duration, matters, participants; badges
  as in the list. Footer: model/backend provenance. QA fields (noSpeechProb,
  confidence) never surface (spec 6.1).
- Body: DisplayRows from the canonical TranscriptProjection - the same pipeline
  as the FILE renders (transcript.md/.txt, session.txt); markers inline;
  timestamps per settings; grouped same-speaker rows; virtualized. Known,
  deliberate divergence: the 3b live view renders raw merger lines with NO
  projection pass (no vocabulary, dedup, name resolution, or grouping), so a
  read view can legitimately differ from what was seen live - implementers and
  tests must not assume live-view parity.
- The read view detects the degraded-system-audio transcript marker and shows
  the System mix notice for mid-session fallbacks the list badge cannot see
  (3.2).
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

- Storage: root picker (picking a new folder always stores the literal path; a
  %VAR% form survives only while the field is left untouched), sync-provider
  warning (existing SyncProviderCheck), open folder, restart-required note on
  root change (no data migration; explicit warning that existing sessions stay
  in the old root), and a "Regenerate all projections" maintenance button
  (bulk re-render, background, progress).
- Recording: audioFormat (flac/wav), mic follow/pin, remote mode
  (auto/perProcess/systemMix). audioRetention is a READ-ONLY display of the
  effective policy in Stage 4 ("Keep everything" by default; a migrated
  never/days:N/afterDiarisation value renders as its own text). The
  auto-delete opt-ins are deliberately not exposed - refines spec section 7
  per the never-propose-audio-auto-deletion decision; specs.md edit queued
  (section 10).
- Transcription: model, backend, language. The model picker enumerates only
  locally installed models (ModelsRoot scan), so an absent model cannot be
  selected; model-download UX remains Stage 7.
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
- Timestamps-style changes do NOT mass-rewrite projections; files refresh
  lazily on next touch, or in bulk via the maintenance button. (Same policy
  will apply to vocabulary changes when Stage 6 exposes them.)

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
- The scan reads through the same maintenance-service per-session queue as
  everything else and inherits 3.1's rules (selfForMigration: null; unreadable
  folders skipped and counted) - note the scan's own reads may be the first
  migration event. Rows awaiting recovery render "Recovering..." with editor
  and delete disabled (3.1).
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
  through it; ViewModels never call SessionWriter directly. Store reads that
  can write-migrate (3.1) route through the same per-session queue, so the
  serialization guarantee covers migration writes too.
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
| window-state.json | keyed per-window entries (`overlay`, `main`, `readViewDefault`) replacing the unversioned bare {x,y}; shape-detected migration (bare pair -> `overlay`); no schemaVersion |

Readers keep reject-higher/migrate-lower semantics; all changes additive.
(window-state.json is exempt: unversioned throwaway UI state with
silently-null failure handling, per spec section 7.)

## 9. Testing

- Unit ([UNIT], no STA, temp roots, injected TimeProvider/dispatch - existing
  conventions): session enumeration incl. migration tolerance + unreadable
  folders + selfForMigration:null; editor auto-save, live-session and
  pending-recovery locks, no Edited-flag flips on metadata saves; matter CRUD,
  id minting (year rollover, collisions), delete-blocked-while-referenced;
  index rebuild (orphan adoption, count recompute, vanished folders); rename
  cascades (a matter-name change VISIBLY updates re-rendered session.txt;
  roster renames trigger no cascade and leave existing renders unchanged;
  truth untouched); recovery-scan orchestration (only endedAtUtc==null
  touched, idempotency, failure isolation); delete flow (recycle call seam
  mocked, live and pending-recovery sessions refused, open read views closed
  first, counts recomputed); settings service (save, propagation timing,
  consent persistence); maintenance single-flight semantics incl. migrating
  reads.
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
- Owed by the implementation plan: the exact seam through which
  SessionController resolves settings at StartAsync instead of a
  constructor-captured snapshot (6.2) - the one deliberately unresolved point
  in this design.
- Queued specs.md amendments (documentation debt from approved refinements):
  section 1.4 - Edited/LastEditedAtUtc marks transcript corrections + pinned
  reassignments only, not metadata edits (3.3); section 6.2 - participant
  names render from the session snapshot, not live rosters (4.4); section 7 -
  audioRetention read-only in the UI, auto-delete opt-ins not exposed (6.1);
  schema-version policy - v2->v3 migration synthesizes participants as empty
  (selfForMigration: null, 3.1); sections 1.4/1.5 - archived flags on matters
  and sessions (schema table, section 8).
