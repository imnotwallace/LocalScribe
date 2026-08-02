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
