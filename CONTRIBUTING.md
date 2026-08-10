# Contributing to LocalScribe

Thanks for looking. A few honest expectations first.

LocalScribe is an unpaid hobby project maintained by one person. There is **no warranty and no support
commitment**, and response times vary. Issues are read. Pull requests are welcome but may sit for a
while, and a large unsolicited PR is more likely to stall than a small focused one — if you are
planning something substantial, open an issue first so we can agree on the shape before you spend the
time.

## Reporting a bug

Please include:

- **The version.** Settings > App shows a build stamp like `0.9.0+g1a2b3c4`. Paste it verbatim — the
  git suffix identifies the exact commit.
- **What you expected and what happened.**
- **Whether the session survived.** Recordings are the point; say whether the transcript, the audio, or
  both were affected.
- **Relevant diagnostics.** Settings > App > **Open diagnostics folder** and **Copy last error**. The
  diagnostic log is a JSONL file per month under your storage root, and it **excludes transcript text
  by default** — check it before pasting anyway, since paths and session ids can embed a session title
  (which may be a client or matter name).

**Never paste transcript content, participant names, or matter names into a public issue.** Redact
before posting. If a bug can only be demonstrated with real content, say so and we will find another
way.

### Security and privacy issues

The privacy claim that matters most is that the application contains no network code. If you find
something that breaks it — anything in `src/LocalScribe.App` or `src/LocalScribe.Core` that opens a
socket, resolves a name or makes a request — that is a serious bug. Please report it privately rather
than in a public issue, via a GitHub security advisory on the repository.

The same applies to anything that would cause transcript content to leave the machine, be written into
a log, or be exposed through the MCP server without consent.

## Development setup

See [Build from source](README.md#build-from-source) in the README. Short version:

```powershell
pwsh tools/fetch-models.ps1
pwsh tools/fetch-ffmpeg.ps1
dotnet build LocalScribe.slnx
dotnet test LocalScribe.slnx --filter "Category!=Fixture"
```

You need the .NET 10 SDK and Windows — every project targets `net10.0-windows` and the capture layer is
WASAPI, so there is no cross-platform build to fall back on.

A few things that will cost you an afternoon if nobody tells you:

- **A running `LocalScribe.App.exe` locks `Core.dll`.** The build fails with MSB3027, which reads like a
  compile error and is not one. Close the app. Do not redirect the build output to work around it —
  `build/BuildOutputGuard.targets` rejects output outside the repo on purpose, because that workaround
  made repo-anchored tests validate a different source tree.
- **The diarizer and assistant helpers are not deployed by a plain `dotnet build`.** Features that need
  them will tell you so by name rather than crashing. See the README for the publish shapes — they are
  not interchangeable, and the `tools/verify-*.ps1` scripts are what check them.
- **There is no central package management.** Every version is repeated per `.csproj`, so a bump has to
  be applied in each project that references the package.

## Tests

- `--filter "Category!=Fixture"` is the gate. It must be green; `build.ps1` refuses to package
  otherwise, and CI runs it on every push and pull request.
- `Category=Fixture` tests need real model files, a published diarizer, ffmpeg, and private audio
  corpora that are not in the repo. **They cannot run on a fresh clone**, and CI only runs them on
  manual dispatch. Do not treat a fixture failure on your machine as a regression until you have
  confirmed the environment is actually provisioned — a missing model looks exactly like a broken
  feature.
- Fixture baselines self-seed: a first run with no baseline writes one and fails with "Baseline
  recorded — re-run to assert". A green second run only proves no regression against whatever *your*
  machine produced.

New behaviour should come with a test that fails without it. The suite is large because it is the only
thing standing between a refactor and a silently corrupted transcript.

## Rules this codebase will not bend on

These are not style preferences. They are the reason the project exists, and a PR that violates one
will be declined regardless of how well it is written.

1. **The transcript is evidence.** `transcript.jsonl` is append-only and never rewritten. Corrections,
   splits and speaker labels are overlays in separate files. Nothing in the product deletes, hides or
   redacts transcript content, and no feature may propose it. A redacted *copy* on export is an
   acceptable future feature; editing the master record is not.
2. **No network code in `App` or `Core`.** Downloads live in `LocalScribe.Fetch`, in a separate process,
   for exactly this reason. A test enforces it.
3. **Never silently drop a user's choice.** A pinned microphone that has vanished, a capture mode that
   had to be degraded, a model that fell back — each is disclosed with a marker in the transcript, a
   warning in the UI, or both. Silent fallback is the failure mode this app is built to avoid.
4. **Do not conflate "none" with "unknown".** The machine-generated-silence disclosure is a tri-state
   for this reason: an imported or crash-recovered file genuinely does not know, and saying "none"
   would certify synthetic audio as original recording.
5. **Recording always wins.** Transcription failure must not stop capture; the assistant yields to a
   recording and is never allowed to block one.
6. **Audio is kept.** There is no auto-expiry and no bulk delete. Deleting a session sends the whole
   folder to the Recycle Bin — recoverable, never a silent permanent wipe.

If you think one of these is wrong, that is a conversation worth having in an issue. It is not
something to settle inside a pull request.

## Pull requests

- Branch from `master`.
- Keep it focused. One concern per PR.
- Match the surrounding code — naming, comment density and idiom vary a little by subsystem, and local
  consistency beats global consistency.
- Comments should explain *why*, especially where the code looks odd. Much of this codebase is odd for a
  measured reason, and those reasons are written down next to the code.
- Update `docs/specs/localscribe-specs.md` when you change anything it documents: schemas, defaults,
  thresholds, state transitions, markers, storage layout. A spec that quietly drifts out of date is
  worse than no spec.
- British spelling in user-facing strings and docs ("organise", "labelled", "diarisation").
- ASCII only in source files.

## Licence

By contributing you agree that your contributions are licensed under the [MIT Licence](LICENSE).
