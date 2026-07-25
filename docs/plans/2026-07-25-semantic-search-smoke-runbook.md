# Semantic search - real-model smoke runbook (user-run)

Prereqs: `tools/fetch-models.ps1 -Embedding`, assistant helper published (or dev tools/assistant), app built.

S1. Helper op: `pwsh tools/smoke-embed.ps1` -> PASS line, dim 256, unit-normalized (already verified once during development; re-run to confirm your environment).
S2. First backfill: launch app with existing corpus; Search page -> type a query ->
    Related section shows "searched N of M sessions - indexing continues"; N reaches M
    within minutes (small corpus) with the app idle. Task Manager: ONE
    LocalScribe.Assistant.exe, working set well under 1 GB, CPU quiet after backfill.
S3. Meaning, not words: search "settlement figure" against a session that discusses money
    amounts WITHOUT those words -> session appears under Related discussion; click a row ->
    read view opens scrolled to the right passage.
S4. Multilingual: query in English against a non-English session (if available) -> hit lands.
S5. Facets: set a Matter/date/app facet -> Related section respects it identically to exact.
S6. Recording pause + memory: start a recording mid-backfill -> within seconds
    LocalScribe.Assistant.exe (embed instance) EXITS in Task Manager; searching still fills
    Related (a fresh helper spawns, then dies after 5 idle minutes); stop recording ->
    backfill resumes, coverage note advances.
S7. Edit staleness: correct a line in a Related-hit passage -> within seconds the session
    re-embeds (coverage dips then recovers); the corrected wording is what the snippet shows.
S8. Deletability: close app, delete <root>\index\semantic\ entirely, relaunch -> full rebuild,
    no errors anywhere.
S9. Floor sanity: nonsense query ("purple quantum sandwich") -> Related section empty or
    hidden, never padded with junk. If real queries return junk or miss obvious passages,
    tune SemanticQueryEngine.MinScore (0.55) and re-run S3.
S10. Chat still works on LLamaSharp 0.27.0: run a real assistant summary or chat turn (the llama.cpp bump that enabled embeddings also rebuilt the chat backend - verify a normal chat answer renders).
S11. CUDA (GPU boxes): with an NVIDIA GPU present, run a summary with backend auto and confirm provenance reports cuda (0.27.0 CUDA execution is file-verified but not yet GPU-executed).
