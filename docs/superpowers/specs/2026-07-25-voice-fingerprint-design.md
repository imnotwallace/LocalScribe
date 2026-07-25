# Voice-Fingerprint Speaker Recognition — Design

**Date:** 2026-07-25
**Status:** Approved by user (brainstorm 2026-07-25); pending implementation plan
**Origin:** OpenWhispr competitive analysis (deep-research, 2026-07-25) — adopt item #1

## Purpose

When a session is diarised, LocalScribe should recognise voices it has heard before and *suggest* their identities, so the user is not re-naming the same handful of people on every session of a matter. Suggestions are advisory only: the app never auto-assigns a name from a voiceprint match (consistent with the locked never-silent-rebind / evidentiary principles).

## Decisions (user-confirmed)

| Decision | Choice |
|---|---|
| Match scope | Matter-scoped by default; explicit per-dialog "Search all people" opt-in for global matching (frequent collaborators) |
| Identity anchor | New global **People registry**; matter `RosterMember` gains an optional `PersonId` link |
| Enrollment trigger | Automatic on identity confirm — confirming a cluster's identity is the consent gate |
| Extraction architecture | Approach A: diarize op also emits per-cluster embeddings; small `embed` op for backfilling legacy sessions |
| Deletion | **First-class requirement**: per-enrollment, per-person, and global purge — voiceprints must be retrospectively deletable at any time |
| Auto-assign | Never. Suggest-only chips with accept/dismiss |

Voiceprints are *derived biometric data*, not evidence. They are deletable without violating the no-transcript-deletion rule; transcripts, `speakers.json` names, and audio are never touched by voiceprint deletion.

## Data model & storage

### People registry — `<root>\people\people.json` (new)

```
PeopleRegistry { SchemaVersion = 1, People: Person[] }
Person {
  Id, Name, Role?, Org?, CreatedUtc,
  Voiceprint: VoiceprintEnrollment[]        // max 20; oldest evicted on overflow
}
VoiceprintEnrollment {
  Id, Embedding: float[],                   // ~192-d CAM++ vector (copied, not referenced)
  Method,                                   // e.g. "campplus-zh-en"; only same-Method embeddings compare
  SourceSessionId, SourceClusterKey, EnrolledAtUtc
}
```

- Written via existing `AtomicFile` + `SchemaGuard` machinery; user data (not rebuildable).
- Enrollments **copy** their vector so person voiceprints survive per-session purges and re-diarises independently.

### Roster link

`RosterMember` gains nullable `PersonId`. Candidate pools:

- **Matter pool** = persons linked (via `PersonId`) from the rosters of all matters in the session's `meta.MatterIds`.
- **Global pool** = every person with ≥1 enrollment. Only reached via the explicit "Search all people" action.

### Per-session cluster embeddings — `sessions\<id>\embeddings.json` (new, derived)

```
ClusterEmbeddings { SchemaVersion = 1, Method, ExtractedAtUtc, Entries: { clusterKey -> float[] } }
```

- Written at diarise time, **after** `SpeakersMerge.FreshKeyRemap` is applied — keys are always post-remap cluster keys (same ordering discipline as `MaintenanceService.RunReDiariseAsync`).
- Derived data: rebuildable (re-diarise or `embed` op), safe to delete, ignored when corrupt.

### Suggestion provenance (evidentiary transparency)

`Speakers` (speakers.json) gains optional `SuggestionProvenance: { clusterKey -> { PersonId, Score, AcceptedAtUtc } }`, recorded only when a suggestion is **accepted**, so an accepted match is never indistinguishable from a hand-typed name. Cleared by the global purge.

### Deletion — three levels

1. **Per-enrollment** delete and **per-person "Delete voiceprint"** on the People management UI.
2. Deleting a Person removes their voiceprint entirely; dangling `RosterMember.PersonId` links null-guard gracefully.
3. **"Purge all voiceprint data"** (Settings, confirmation-gated): clears every person's enrollments, deletes every session's `embeddings.json`, and clears all `SuggestionProvenance` entries. Real deletion via atomic rewrite; no tombstones or markers. Speaker *names* assigned in speakers.json are untouched.

## Extraction & matching pipeline

### Diarizer wire changes (`DiarisationWire`; both sides ship together)

The Diarizer wire is not a locked contract (unlike `AssistantWire`); changes are additive and back-compat:

1. `DiarisationJob` gains `EmitEmbeddings: bool` (default `false`). When set, the result payload gains
   `clusterEmbeddings: { "<clusterId>": float[] }` and `embeddingMethod: string`. Near-zero cost — CAM++
   already computes these vectors during clustering; the helper currently discards them.
2. New job variant `Op: "embed"`: `{ FlacPath, Ranges: [{startMs,endMs}], EmbeddingModelPath }` →
   `{ embedding: float[], method }`, implemented with sherpa-onnx `SpeakerEmbeddingExtractor`.
   Used only for backfill-enrollment from sessions diarised before this feature (ranges derived from the
   existing `speakers.json` assignments for the chosen cluster).
- Old-helper / missing-field degradation: if the result payload lacks `clusterEmbeddings`, diarisation
  completes exactly as today; suggestions are silently absent (one log line).

### Matching — `VoiceprintMatcher` (Core, pure)

- `Suggest(clusterEmbeddings, candidates)` → at most one suggestion per cluster.
- Cosine similarity; a person's score = max over their enrollments.
- Suggest only if top score ≥ **0.55** AND margin over runner-up person ≥ **0.05** (named constants;
  tunable after real-audio smoke).
- Only same-`Method` embeddings compare. Stale-method enrollments are skipped; People UI shows a
  "re-enroll needed" hint for persons whose enrollments are all stale.

### Enrollment flow

- Confirming a cluster's identity (naming it to a person-linked roster member, or accepting a suggestion)
  reads that cluster's vector from `embeddings.json` and appends a `VoiceprintEnrollment`.
- Legacy session (no `embeddings.json`): explicit "Enroll voice from this session" action runs the `embed` op.
- Re-diarise: `embeddings.json` is rewritten with fresh post-remap keys. Existing person enrollments are
  unaffected (they own copied vectors).

## UX

- **Split Speakers dialog** (existing post-diarise flow, `SplitSpeakersViewModel`): after diarisation,
  matching runs against the matter pool; each cluster row can show one suggestion chip —
  "Sounds like Sarah Chen · 87% — [Accept] [Dismiss]".
  - Accept: fills the cluster name, links the participant slot to the person, records provenance.
  - Dismiss: hides the chip; nothing stored.
  - Below threshold: no chip. The app never auto-assigns.
- **"Search all people"** button on the same dialog re-runs matching against the global pool for
  still-unnamed clusters — deliberate opt-in, never default.
- **People management**: new Settings section (occasional-use; not a nav page). Lists persons with
  their enrollments (source session + date); per-enrollment delete, per-person delete-voiceprint,
  person delete, and the global purge (confirmation prompt).
- **Person creation** is inline: when naming a speaker, a small "link to person: existing / new" picker.
  No pre-populated registry required.

## Error handling

- Embedding model missing, old helper, or `EmitEmbeddings` unsupported → diarisation unaffected;
  no suggestions; one log line. The feature always fails toward "no suggestions", never toward
  blocking diarisation.
- `embed` op failures reuse the existing error surface (`MODEL_MISSING` / `BAD_AUDIO` / `HELPER_CRASH`).
- Corrupt `embeddings.json` → ignored; rebuildable. Corrupt `people.json` → SchemaGuard refuses load
  and reports (user data; atomic writes protect it).
- Deleted person referenced by `RosterMember.PersonId` or `SuggestionProvenance` → null-guarded display.

## Testing

Pure-first, per house style:

- `VoiceprintMatcher`: threshold, margin, method-gating, empty-pool, single-candidate, tie cases.
- People store: enroll/append, 20-cap eviction, per-enrollment delete, person delete, purge-all
  (including `embeddings.json` removal and provenance clearing), roster-link null-guarding, schema guard.
- Wire: fake diarisation helper emitting `clusterEmbeddings`; missing-field back-compat path.
- `SplitSpeakersViewModel` suggestion flow with **queued** dispatcher fakes (BeginInvoke stamp-ordering
  lesson from the assistant-surfaces round — sync fakes mask ordering bugs).
- `SpeakersMerge`/re-diarise: embeddings keyed correctly through `FreshKeyRemap`.
- Helper-side embedding emission: manual smoke with real sherpa models (existing smoke path).

## Out of scope

- Auto-assignment of identities at any confidence.
- Cross-matter matching by default.
- Voice *verification* claims (this is a convenience suggester, not a forensic identification tool —
  suggestion scores are UI hints, not evidence).
- Live (mid-recording) speaker recognition; matching runs only at diarise time.
