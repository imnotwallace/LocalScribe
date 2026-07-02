# Stage 2b Golden Corpus

The fixture E2E (`GoldenCorpusFixtureTests`) runs the real pipeline (Silero VAD +
Whisper `base.en`, CPU) over a paired two-stream recording and holds WER at the
first-measured baseline (design "Quality bar": baseline + 0.05 epsilon, never a fixed
absolute). The corpus contains REAL call audio (privileged) and is therefore NEVER
committed - it lives beside the models:

    <ModelsRoot>/golden/
      local.wav             # Stage-1 smoke capture, mic side
      remote.wav            # Stage-1 smoke capture, loopback side
      reference-local.txt   # human transcript of local.wav (plain text)
      reference-remote.txt  # human transcript of remote.wav
      baseline.json         # written by the fixture on first run; commit-free

Setup: copy a Stage-1 hardware-gate capture pair (see
docs/plans/2026-07-01-stage-1-implementation-notes.md runbook) into the folder and
hand-transcribe the two sides once. Keep the pair short (1-3 min).

The silence gate needs no corpus: the fixture synthesizes 30 s of silence and asserts
the pipeline yields ZERO segments (the one hard absolute).
