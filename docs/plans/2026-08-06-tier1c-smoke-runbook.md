# Tier 1C smoke runbook (trustworthy-output round)

Branch `feat/tier1c-tier-c-trustworthy-output-2026-08-05` @ `38f2e27`.
Static suite is green (Core 1329 / App 1092 / Mcp 6 = 2427). **These ten items are the ones a static
suite cannot settle** - two are judgement calls only a solicitor can make, the rest need a real
recording, real Word, or a real crash.

Do not merge until items 1-7 pass. Items 8-10 need extra setup; skip-with-a-note is acceptable for
those, failing is not.

---

## Setup (do this first - four of these WILL bite you)

**1. Point the app at the models.** `ModelPaths` walks up for `LocalScribe.slnx` and then looks for
`models\` beside it - and this worktree has its own `.slnx` but **no `models\` folder**. Without the
override, Start refuses with "Model 'small.en' is not downloaded" and you will think the round broke
recording.

```powershell
$env:LOCALSCRIBE_MODELS = 'F:\LocalScribe\models'
```

Set it in the SAME shell you launch from.

**2. Close any running LocalScribe.** The single-instance guard means a second copy hands off its
argv to the first and exits - you would be smoking the OLD build without noticing.

```powershell
Get-Process -Name 'LocalScribe.App' -ErrorAction SilentlyContinue |
    Select-Object Id, Path        # expect nothing; if not, close it from its tray icon
```

**3. Launch this branch's build and confirm it.**

```powershell
cd F:\LocalScribe\.claude\worktrees\feat+tier1c-tier-c-trustworthy-output-2026-08-05
dotnet run --project src/LocalScribe.App
```

**Settings > About** must show a SHA starting `38f2e27` (or whatever `git rev-parse --short HEAD`
says). This matters more than usual here: half the round is invisible unless you are on the new
build, and an old build produces no `manifest.json` at all.

**4. THE TRAP THAT WILL COST YOU AN HOUR: sessions recorded before this build have no seal, and
that is CORRECT.** `manifest.json` is written at finalize. Every session already in your library
predates this round, so **"Verify integrity" on any of them says it has no seal and that is a PASS,
not a bug** (item 5 makes it an explicit check). Only sessions you record AFTER launching this build
are sealed. Do not go hunting for a missing manifest on an old session.

**5. Second trap, same family: a CRASH-RECOVERED session's audio is deliberately not sealed.**
Hashing at finalize is the only place audio is hashed; the launch-time recovery scan passes the cost
gate `sealAudio:false` on purpose, so a leg that has never been hashed is LEFT OUT of the manifest
rather than sealed with bytes nobody read. So on a crash-recovered session, "Verify integrity"
reports the TEXT files only and makes no claim about `local.flac`. That is the designed behaviour -
a retroactive whole-library hash would be an unbounded, un-cancellable, unconsented multi-hour read.
Report it if you see it, but it is not a failure.

**6. Where to look.** Storage root defaults to `%USERPROFILE%\LocalScribe`. Paste these helpers -
every item below uses them:

```powershell
function Latest-Session { (Get-ChildItem "$env:USERPROFILE\LocalScribe\sessions" -Directory |
    Sort-Object LastWriteTime -Desc | Select-Object -First 1).Name }

function Show-Engine([string]$Id) {
  $d = Join-Path $env:USERPROFILE "LocalScribe\sessions\$Id"
  $s = Get-Content (Join-Path $d 'session.json') -Raw | ConvertFrom-Json
  "session.json model  : $($s.model)"
  "session.json backend: $($s.backend)"
  "session.json weights: $($s.weightsFile)"
  "--- first 3 transcript lines ---"
  Get-Content (Join-Path $d 'transcript.jsonl') | Select-Object -First 3 |
    ForEach-Object { $l = $_ | ConvertFrom-Json; "  [{0,7} ms] {1,-7} {2}" -f $l.startMs, $l.kind, $l.text }
}

function Show-Manifest([string]$Id) {
  $p = Join-Path $env:USERPROFILE "LocalScribe\sessions\$Id\manifest.json"
  if (-not (Test-Path $p)) { "NO manifest.json (unsealed - see setup trap 4)"; return }
  $m = Get-Content $p -Raw | ConvertFrom-Json
  "writtenAtUtc: $($m.writtenAtUtc)   version: $($m.versionId)"
  foreach ($f in $m.files) {
    "  {0,-24} {1}  {2,10} bytes" -f $f.name, $f.sha256.Substring(0,16), $f.sizeBytes
    if ($f.name -match '\.(flac|wav)$') {
      "      sampleRate={0}  fabricatedSilenceKnown={1}  spans={2}" -f `
        $f.sampleRate, $f.fabricatedSilenceKnown, $f.fabricatedSilence.Count
      foreach ($s in $f.fabricatedSilence) {
        "        {0,-10} {1:N2}s .. {2:N2}s" -f $s.reason,
          ($s.startSample / [double]$f.sampleRate), ($s.endSample / [double]$f.sampleRate)
      }
    }
  }
}
```

**A note on your real data.** These items write new sessions into your real storage root. Item 6
asks you to hand-edit a `transcript.jsonl` - use a session you recorded FOR this smoke, never real
client material, and undo the edit afterwards.

---

## 1. Which engine actually ran - the headline judgement call (T1-6)

This is the item the spec singles out (`:213-218`), and it is the only one that can revisit the
owner's ruling that the live model cap stays. It needs a real call, not a test tone.

1. Before pressing Record, look at the **ready card's engine chip**. It must read
   `<model> · <BACKEND> · <tier>`, e.g. `small.en · CUDA · Decent accuracy`.
2. **Hover it.** The tooltip must name the REMEDY, not merely restate the cap:
   *"Live capture uses a faster model to keep up with realtime. For a session that matters,
   re-transcribe it at higher accuracy afterwards (Sessions > Re-transcribe...)."*
3. Record a **real Webex call** - actual speech, both sides, several minutes. Stop.
4. `Show-Engine (Latest-Session)`.

**PASS so far:** the FIRST transcript line is a marker at `0 ms` reading
`transcription engine: <model> (<BACKEND>), <tier>`, and the model it names matches the chip you
read in step 1.

5. Now **Import** the same audio (`sessions\<id>\local.flac`, or the call recording) as a new
   session with the model picker on **large-v3-turbo**.
6. Open both transcripts side by side and read them as a solicitor would.

**Report a judgement, not a pass/fail:** is the difference material? Names, numbers, dates and
legally-operative words are what matter - not stylistic wording. The ruling that live stays capped is
revisited only on this evidence, so a specific example ("it heard 'Doe' as 'Dough' throughout")
is worth more than an impression.

**FAIL:** no marker at all, a marker that names a different model than `session.json`, a chip with no
accuracy tier, or a tooltip that does not mention re-transcription.

---

## 2. The disclosure is visible while recording, not just on disk (T1-6)

The marker is queued into the outbox before Start raises its state change, and a bare list-clear
used to be able to wipe it from the live view. That is fixed; this confirms the fix on real timing.

1. Start a recording and **watch the live transcript list immediately**, before anyone speaks.
2. Toggle to the **compact pill**.

**PASS:** the `transcription engine: ...` line appears in the live list within a second or two of
Start, and the compact pill shows it too (rather than sitting on "Listening" for the whole call).

**FAIL:** the list stays empty / shows only "Listening" until the first spoken segment arrives, even
though `transcript.jsonl` has the marker. That means the enter-Recording rebuild regressed.

---

## 3. The seal, and the silence it admits to (T1-7)

The point of the round: a hash that seals a FLAC without saying which parts of it the app itself
fabricated would certify machine-generated silence as original recorded audio.

1. Using the session from item 1, run `Show-Manifest (Latest-Session)`.

**PASS:**
- `session.json`, `meta.json` and `transcript.jsonl` each carry a 64-char `sha256`.
- **Every retained leg** (`local.flac`, `remote.flac`) carries a `sha256`, `sampleRate` 16000, and
  `fabricatedSilenceKnown = True`.
- Each leg has **at least one `end-pad` span** - a clean Stop always pads the file to the stop
  instant, so this is guaranteed, and its absence means the writer's ranges never reached the
  manifest.
- **The numbers look sane.** The `end-pad` should be a few seconds at the very end of the file, not
  minutes. On a call with a genuine dropout or a pause you should also see `clock-gap` spans, and
  their times should line up with when the audio actually went quiet.

**FAIL:** no `manifest.json` on a session recorded by this build; a leg with
`fabricatedSilenceKnown = False`; no `end-pad`; or an `end-pad` spanning most of the file (that would
mean the leg captured almost nothing and the file's length is nearly all fabrication - a real
capture bug the seal has just exposed).

---

## 4. Verify integrity survives ordinary editing (T1-7)

A false tamper verdict is the one outcome this command must never produce, and ordinary use is where
that would show up.

1. Select the item-1 session on the Sessions page and press **Verify integrity**.
   **PASS:** *"Integrity check passed for ... : N files match the seal written ..."*
2. Open it in the read view, **correct one line**, save.
3. **Verify integrity again.**
   **PASS:** still passes. The overlay write reseals through the projection choke point.
4. In Session Details, **rename a speaker** (or pin one), save. Verify again.
   **PASS:** still passes.
5. Edit the session **title** in Session Details, save. Verify again.
   **PASS:** still passes (meta.json is sealed too).

**FAIL:** any `session.json CHANGED`, `meta.json CHANGED`, `speakers.json CHANGED` or
`transcript.jsonl CHANGED` after an edit you made through the app. That is the false verdict, and it
means a writer is mutating a sealed file without resealing.

---

## 5. Verify integrity across versions, and on an old session (T1-7)

The version switch is the mutation that skips the projection regen by design, so it is the one most
likely to strand a manifest.

1. **Re-transcribe** the item-1 session to create a v2. Wait for it to finish.
2. **Verify integrity.** PASS: passes (it verifies the ACTIVE version, now v2).
3. In the read view's version switcher, **switch back to v1**.
4. **Verify integrity** again. **PASS: passes.**
5. Switch to v2 and verify once more. **PASS: passes.**

**FAIL:** `session.json CHANGED` at step 4 or 5. The version switch rewrites `session.json` and does
not regenerate projections, so if the reseal did not land, this command invents a tamper verdict on a
session nobody touched.

6. Now pick a session recorded **before this build** and press **Verify integrity**.

**PASS:** *"... has no integrity seal - it was recorded before integrity manifests existed, or its
manifest.json was deleted. Nothing can be verified."*

**FAIL:** a *pass* result. "Nothing to check" and "everything checks out" are opposite claims, and
reporting the first as the second is a false assurance.

7. Note that session's `session.json` modified timestamp, run **Verify integrity twice more**, and
   check it again.

```powershell
(Get-Item "$env:USERPROFILE\LocalScribe\sessions\<old-id>\session.json").LastWriteTimeUtc
```

**PASS:** unchanged. A verifier that writes what it is about to hash verifies nothing - and an old
`session.json` is exactly the shape that would get migrated in place.

---

## 6. Verify integrity actually detects tampering (T1-7)

Everything above proves it does not cry wolf. This proves it can bark. **Use a session you recorded
for this smoke, not real client material.**

1. Open `sessions\<smoke-id>\transcript.jsonl` in Notepad. Change one word inside one line's `text`.
   Save.
2. **Verify integrity.**

**PASS:** *"Integrity check FAILED for ...: transcript.jsonl CHANGED. N of M files match the seal
written ..."* - naming the file, not just counting.

3. **Undo the edit** (restore the exact original word) and verify again - it should pass. If you
   cannot restore it byte-for-byte, correct the line through the app instead, which reseals.
4. Optional, and the graver verdict: rename `local.flac` to `local.flac.bak` and verify.
   **PASS:** `local.flac MISSING`, listed BEFORE any CHANGED file. Rename it back.

**FAIL:** a pass after a hand edit, or a failure that gives only a count without naming the file.

---

## 7. The exported document, read in real Word (T1-7 / T1-8)

The SDK accepts an invalid `pPr` child order silently; only Word tells the truth. This is also where
a solicitor's opponent actually reads the disclosures.

1. Export the item-1 session (after item 4's correction) as **.docx**. Open it in **real Word**.
2. In the metadata block at the top, confirm every one of these:
   - `Session ID: <the folder id>`
   - `Exported: YYYY-MM-DD HH:MM UTC by LocalScribe <version>`
   - `Transcript version: v1 · <model> · <backend>` (or v2)
   - `Weights file: ggml-....bin`
   - `Model accuracy: <the catalog subtitle>`
   - `Transcript SHA-256: <64 hex>`
   - `Audio SHA-256 (local.flac): <hash> (includes N machine-generated silence spans, HH:MM:SS
     total)` - and the same for `remote.flac`
   - `Human edits: 1 text correction, ...` (matching what you actually did in item 4)
3. Find the turn you corrected. It must read `Name [text corrected]:`.
4. **Scroll to page 2 or later and look at the running head.** It must show the speaker name
   **WITHOUT** the mark - `Sam`, never `Sam [text corrected]`.
5. Check the transcript's **line numbers still start at 1** on the first turn, and that the metadata
   lines are not numbered.
6. Word must not complain the file is corrupt on open, and **File > Info** must show no repair notice.

**FAIL, in descending seriousness:** Word repairs the file; the mark appears in the running head
(STYLEREF returns the speaker run verbatim, so this would put it on every page); line numbering
restarts or covers the metadata; any listed line missing or showing an empty value after its colon.

7. Export the same session as **.md** and **.txt** and confirm all three carry the same set of lines
   with the same wording.

**PASS:** identical facts in all three; only the decoration differs.

---

## 8. The correction mark is a choice, and it is remembered (T1-8)

1. Export the item-4 session as **.txt** with **Mark corrected turns** ticked (the default - confirm
   it starts ticked).
2. Export again with it **unticked**.
3. Close the dialog, reopen it.

**PASS:** the first file carries `[text corrected]` on the corrected turn, the second does not, and
on reopening the checkbox is **remembered unticked**. Re-tick it before moving on - it is meant to
ship on.

---

## 9. Import with speaker detection, then verify (T1-7)

This covers a reseal path with no end-to-end automated test: import-time speaker detection rewrites
`transcript.jsonl`, `session.json` and `meta.json` AFTER the importer has already sealed the folder.

1. **Import** an audio file with **Speakers: Auto** (or a declared count).
2. When it finishes, select the new session and press **Verify integrity**.

**PASS:** passes. (Note that an imported session's audio is not sealed - see setup trap 5 - so this
verifies the text files. The point is that none of them reports CHANGED.)

**FAIL:** `transcript.jsonl CHANGED`, `session.json CHANGED` or `meta.json CHANGED`. Detection wrote
those files and did not reseal.

3. Optional, same family: **Settings > purge all voiceprint data**, then verify a session that had
   speaker suggestions. **PASS:** still passes.

---

## 10. Engine-ladder behaviour under pressure (T1-6)

Only reachable on hardware that actually runs out of VRAM, so skip-with-a-note is fine.

1. Pick **large-v3-turbo** explicitly in Settings and record on a GPU that cannot hold it.

**PASS:** the session steps DOWN the ladder (a `transcription weights changed: ... -> ...` marker
naming `large-v3`) rather than falling straight to CPU with no ladder step. Before this round,
`Downgrade("large-v3-turbo")` returned null and that user got no ladder at all.

2. On a machine slow enough to lag sustainedly through a long call, check for **more than one**
   `transcription lagging` marker, capped at three.

**PASS:** up to three, then silence - not exactly one for a two-hour call that degraded throughout,
and not one per window.

---

## Reporting back

For each item: pass / fail / skipped-and-why. For any failure, the useful artefacts are:

- `Show-Engine <id>` and `Show-Manifest <id>` for that session,
- the exact InfoBar text from "Verify integrity",
- the `.docx` itself for item 7,
- `%USERPROFILE%\LocalScribe\diagnostics\diag-<yyyyMM>.jsonl`, and **Settings > Copy last error**.

Items 1-7 are the merge gate. Item 1's second half is a judgement call rather than a pass/fail, and
its answer is the only thing that can reopen the live-model-cap ruling - so answer it even if
everything else passes.
