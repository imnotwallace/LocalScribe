# MCP server - real-model smoke runbook (user-run)

Everything in this round is covered by automated tests EXCEPT what needs a real MCP client, a real
embedding model, and a real recording. That is what this runbook covers.

Prereqs: `LocalScribe.App.exe` published, an MCP client (Claude Desktop or Claude Code), a real
transcript corpus with at least one session and at least two matters (so you can prove one matter
stays invisible), and an embedding model installed.

The six tools are exactly: `search_transcripts`, `search_transcripts_semantic`, `read_transcript`,
`list_sessions`, `list_matters`, `get_summary`.

## Publish and guard

- [ ] P1. Publish the MCP server: `dotnet publish src/LocalScribe.Mcp -c Release -o <app publish dir>`
  (where `<app publish dir>` is the directory containing `LocalScribe.App.exe`).
- [ ] P2. Verify layout: `pwsh tools/verify-mcp-publish.ps1 -PublishDir <app publish dir>` -> PASS
  message and exit code 0.

## Registration and connectivity

- [ ] C1. In LocalScribe Settings > MCP Access, copy the generated client config snippet.
- [ ] C2. Register it (Claude Desktop: paste the JSON into `claude_desktop_config.json` under
  `mcpServers`; Claude Code: run `claude mcp add localscribe -- <exe> --storage-root <root>`).
- [ ] C3. Restart the client.
- [ ] C4. Ask the client to list the available MCP tools -> PASS: exactly these six appear -
  `search_transcripts`, `search_transcripts_semantic`, `read_transcript`, `list_sessions`,
  `list_matters`, `get_summary`. (A missing or extra tool means the contract drifted.)

## End-to-end quoting

- [ ] Q1. Ask the client to find a topic you know was discussed, and to quote it - for example
  "Where did we discuss <topic>? Quote the relevant part."
- [ ] Q2. PASS: each hit cites a session id and a `seq` anchor, and the quoted text matches what
  LocalScribe's read view shows for that session - corrected text (your manual corrections applied)
  and the identity-corrected speaker names, not raw transcription output.

## Consent polarity and immediate revocation

- [ ] N1. In Settings > MCP Access, turn MCP access OFF.
- [ ] N2. In the client, call any tool -> PASS: it returns exactly
  "MCP access not enabled in LocalScribe Settings".
- [ ] N3. Turn MCP access ON (accept the confirm) and tick exactly ONE matter.
- [ ] N4. Call `list_matters` -> PASS: only that one matter appears.
- [ ] N5. Call `search_transcripts` with a broad query -> PASS: only sessions from the ticked matter
  appear. Sessions from the other matter must be absent entirely.
- [ ] N6. Call `list_sessions` -> PASS: same - only the ticked matter's sessions.
- [ ] N7. With the client conversation STILL OPEN, return to Settings > MCP Access and untick that
  matter. Do not restart the client, the server, or the app.
- [ ] N8. In the same conversation, immediately call a tool again -> PASS: denied. Revocation takes
  effect on the very next call, with no restart anywhere.

## Audit trail

- [ ] A1. Open `<storageRoot>/mcp/audit/audit-YYYYMM.jsonl` for the current month. `<storageRoot>` is
  LocalScribe's storage root - by default `%USERPROFILE%/LocalScribe`, not `%APPDATA%`. (Settings >
  MCP Access has a button that opens this folder for you.)
- [ ] A2. PASS: one JSON line per tool call you made above. Each carries `ts_utc`, `tool`,
  `args_json`, `session_ids`, `matter_ids`, `result_count`, `result_chars`, `outcome`.
- [ ] A3. PASS: the denied calls from N2 and N8 are present with `"outcome":"denied"`. An audit that
  only records successes would defeat the point.
- [ ] A4. PASS: search the file for a distinctive phrase from a transcript you read in Q1/Q2 - it must
  NOT appear. The log records what was asked and how much came back, never the text itself.

## Semantic during recording

- [ ] S1. Start a recording in LocalScribe and let it run for a few seconds.
- [ ] S2. While it is still recording, run a semantic query from the client
  (`search_transcripts_semantic`) -> PASS: it answers.
- [ ] S3. PASS: the recording continues cleanly - elapsed time still advancing, no error banner, and
  the finished session transcribes normally.

## App closed entirely

- [ ] D1. Quit LocalScribe completely (not minimised to tray).
- [ ] D2. From the client, call all six tools: `search_transcripts`, `search_transcripts_semantic`,
  `read_transcript` (with a session id and seq from earlier), `list_sessions`, `list_matters`,
  `get_summary`.
- [ ] D3. PASS: all six work against the storage root with the app closed. Content reflects what is on
  disk.

## Missing embedding model

- [ ] E1. Locate the models root and its `assistant-manifest.json`. NOTE: the models root is NOT under
  the storage root - it is whatever `LOCALSCRIBE_MODELS` points at, else `models/` at the repo root
  (the folder containing `LocalScribe.slnx`), else `models/` beside the binary.
- [ ] E2. Rename it to `assistant-manifest.json.bak`.
- [ ] E3. Call `search_transcripts_semantic` -> PASS: returns an error beginning with
  "semantic unavailable:"; it neither crashes nor hangs.
- [ ] E4. Call `search_transcripts` with the same query -> PASS: lexical search is unaffected.
- [ ] E5. Call `read_transcript` -> PASS: reads still work.
- [ ] E6. Rename `assistant-manifest.json.bak` back. Do not skip this step.

## Split-segment fidelity (only if your corpus has one)

- [ ] T1. Skip this section unless a session contains a manually split segment. Mark N/A otherwise.
- [ ] T2. Search for text you know lives in a specific split PART, not the whole segment.
- [ ] T3. PASS: the hit reports both a `seq` and a `part_index`.
- [ ] T4. Call `read_transcript` with `around_part_index` set to the part the hit named -> PASS: the
  window is centred on that part, not on the first part of the same seq.

## What this round deliberately does NOT expose

Read-only over transcripts, and nothing else. Confirm none of the following ever appears:

- Audio files or audio data of any kind.
- The people/voiceprint registry (speaker identity data).
- Export functionality (docx, zip archives).
- Any mutation whatsoever - no edits, no deletes, no speaker or matter changes, no index writes.

The only files the server itself writes are its own audit log, and the only file the Settings page
writes for this feature is `<storageRoot>/mcp/consent.json`.
