# MCP Server over the Local Corpus — Design

Date: 2026-07-26
Status: Approved design, pre-plan
Origin: Adopt item #3 from the OpenWhispr competitive analysis (2026-07-25).
Item #1 (voice fingerprinting) merged @ 9a36bdc; item #2 (semantic search)
merged @ eae34db — its spec explicitly deferred MCP reuse of
`SemanticQueryEngine` to this design. Still queued after this: the
Parakeet-via-sherpa ASR spike.

## Goal

Let an MCP client (Claude Desktop, Claude Code, etc.) search and read the
LocalScribe transcript corpus — "find where we discussed the settlement figure
and quote it" — without the corpus ever leaving the machine except through the
user's own deliberate client usage.

## Hard constraints (non-negotiable)

- **Fully local.** Stdio transport only. No network listener, no port, no
  daemon, no cloud anything.
- **Evidentiary firewall: strictly read-only over the corpus.** No MCP tool
  may mutate sessions, edits, speakers, matters, or indexes. Ever. The only
  write path in the server process is the audit log under `<storageRoot>/mcp/`.
- **Consent-grade exposure.** This is a privileged legal corpus. Exposure is
  opt-in, per-matter, default-dark, revocable live.
- **Native isolation rules stand.** Core keeps ORT 1.22.0; llama.cpp stays in
  the assistant helper; the server respects the one-warm-embed-helper spirit
  and the recording-gate posture (see Concurrency).

## Decisions (user-settled during brainstorm)

| Question | Decision |
|---|---|
| Standalone? | Fully standalone exe over the storage root; works with the App closed, including semantic. |
| Tool surface extras | Assistant summaries: yes. Real projected speaker names: yes. Per-session marker *detail* tool: deferred (marker rows still appear inline in reads). Audio: never exposed. People/voiceprint registry: never exposed. |
| Consent scope | Per-matter allowlist + explicit "unassigned sessions" toggle. Default: nothing exposed. Fail closed. |
| Audit | Append-only JSONL audit log of every tool call (including denied ones). Never logs returned transcript text. |
| Recording posture | Everything stays available during a live recording, including semantic (matches the App's own query-embed exemption; one-shot respawn, short idle reclaim). |
| Setup UX | App Settings "MCP Access" section: consent controls + copyable config snippet. LocalScribe never writes another app's config file. |
| Architecture | Approach A: new `LocalScribe.Mcp` console exe on the official `ModelContextProtocol` C# SDK, stdio transport, Core reuse throughout. |

Approaches rejected: MCP as a mode of `LocalScribe.Assistant.exe` (wrong
lifecycle — that helper is designed to be killed on recording start; drags the
LLamaSharp folder-publish layout into the server identity); hand-rolled MCP
over stdio (MCP is JSON-RPC 2.0 + a moving lifecycle spec; the other end is
Claude Desktop, not our own code — protocol drift lands on us).

## Architecture

### Project & process model

- New `src/LocalScribe.Mcp` (`OutputType=Exe`, net10.0-windows). References
  `LocalScribe.Core` and the official `ModelContextProtocol` NuGet package
  (exact version pinned during planning). Pure managed — no native deps.
- Published beside `LocalScribe.App.exe`, next to the `assistant\` folder so
  `AssistantHelperLocator` resolves unchanged. Publish-layout guard script
  asserts its presence.
- The MCP client spawns `LocalScribe.Mcp.exe --storage-root <path>` and owns
  its lifetime. If `--storage-root` is omitted, resolve the same default the
  App uses (`%USERPROFILE%\LocalScribe`); the Settings-generated snippet
  always passes it explicitly.
- Protocol frames own stdout exclusively; all diagnostics go to stderr.

### Read paths (existing Core seams — no new read machinery)

- **Lexical:** at startup, build the in-memory index the way
  `SearchIndexService.InitializeAsync` does — using `index/search-index.json`
  as a *read-only* seed, re-deriving stale/missing sessions via
  `SessionProjectionLoader`. The server never writes the cache file;
  self-heal writes remain App-only.
- **Semantic:** `.vec` sidecars via `SemanticIndexStore`; ranking via the pure
  `SemanticQueryEngine`; facet semantics shared with lexical via
  `SearchQueryEngine.PassesFacets`. Query embedding through the existing
  `AssistantEmbeddingClient` spawning `LocalScribe.Assistant.exe` via
  `AssistantHelperLocator`, with a shorter idle reclaim (60–90s vs the App's
  5min) since MCP queries are bursty.
- **Transcript reads:** `SessionProjectionLoader.LoadAsync` — corrected text,
  active version, projected speaker display names. Same one-true-read-path as
  the UI. Marker rows are included inline as typed entries.
- **Summaries:** the `sessions/{id}/assistant/summaries.json` sidecar, with
  provenance (model, cuda-fell-to-cpu flag, generated-at).

### Freshness

The App owns the indexes and may write while the server reads.

- Refresh-on-query with an mtime short-circuit: at most once per ~10s,
  re-stat `sessions/`; re-derive only changed/new session folders.
- Every search response carries `index_as_of` (UTC timestamp of last refresh).
- Semantic responses carry the same coverage/staleness honesty as the UI
  (sidecar freshness stamps vs current versionId).
- All corpus file opens use `FileShare.ReadWrite | FileShare.Delete` — never
  block the App. The App's atomic temp+rename writes mean readers always see
  consistent files.

### Read-only enforcement

Structural, not policy: the process contains exactly one write path — the
audit appender under `<storageRoot>/mcp/`. No Core mutation service is ever
constructed; sessions, edits, speakers, matters, and both indexes are
untouchable because no code that writes them is reachable from the server.

## Tool surface (v1 — tools only, no MCP resources)

Contract carries an explicit `contract_version: 1` in server info alongside
`name: "localscribe"` and the assembly version.

1. **`search_transcripts`** — lexical. Args: `query` (required); optional
   `matter_id`, `from_date`, `to_date`, `app`, `limit` (default 10, max 50).
   Hits return `session_id`, matter, `started_local`, app, speaker, `seq`
   anchor + start/end ms, and a ~300-char snippet around the match — the same
   anchors the UI uses, so every quote is traceable.
2. **`search_transcripts_semantic`** — "related discussion." Same facet args.
   Embeds the query via the helper, ranks with `SemanticQueryEngine`, returns
   chunk hits in the same anchor shape plus `score`, and a `coverage` block
   (`sessions_covered` / `sessions_eligible`, `stale_count`). If the assistant
   helper is missing/unavailable, this tool errors clearly ("semantic
   unavailable: …") while lexical stays up.
3. **`read_transcript`** — Args: `session_id`, then either `from_seq`/`to_seq`
   or `around_seq` + `context` (segments each side). Returns projected rows:
   seq, start/end ms, speaker display name, corrected text; marker rows
   included as typed entries. Hard cap ~15k chars per call with `next_cursor`
   continuation — clients page rather than pulling a two-hour call at once.
4. **`list_sessions`** — facet args + paging; returns id, title, date, matter,
   app, duration, `has_summary`.
5. **`list_matters`** — allowlisted matters only: id, display name, session
   count.
6. **`get_summary`** — `session_id`; returns the assistant summary sidecar
   text + provenance.

Deliberately unexposed: audio (paths or bytes), the people/voiceprint
registry, exports, and any mutation of anything.

## Consent

- The App's Settings page writes `<storageRoot>/mcp/consent.json`:
  `{ enabled, allowed_matter_ids[], allow_unassigned, updated_utc }` —
  atomic temp+rename.
- File absent or `enabled:false` ⇒ every tool returns a uniform "MCP access
  not enabled in LocalScribe Settings." **Fail closed.**
- The server mtime-checks and re-reads consent on **every** tool call —
  unticking a matter revokes mid-conversation.
- One choke point: a single `ConsentFilter` gates all six tools **before**
  any engine runs:
  - Non-allowlisted sessions are excluded from lexical and semantic candidate
    sets pre-ranking — no leakage via scores, counts, or coverage numbers
    (coverage is computed over the *allowlisted* eligible set).
  - `read_transcript` / `get_summary` on a non-allowlisted session return
    "not found or not exposed" — indistinguishable from nonexistence, so
    existence doesn't leak.
  - `list_matters` shows only ticked matters; unassigned sessions ride the
    `allow_unassigned` toggle.

## Audit

- Every tool call appends one JSON line to
  `<storageRoot>/mcp/audit/audit-YYYYMM.jsonl`: timestamp, tool, sanitized
  args (query text yes — it's the user's own client's query; returned
  transcript text never), session/matter ids touched, result count + bytes
  returned, outcome (`ok` / `denied` / `error`).
- Consent-denied calls are logged too.
- Rotation is the monthly filename; no size pruning (keep-everything posture).

## Concurrency & memory posture

- No recording detection; all tools stay available during a live recording.
  Query embeds are one-shot respawns — consistent with the semantic spec's
  existing exemption for the App's own query path.
- Worst case is a brief overlap of two small embed helpers (App's + server's);
  accepted. The server's short idle reclaim (60–90s) keeps the window small.
- Client-side cancellation releases the in-flight embed batch but never kills
  the warm helper mid-batch (same discipline as the App's query path).

## Setup UX (App Settings)

New "MCP Access" section:

- Master enable toggle; per-matter checkbox list (display names + session
  counts); "include unassigned sessions" toggle; link to open the audit
  folder.
- Read-only config snippet with Copy button:

  ```json
  { "mcpServers": { "localscribe": {
      "command": "<install>\\LocalScribe.Mcp.exe",
      "args": ["--storage-root", "<actual root>"] } } }
  ```

  plus one-line instructions for Claude Desktop (paste into
  `claude_desktop_config.json`) and Claude Code
  (`claude mcp add localscribe -- <exe> --storage-root <root>`).
- First enable shows a plain-Window confirm dialog stating what exposure
  means — one deliberate consent moment (plain Window per the FluentWindow
  startup-rendering gotcha).
- LocalScribe never writes another application's config file.

## Error handling

- Storage root missing/unreadable ⇒ server starts and reports the problem in
  every tool response (clients surface tool errors better than spawn
  failures).
- Corrupt sidecar or cache entry ⇒ skip that session, count it in the
  coverage/honesty block, never crash.
- Embed helper spawn failure ⇒ semantic tool errors; everything else
  unaffected.

## Testing

- **Core.Tests** (temp storage roots, fake embed clients — no real helper):
  - `ConsentFilter`: fail-closed on absent/disabled consent, revocation via
    mtime re-read, unassigned toggle, no-existence-leak on denied reads,
    pre-ranking exclusion from both engines.
  - Audit: line shape, append under concurrent read, denied-call logging.
  - `read_transcript`: span selection, char cap, cursor continuation,
    marker rows inline.
  - Freshness: re-derive on changed session folder, `index_as_of` stamping,
    read-only cache seed (cache file byte-identical after server run).
  - Semantic coverage honesty with missing/stale sidecars; helper-missing
    error path.
- **Wire-level test:** drive the actual server over an in-memory/stdio pipe
  pair through initialize → list-tools → each tool; assert stdout purity
  (no non-protocol bytes).
- **App.Tests:** Settings VM writes correct `consent.json`; snippet renders
  real paths; first-enable confirm flow.
- **Manual smoke runbook (user):** Claude Desktop registration, end-to-end
  "find where we discussed X and quote it," revocation mid-conversation,
  live-recording concurrency, App-closed standalone operation.

## Out of scope (v1)

- MCP resources / prompts (tools only).
- Per-session marker *detail* tool (marker rows already appear inline in
  reads; a dedicated tool can come later).
- Audio exposure in any form.
- In-process embedding in the server (later optimization; v1 reuses
  `AssistantEmbeddingClient` + helper).
- Auto-registration into MCP clients' config files.
- Any index writing from the server (lexical cache self-heal stays App-only).
