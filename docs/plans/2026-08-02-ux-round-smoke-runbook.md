# UX round 2026-08-02 - manual smoke runbook (user)

## 3.9 Per-app target watermark (Settings > Recording, Remote capture = perProcess)
- [ ] Fresh settings (Remote.App unset): the Per-app target combo shows the muted watermark "e.g. Webex, Zoom" instead of an empty box.
- [ ] Typing hides the watermark at the first character; clearing the text brings it back after focus leaves the box.
- [ ] Picking a suggestion (CiscoCollabHost) hides the watermark; clicks land in the edit box normally (the watermark never intercepts the mouse).
- [ ] Legible in both light and dark themes.

## M - Model descriptions (item 4)
- [ ] M1 Import dialog: every model row shows the technical name with a one-line plain-language
      description under it; large-v3-turbo reads "Best accuracy at fast speed - recommended".
- [ ] M2 Import dialog: turbo preselected when present; the collapsed (closed) combo showing two
      lines is acceptable; the helper sentence below the combo names no raw model IDs.
- [ ] M3 Import a file with the default selection: the run works and the read-view footer /
      version label show the technical name exactly as before (no subtitle text anywhere).
- [ ] M4 Re-transcribe dialog: two-line rows; with large-v3-turbo on disk the default is turbo
      (no longer base.en); "Current transcript: vN - model - date" line unchanged.
- [ ] M5 Re-transcribe with the default: run completes; the new version's label in the read-view
      version dropdown shows the bare technical name.
- [ ] M6 Settings > Transcription: "auto" row reads "Choose automatically for this PC"; all rows
      two-line; the combo is wide enough that no subtitle ellipsizes.
- [ ] M7 Settings: pick a model, restart the app - the pick persisted and the row is selected
      (settings.json holds the bare technical name, no subtitle text).
- [ ] M8 Drop any foreign ggml file (e.g. rename one to ggml-myfinetune.bin) into models\ and
      reopen Settings + both dialogs: "myfinetune" appears as a single-line row and is selectable.
- [ ] M9 Provenance spot-check: read-view footer "model - BACKEND", version dropdown labels,
      Record console engine chip, and an exported transcript.md header line all show bare
      technical names - zero plain-language copy outside the three pickers.

## 3.11 Split-child speaker placeholder (read view > Edit mode)
- [ ] Split a line (expand a section, caret mid-text, Split): both children's speaker boxes show "(inherits parent's speaker)" until a speaker is picked.
- [ ] Picking a speaker on a child replaces the placeholder with the selection; Save then re-Edit shows the persisted speaker, not the placeholder.
- [ ] The placeholder never blocks opening the dropdown (click lands on the ComboBox).

## 3.x End-to-end dropdown sweep (one pass over every fixed site)
- [ ] Settings > Assistant > Model: first open with chat models installed shows a selected model within seconds (never an enabled-but-blank box); with only a non-default model installed, the picker shows that model (matches what the assistant actually runs).
- [ ] Record console: in Settings pin Remote capture = perProcess with app "Webex" while Webex is NOT running - both Remote target combos (ready card and live view) show "Webex" selected, and the selection survives the 2 s refresh and a dropdown open/close.
- [ ] Session Details > Speakers: both "Add from roster" pickers show "(choose a person)"; Add is greyed until a real person is picked; picking then Add adds exactly that person to the correct side.
- [ ] Read view assistant panel on a never-summarised session: summary version combo shows "(no summaries yet)"; thread combo shows "(no conversations yet)"; after the first Regenerate/ask both show the real entries.
- [ ] Sessions page: matter filter shows "All matters" immediately on first open and after Refresh; picking a matter filters the grid; clearing back to "All matters" restores it.
- [ ] Search page: matter facet shows "All matters" with no blank flash on first navigation.
- [ ] Import dialog and Re-transcribe dialog with an empty models folder: greyed "(no models found)" selected in the model picker, Start disabled.
- [ ] Settings > Transcription with the pinned model's weights file deleted: "name (not installed)" selected; hand-edit settings.json Language to "sv": "sv (not installed)" selected. Neither state rewrites settings.json until you explicitly change the field.

## V - Video import (audio-only extraction, user addition 2026-08-02)
- [ ] V1 Sessions page: the action bar button reads "Import audio or video..."; clicking it opens a dialog titled "Import audio or video"; "Choose file..." shows video containers (MP4, MOV, MKV, WEBM, AVI, WMV) alongside the existing audio ones in the file-picker's filter dropdown.
- [ ] V2 Import a real .mp4 recording (e.g. a Webex/Zoom local recording with a video track): the probe preview shows a plausible duration/size/format; Start runs Copy -> Decode -> Transcribe -> Save exactly like an audio import, with no video-specific error.
- [ ] V3 After the import completes, open the session: the transcript contains real speech text (not silence or noise pulled from the video track); the audio player plays back the extracted audio only.
- [ ] V4 Provenance: Session Details (or an exported transcript header) shows the ORIGINAL video file's name (e.g. "recording.mp4"), never a renamed or transcoded filename.
- [ ] V5 Hover the Import button while FFmpeg is present: the tooltip mentions video formats (MP4 etc.) alongside the audio ones.
