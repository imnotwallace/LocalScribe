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
