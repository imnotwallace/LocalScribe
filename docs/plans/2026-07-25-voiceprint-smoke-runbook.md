# Voiceprint Suggestion Chip Smoke Runbook

## Context

Cross-session voice-fingerprint speaker recognition (design 2026-07-25) adds a SUGGEST-ONLY
advisory to the Split-speakers dialog: a diarised cluster whose voice matches a saved voiceprint
shows a "Sounds like <name> (NN%)" chip that the user explicitly Accepts or Dismisses. Nothing
auto-assigns a name. An opt-in "Search all people" widens the suggestion pool from the session's
matters' rosters to every saved person. A ticked "Remember voice" (only meaningful once a row
carries a real name) saves that cluster's voiceprint locally so future sessions can suggest the
same person. Settings gains person-level voiceprint management and a global purge. This runbook
covers the chip lifecycle, confirm-time persistence, the global search widen, deletion/purge, the
backfill scan, and the matcher's threshold sanity on real audio.

None of this is voice identification evidence: the score is a similarity hint, not a probability
of identity, and every path is reviewed for suggest-only behavior before this runbook is run.

## Smoke Test Steps

### V1: Diarising a session with an enrolled roster member surfaces a chip; Accept fills the name

**Steps:**
1. Ensure a matter has a roster member linked to a Person (People/Settings) with at least one
   enrolled voiceprint.
2. Record or use an existing finalized session on that matter where the same voice speaks on an
   unnamed cluster's leg.
3. Open Session Details (or the read view's "Split speakers..." button) and run Split speakers.
4. Select the source(s) and click Run.
5. Locate the cluster whose voice matches the enrolled person.
6. Verify a "Sounds like <name> (NN%)" chip appears directly under that row's naming ComboBox, with
   Accept and Dismiss buttons.
7. Verify the naming ComboBox text has NOT changed on its own (suggest-only: nothing auto-fills).
8. Click Accept.
9. Verify the ComboBox now shows the suggested name, the chip disappears, and a small "Linked from
   a voiceprint match (NN%)" indicator appears in its place.

**Expected result:** A chip appears only for a genuinely matched cluster, never auto-fills the
name, and Accept is the only thing that ever writes the name from a suggestion.

---

### V2: Confirm persists suggestionProvenance, embeddings.json, and grows people.json

**Steps:**
1. Continuing from V1 with the row Accepted, click Confirm / Save.
2. Verify the dialog reports success (or simply closes/updates without an error banner).
3. Inspect `speakers.json` for the session's active version: verify `suggestionProvenance` now has
   an entry for the accepted cluster's key, with the matched PersonId and score.
4. Verify `embeddings.json` exists beside it for this session/version.
5. Open Settings' People/voiceprint view (or inspect `people.json`): verify the matched person's
   enrollment count increased by exactly the number of clusters confirmed for them.
6. Verify the transcript text and existing speaker names for OTHER clusters are unchanged
   (evidentiary firewall: nothing but the confirmed cluster's own name/provenance moved).

**Expected result:** A successful Confirm durably records provenance and grows the person's
voiceprint enrollment by exactly the confirmed clusters - nothing else in the session is touched.

---

### V3: "Search all people" surfaces a chip for a person not on the matter's rosters

**Steps:**
1. Enroll a voiceprint for a Person who is NOT linked to any roster member of the session's
   matter(s) (a "global" person).
2. Diarise a session on that matter where an unnamed cluster is that global person's voice.
3. Run Split speakers; verify the cluster shows NO chip after the default Run (the matter-scoped
   pool does not include this person).
4. Click "Search all people".
5. Verify the same cluster now shows the "Sounds like <name> (NN%)" chip for the global person.
6. Verify a row you already Accepted or manually typed a name into (if any) is left untouched by
   the search - only rows still carrying their default label and no accepted link may pick up a
   new chip.

**Expected result:** The default pass never reaches outside the matter's rosters; "Search all
people" is the only path that does, and it never overwrites a row the user already decided.

---

### V4: Deleting a person's voiceprint removes future suggestions for them

**Steps:**
1. From a person with an enrolled voiceprint used in a prior chip match, open Settings and delete
   that person's voiceprint (not the person, just the voiceprint/enrollment).
2. Re-run Split speakers (Run, and if relevant, "Search all people") on a session containing that
   same voice.
3. Verify no chip appears for that person anywhere in the run.
4. Verify the person's name/roster link (if any) and any PREVIOUSLY confirmed
   `suggestionProvenance` entries from before the deletion are left untouched (deletion only stops
   future matching, it does not retroactively edit past commits).

**Expected result:** A deleted voiceprint stops producing new suggestions immediately; historical
provenance already committed to a session is not rewritten or removed.

---

### V5: "Purge all voiceprint data" clears every embeddings.json and provenance map; names/transcripts/audio untouched

**Steps:**
1. In Settings, locate "Purge all voiceprint data" (or equivalent) and confirm the action.
2. Verify every session's `embeddings.json` is gone from disk.
3. Verify every session's `speakers.json` `suggestionProvenance` map is now empty (the key itself
   may remain as an empty object, but no entries survive).
4. Verify every person's voiceprint enrollment list in `people.json` is now empty.
5. Verify session/speaker NAMES, transcript text, and audio files are completely untouched -
   re-open a purged session's read view and confirm the transcript and speaker labels look exactly
   as before the purge.

**Expected result:** Purge is voiceprint-data-only. It never touches names, transcripts, or audio
(evidentiary firewall holds even for this destructive, user-confirmed action).

---

### V6: "Scan sessions and enroll known speakers" backfills a pre-feature session

**Steps:**
1. Identify (or restore) a session that was diarised BEFORE this feature shipped (no
   `embeddings.json`), with a cluster durably owned by a participant slot that is itself linked to
   a Person.
2. In Settings, run "Scan sessions and enroll known speakers" (the backfill scan).
3. Wait for the scan to complete.
4. Verify the linked person's voiceprint enrollment list now includes an entry whose
   `SourceSessionId` is that pre-feature session's id.
5. Verify sessions with no eligible owned+linked cluster are skipped without error, and the scan
   reports a sane summary (sessions scanned / enrolled / skipped).

**Expected result:** The backfill scan enrolls only owned, person-linked clusters from sessions
diarised before the feature existed, and never touches an unrelated or unlinked session.

---

### V7: Threshold sanity on real audio - same speaker matches, different speakers don't

**Steps:**
1. Using real (not synthetic) audio, enroll a voiceprint for a specific speaker from one session.
2. Diarise a SECOND, different session containing the same speaker's real voice; verify the
   suggestion score for that speaker's cluster is >= ~0.55 (a chip should appear).
3. Diarise a THIRD session containing a genuinely DIFFERENT speaker's voice against the same
   enrolled person; verify the suggestion score stays below ~0.55 (no chip, or a chip for the
   correct different person only).
4. If real-audio scores do not separate cleanly around this threshold, tune the
   `VoiceprintMatcher` threshold constant(s) before merging this feature - do not ship with a
   threshold that was only ever validated on synthetic vectors.

**Expected result:** Same-speaker-across-sessions scores clear the suggestion threshold; genuinely
different speakers do not. This is the one check in this runbook that requires real audio and
cannot be satisfied by unit tests alone.

---

### V8: Settings > Voiceprints reads honestly - list, empty state, and the re-enroll hint

**Steps:**
1. With no `people.json` on disk (or an empty registry), open Settings and scroll to the
   "Voiceprints" card.
2. Verify the explainer says voiceprints are a suggestion you accept or dismiss, stored only on
   this computer, deletable at any time - and does NOT claim identification.
3. Verify the empty state reads "Nothing is stored: no person on this computer has a saved
   voiceprint." and no rows are listed.
4. Enroll at least one person (V1/V2 or V6), re-open Settings, and verify the row shows the
   person's name plus "N voiceprint(s) - latest YYYY-MM-DD from session <id>".
5. Verify a person with NO enrollments shows their name with no summary line, and their "Delete
   voiceprint" / "Delete oldest" buttons are disabled.
6. Hand-edit one person's `people.json` enrollments so every `method` is something other than
   `campplus-zh-en`, re-open Settings, and verify that row shows the amber "Saved with an older
   voice model - it cannot be matched. Delete it and enroll again." hint. Verify a person with at
   least one current-method enrollment does NOT show it.

**Expected result:** The card answers "what is stored about whom" without overclaiming, and a
person whose enrollments can never be matched says so instead of looking usable.

---

### V9: Per-person deletes take effect immediately and never touch anything else

**Steps:**
1. With a person holding 2+ enrollments, note their count, then click "Delete oldest".
2. Verify the row's count drops by exactly one with no page reload, and `people.json` lost the
   OLDEST enrollment (compare `enrolledAtUtc` values) - not the newest.
3. Click "Delete voiceprint" on that person. Verify no confirmation appears, the count drops to
   zero, the person's NAME stays on screen, and `people.json` still contains the person with an
   empty `voiceprint` array.
4. Click "Delete person". Verify a Yes/No confirmation appears naming that person, defaulting to
   No, and that clicking No leaves the row exactly as it was.
5. Repeat and click Yes. Verify the row disappears and the person is gone from `people.json`.
6. Open a session that had that person's name assigned to a speaker: verify the speaker name,
   transcript text, and audio are unchanged (evidentiary firewall).

**Expected result:** Each deletion level does exactly what its label says, the on-screen counts
always match `people.json` immediately afterwards, and only the person-delete asks first.

---

### V10: A partially-failed purge is reported as a partial failure, not as success

**Steps:**
1. Enroll at least one person so `people.json` holds a real voiceprint. Close LocalScribe.
2. Make the registry unreadable: edit `people\people.json` and set `"schemaVersion": 99` (a
   forward version the app must refuse to load). Keep a backup copy.
3. Start LocalScribe, open Settings > Voiceprints, click "Purge all voiceprint data".
4. Verify the confirmation states plainly: voiceprints go; people keep their names; transcripts,
   speaker names, and audio are untouched; cannot be undone. Confirm it.
5. **Verify the status line under the buttons is styled as a red/critical warning (not a muted
   note) and says the saved voiceprints could NOT be deleted and are still stored on this
   computer, naming `people.json`.** It must NOT read as a completed purge.
6. Verify on disk that the enrollments really did survive (the message was truthful).
7. Restore the backup `people.json`, purge again, and verify the status line is now an ordinary
   note reading "Deleted all saved voiceprints. Voice data was cleared from N session(s). Names,
   transcripts, and audio were not changed." and that the enrollments are actually gone.

**Expected result:** The one failure mode where the most identifying data survives a deletion the
user asked for is impossible to mistake for success - it is worded as "NOT deleted", styled as a
warning, and is verifiably true on disk.

---

### V11: Backfill status line reports real counts and needs the Diarizer helper

**Steps:**
1. With `LocalScribe.Diarizer.exe` NOT deployed beside the app, click "Scan sessions and enroll
   known speakers". Verify an error surfaces through the normal error banner and the status line
   reads that the scan stopped early - not a fake success.
2. Deploy the Diarizer helper (see the Stage 5 runbook's prerequisite section) and the CAM++
   embedding model, then click the button again.
3. Verify the status line briefly reads "Scanning sessions..." and then
   "Scanned N session(s) - enrolled K, skipped S."
4. Verify the People list above it refreshed with the new counts without re-opening Settings.
5. Verify no new people were created by the scan (only already-linked speakers enroll).

**Expected result:** The backfill reports what it actually did, refreshes the list it just
changed, and fails visibly rather than silently when the helper or model is missing.

---
