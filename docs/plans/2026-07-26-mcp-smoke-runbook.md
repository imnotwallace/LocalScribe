# MCP server - real-model smoke runbook (user-run)

Prereqs: LocalScribe.Mcp.csproj built, LocalScribe.App.exe published, an MCP client (Claude Desktop or Claude Code), a real transcript corpus with at least one session and one matter, an embedding model available.

## Publish and guard

- [ ] M1. Publish the MCP server: `dotnet publish src/LocalScribe.Mcp -c Release -o <app publish dir>` (where `<app publish dir>` is the same directory containing `LocalScribe.App.exe`).
- [ ] M2. Verify layout: `pwsh tools/verify-mcp-publish.ps1 -PublishDir <app publish dir>` -> PASS message and exit code 0.

## Registration and connectivity

- [ ] C1. In LocalScribe.App Settings page, MCP Access section, copy the generated client config snippet (for Claude Desktop: a JSON object; for Claude Code: a `claude mcp add` command).
- [ ] C2. Register the server in your MCP client (Claude Desktop: paste JSON into `claude_desktop_config.json` under `mcpServers`; Claude Code: run the `claude mcp add` command).
- [ ] C3. Restart the client.
- [ ] C4. In the client, verify the server appears as available. Then ask the client to list the available MCP tools -> PASS: exactly 6 tools appear: `list_matters`, `list_sessions`, `search_transcripts`, `read_transcript`, `search_semantic`, `read_session_text`.

## End-to-end quoting

- [ ] E1. In the client, ask it to find a real topic that you know was discussed in your recordings (e.g., "Where did we discuss <specific topic>? Quote the relevant part.").
- [ ] E2. The client returns a quote with citations. Check the citation format -> PASS: each citation includes a session ID (e.g., `session-12345`) and a sequence range (e.g., `seq 45-52`), and the text snippet exactly matches the corrected text visible in LocalScribe.App's read view for that session (respecting any manual corrections you made; speaker names match the identity-corrected names in the app, not the raw transcription).

## Consent polarity and immediate revocation

- [ ] P1. In LocalScribe.App Settings, MCP Access section, disable MCP entirely (toggle off).
- [ ] P2. In the MCP client, attempt to call any tool (e.g., `list_matters`) -> PASS: the tool returns an error message "MCP access not enabled in LocalScribe Settings" (or similar clear denial).
- [ ] P3. In LocalScribe.App Settings, enable MCP (toggle on) and select exactly ONE matter to expose.
- [ ] P4. In the MCP client, call `list_matters` -> PASS: only the one selected matter appears in the list.
- [ ] P5. Call `search_transcripts` with a broad query -> PASS: only sessions from the exposed matter appear in the results.
- [ ] P6. Call `list_sessions` -> PASS: only sessions from the exposed matter appear.
- [ ] P7. While the client conversation is still open, go back to LocalScribe.App Settings MCP Access section and UNTICK the matter (disable it) without restarting the client or the app.
- [ ] P8. In the client, immediately call any of the tools again (e.g., `list_sessions`) -> PASS: the tool returns the access-denied error message (revocation is immediate, no client/server restart required).

## Audit trail

- [ ] A1. In File Explorer, navigate to `<storageRoot>/mcp/audit/` (where `<storageRoot>` is LocalScribe's storage root, typically `%APPDATA%/LocalScribe`).
- [ ] A2. Open the file `audit-YYYYMM.jsonl` (the current month's audit log).
- [ ] A3. Each line is one JSON object representing one tool call. Verify at least 6 lines exist (one per tool, from the earlier tests) and review their structure -> PASS: each object contains fields like timestamp, tool_name, caller_id (or equivalent client identifier), and result status (allowed/denied). No line contains transcript text or audio data.
- [ ] A4. Check that denied calls (from P2 and P8) are also logged -> PASS: audit includes the denied calls with a clear denied status.

## Semantic during recording

- [ ] S1. Start a new recording in LocalScribe.App (click Record button).
- [ ] S2. Let it record for a few seconds.
- [ ] S3. While recording is active and running, switch to the MCP client and run a semantic search query (e.g., `search_semantic` with a query that should match existing sessions) -> PASS: the query returns results quickly and completes.
- [ ] S4. Switch back to LocalScribe.App and verify the recording is still ongoing (waveform animating, elapsed time increasing) and encounters no errors -> PASS: the app continues recording cleanly without freezes or crashes.

## App closed entirely

- [ ] D1. Close LocalScribe.App entirely (quit the application, not just minimize or close a window).
- [ ] D2. In the MCP client, call all six tools in sequence:
  - `list_matters`
  - `list_sessions`
  - `search_transcripts` with a broad query
  - `read_transcript` with a valid session ID and sequence range from earlier tests
  - `search_semantic` with a query
  - `read_session_text` with a valid session ID
- [ ] D3. PASS: all six tools return results or clear error messages (never "server unavailable" or connection timeouts). The content is current and reflects the corpus on disk.

## Missing embedding model

- [ ] M1. In File Explorer, navigate to `<storageRoot>/models/` and locate `assistant-manifest.json`.
- [ ] M2. Rename it to `assistant-manifest.json.bak` (to simulate missing embedding model).
- [ ] M3. In the MCP client, call `search_semantic` with a query -> PASS: the tool returns a clear error message such as "semantic search unavailable: embedding model not found" or similar; the tool does not crash or hang.
- [ ] M4. Call `search_transcripts` (lexical search) with the same query -> PASS: the tool returns results normally (lexical/text search is unaffected by missing embedding model).
- [ ] M5. Call `read_transcript` -> PASS: read still works.
- [ ] M6. Rename `assistant-manifest.json.bak` back to `assistant-manifest.json` to restore the embedding model.

## Split-segment fidelity (if available)

- [ ] T1. (Only run this check if your corpus contains at least one session with a manually split segment. If not, mark this section as N/A.)
- [ ] T2. In the MCP client, run `search_transcripts` or `search_semantic` with a query that you know matches a split part (not the whole segment).
- [ ] T3. A hit is returned pointing to that split part (with `seq` anchors or a part_index).
- [ ] T4. Call `read_transcript` with `around_part_index` set to the part that was hit -> PASS: the returned snippet is centered on the split part that the search pointed at, not the whole unsplit segment.

## What this round deliberately does NOT expose

The MCP server implements read-only access to transcripts. The following are intentionally NOT exposed:

- Audio files or audio data of any kind.
- The people/voiceprint registry (speaker identity model data).
- Export functionality (docs, PDFs, archives).
- Any mutation or write capability (no edit, no delete, no matter creation/deletion).

Confirm none of these appear in your testing.
