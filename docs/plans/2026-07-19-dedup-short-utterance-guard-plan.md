# Dedup Short-Utterance Guard Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement design §2 (`fix/dedup-short-utterance-guard`) of `docs/plans/2026-07-18-steno-round-design.md`: close the audit-confirmed defect that whole-string similarity in `PhantomBleedDedup` has no length floor — a genuine brief reply ("Yes.", "OK") coextensive within `NearWindowMs` (750 ms) of a similar short line on the other leg is silently hidden when the ≥3 dB RMS gap holds (or, pass 1 only, on identical text via the 0.975 text-only bar when RMS is missing). `EchoTimeCoverageMin = 0.70` only protects fragments inside *longer* lines, not short-vs-short. Fix: `PhantomBleedOptions` gains `MinAutoSuppressChars = 12` and `MinAutoSuppressTokens = 3` (provisional until golden-corpus-validated), evaluated on the NORMALIZED text of the segment that WOULD BE SUPPRESSED, at the top of both `IsBleedOf` (pass 1) and `IsEchoOfLocal` (pass 2): below EITHER floor → never auto-suppressed, regardless of similarity/RMS/coverage. Accepted cost (design §2, recorded): a real short echo now renders twice — a visible duplicate is evidentiarily safer than a silent hide of possibly-genuine speech.
**Architecture:** A pure render-layer change confined to `src\LocalScribe.Core\Projection\` — two new public init-properties on the `PhantomBleedOptions` record and one private guard helper in `PhantomBleedDedup`, wired as the first check of each pass's predicate. The guard reuses `TextDistance.Normalize` (the same normalization every similarity metric in this file already runs on) and mirrors the existing containment floor's strict-less-than semantics (`TextDistance.ContainmentSimilarity` line ~51: `shorter.Length < 12 || sTok.Length < 3`). No App changes, no persistence changes, no new types, no threshold-value changes: `Filter` still hides via `continue` (render-only; JSONL keeps both copies), and the `Corrected`/`IsSplitChild` exemptions are untouched.
**Tech Stack:** C#/.NET 10, xUnit (Core test suite only — no App surface).

## Global Constraints

- **Target branch:** `fix/dedup-short-utterance-guard`, created off master AFTER the in-flight `feat/ux-round-2026-07-18` merges. The design spec `docs/plans/2026-07-18-steno-round-design.md` reaches master via that merge; only THIS plan (`docs/plans/2026-07-19-dedup-short-utterance-guard-plan.md`) needs adding to the branch (a `docs(plans): implementation plan for the dedup short-utterance guard` commit, with the standard trailer) if it is not already on master when the branch is cut.
- **Merge order for the Steno round:** this branch merges FIRST of the seven (smallest, a real defect fix — design §1).
- **Line anchors are grounded @ 7605606** (verified: `PhantomBleedDedup.cs`, `TextDistance.cs`, and `PhantomBleedDedupTests.cs` are byte-identical between 7605606 and the current working tree). Because the branch is cut after the ux-round merge, ALWAYS re-verify each anchor by its quoted context before editing — if drifted, locate by the quoted code, not the number.
- **Evidentiary rules that bind this branch (design §1, locked):** the machine transcript is append-only evidence — this change rewrites/deletes NOTHING and strictly REDUCES render-time hiding. Dedup stays render-only (`Filter` returns a filtered view; transcript JSONL keeps both copies). Never propose hide/delete/redact of content.
- **The four golden thresholds and `EchoTimeCoverageMin` are UNTOUCHED** (`NearWindowMs=750`, `MinSimilarity=0.85`, `MinRmsGapDb=3.0`, `TextOnlyMinSimilarity=0.975`, `EchoTimeCoverageMin=0.70`). This plan adds a NEW mechanism (the floor) only. Tune threshold values ONLY against the golden corpus — never ad hoc.
- **At-floor semantics (decided, encoded in tests):** at-or-above BOTH floors = still eligible for suppression; below EITHER floor = exempt. Strict less-than (`< 12 || < 3`), exactly matching the existing containment floor in `TextDistance.ContainmentSimilarity`. A normalized text of exactly 12 chars and 3 tokens ("call me back") remains suppressible.
- **Floor values are PROVISIONAL** (`MinAutoSuppressChars = 12`, `MinAutoSuppressTokens = 3`) until validated against the golden corpus — Task 3 encodes that gate. The golden corpus is REAL privileged call audio living at `<ModelsRoot>\golden\` (never committed — see `docs/plans/2026-07-02-stage-2b-golden-corpus.md`), so the validation run is a USER-RUN gate on the box that has it, mirroring how prior rounds handled hardware/corpus gates (recorded as an OPEN user item, run result recorded in the PR).
- 0-warning build gate must hold.
- Tests: xUnit. Filtered run: `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~<Name>" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\dedup-short-guard\`
- **Full App-suite gate runs (XamlHygieneTests):** `RepoPaths.SolutionRoot()` walks UP from the test assembly directory to find `.git`, so an App-suite run that includes `XamlHygieneTests` MUST NOT use the Temp isolated BaseOutputPath (it sits outside the repo — 5 false failures). Run full App-suite gates with the default repo-internal output path; keep the isolated path for filtered runs. If the default path hits MSB3027 (app running, locked bin), report and wait — never kill processes.
- IMPORTANT: the LocalScribe app may be running and LOCK bin DLLs (MSB3027 copy error — NOT a compile error). Always use the isolated BaseOutputPath above (every command below already appends it); NEVER kill the user's running app or any npm/tauri processes — always report instead.
- There is NO `InternalsVisibleTo` anywhere in this repo — any member tests touch directly must be `public`. Here the two new option properties are public init-properties on the public `PhantomBleedOptions` record; the guard helper stays `private` because every new test exercises it through the public `Filter` (the only behavior that matters).
- Never use Unicode emojis in test code or scripts (project rule). All new test strings in this plan are plain ASCII.
- Core suite has 2 known pre-existing fixture failures (`Der_within_baseline_plus_epsilon`, `Golden_pair_wer_stays_at_baseline` — env-absence of the privileged golden corpus/model files, NOT regressions). The bar is: no NEW failures.
- Commit style: `fix(core)` / `test(core)` / `docs(plans)`; every commit message ends with exactly this trailer line:
```
Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
```

---

### Task 1: Core — floor options + short-utterance guard in pass 1 (`IsBleedOf`)
**Files:**
- Modify `src\LocalScribe.Core\Projection\PhantomBleedDedup.cs`:
  - `PhantomBleedOptions` record (lines 6–23): insert the two new properties after line 22 (`public double EchoTimeCoverageMin { get; init; } = 0.70;`), before the record's closing brace (line 23).
  - `PhantomBleedDedup` class: insert the private guard helper immediately after `GuardedSimilarity` (its closing brace is line 111, directly before `private bool IsBleedOf(...)` at line 113); wire the guard as the first statement of `IsBleedOf` (lines 113–126).
- Test `tests\LocalScribe.Core.Tests\PhantomBleedDedupTests.cs`: append six `[Fact]`s inside `PhantomBleedDedupTests` before the class's closing brace (line 219, immediately after `Corrected_but_organically_kept_local_still_anchors_the_echo_hide`, which ends at line 218), reusing the file's existing `Seg(TranscriptSource src, int seq, long startMs, long endMs, string text, double? rmsDb)` helper (lines 6–9).

**Interfaces:**
- Produces:
  - `public int PhantomBleedOptions.MinAutoSuppressChars { get; init; } = 12;` — NEW mechanism constant (design 2026-07-18 §2), provisional until golden-corpus-validated (Task 3 gate). Measured on `TextDistance.Normalize` output. NOT one of the four golden thresholds.
  - `public int PhantomBleedOptions.MinAutoSuppressTokens { get; init; } = 3;` — token half of the same floor.
  - `private bool PhantomBleedDedup.IsBelowAutoSuppressFloor(ProjectedSegment hidden)` — true when the would-be-suppressed segment's normalized text is below EITHER floor (strict less-than, the containment floor's exact shape); consumed by pass 1 here and pass 2 in Task 2.
- Consumes: `public static string TextDistance.Normalize(string text)` (`src\LocalScribe.Core\Projection\TextDistance.cs` lines 64–79 — lowercase, punctuation runs collapsed to single interior spaces, no leading/trailing space, so `Split(' ')` over its output is the token count); existing `_o` (`PhantomBleedOptions`); `ProjectedSegment.Text`. Pass-1 evidence path (`GuardedSimilarity`, RMS gap, text-only bar) is otherwise UNCHANGED.

Steps:
- [ ] **Write the failing tests.** In `tests\LocalScribe.Core.Tests\PhantomBleedDedupTests.cs`, append inside the class, immediately after the closing brace of `Corrected_but_organically_kept_local_still_anchors_the_echo_hide` (line 218: `    }`) and before the class's closing brace (line 219):
```csharp

    [Fact]
    public void Floor_short_genuine_reply_survives_pass1_despite_qualifying_rms_gap()
    {
        // Steno-round design 2026-07-18 section 2: whole-string similarity has no length floor, so
        // a genuine brief reply coextensive with a similar short remote line was hidden whenever
        // the 3 dB gap held (identical text, sim 1.0, local 13.5 dB quieter here). Normalized
        // "yes exactly" = 11 chars / 2 tokens - below BOTH floors (12 chars / 3 tokens) - so
        // pass 1 must never auto-suppress it, regardless of similarity or RMS evidence.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 2200, "Yes, exactly.", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 1050, 2250, "Yes, exactly.", -31.5);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Floor_short_genuine_reply_survives_pass1_missing_rms_identical_text()
    {
        // Missing-RMS path: identical text (sim 1.0) clears the 0.975 text-only bar, so before
        // the floor this pair lost the local copy with NO energy evidence at all. Below either
        // floor -> never auto-suppressed on the text-only path either.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 2200, "Yes, exactly.", null);
        var local = Seg(TranscriptSource.Local, 1, 1050, 2250, "Yes, exactly.", null);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Floor_boundary_at_exactly_12_chars_3_tokens_pass1_still_suppresses()
    {
        // Boundary semantics mirror the containment floor's strict less-than (< 12 || < 3):
        // normalized "call me back" is EXACTLY 12 chars and 3 tokens - at-or-above both floors -
        // so it stays ELIGIBLE and the quieter coextensive copy is still hidden as a true bleed.
        // Pins today's behavior; guards the floor against drifting to at-or-below (<=) semantics.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 2500, "Call me back.", -18.0);
        var bleed = Seg(TranscriptSource.Local, 1, 1050, 2550, "Call me back.", -31.5);
        var kept = new PhantomBleedDedup().Filter(new[] { remote, bleed });
        var only = Assert.Single(kept);
        Assert.Equal(TranscriptSource.Remote, only.Source);
    }

    [Fact]
    public void Floor_boundary_11_chars_3_tokens_is_exempt_in_pass1()
    {
        // Normalized "yes ok sure" = 11 chars / 3 tokens: the token count meets its floor but the
        // char count sits ONE below its own - below EITHER floor exempts, so both copies stay.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 2200, "Yes, OK, sure.", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 1050, 2250, "Yes, OK, sure.", -31.5);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Floor_boundary_18_chars_2_tokens_is_exempt_in_pass1()
    {
        // Normalized "absolutely correct" = 18 chars / 2 tokens: chars clear their floor but the
        // token count sits one below its own - below EITHER floor exempts, so both copies stay.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 2600, "Absolutely correct.", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 1050, 2650, "Absolutely correct.", -31.5);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }

    [Fact]
    public void Floor_measures_normalized_text_not_raw()
    {
        // Raw "No, no, no!!!" is 13 chars / 3 whitespace-separated words - past both floors if
        // measured raw - but normalizes to "no no no" (8 chars / 3 tokens). The guard must
        // measure the NORMALIZED text (design 2026-07-18 section 2), so the char floor exempts it.
        var remote = Seg(TranscriptSource.Remote, 0, 1000, 2200, "No, no, no!!!", -18.0);
        var local = Seg(TranscriptSource.Local, 1, 1050, 2250, "No, no, no!!!", -31.5);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { remote, local }).Count);
    }
```
- [ ] **Run it and see it FAIL.** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~Floor_" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\dedup-short-guard\` — expected: 6 run, **5 failed** with `Assert.Equal() Failure` / `Expected: 2` / `Actual:   1` (today pass 1 hides each short local), **1 passed** (`Floor_boundary_at_exactly_12_chars_3_tokens_pass1_still_suppresses` — it pins today's suppression of an at-floor pair, unchanged by this fix). No compile error is expected: the tests exercise only the public `Filter`.
- [ ] **Add the two option properties.** In `src\LocalScribe.Core\Projection\PhantomBleedDedup.cs`, the `PhantomBleedOptions` record currently ends (lines 22–23):
```csharp
    public double EchoTimeCoverageMin { get; init; } = 0.70;
}
```
Replace with:
```csharp
    public double EchoTimeCoverageMin { get; init; } = 0.70;

    /// <summary>NEW mechanism constant (Steno-round design 2026-07-18 section 2) - not one of the
    /// four original golden-corpus-gated thresholds above, and EchoTimeCoverageMin is likewise
    /// untouched: the segment that WOULD be auto-suppressed (the hidden side of a candidate pair,
    /// in EITHER pass) must have at least this many NORMALIZED characters (TextDistance.Normalize)
    /// AND at least MinAutoSuppressTokens normalized tokens, or it is never auto-suppressed -
    /// regardless of similarity, RMS gap, or time coverage. Rationale (audit-confirmed defect):
    /// whole-string similarity had no length floor, so a genuine brief reply ("Yes.", "OK")
    /// coextensive with a similar short line on the other leg was silently hidden; the containment
    /// path already floors at 12 chars / 3 tokens (TextDistance.ContainmentSimilarity) and these
    /// values mirror it, with the same strict-less-than shape: at-or-above BOTH floors = still
    /// eligible, below EITHER = exempt. Accepted cost (recorded in the design): a real short echo
    /// now renders twice - a visible duplicate is evidentiarily safer than a silent hide of
    /// possibly-genuine speech. Values are PROVISIONAL until validated against the golden corpus
    /// (Stage 2b) - tune ONLY there, never ad hoc.</summary>
    public int MinAutoSuppressChars { get; init; } = 12;

    /// <summary>Token half of the short-utterance floor - see
    /// <see cref="MinAutoSuppressChars"/>.</summary>
    public int MinAutoSuppressTokens { get; init; } = 3;
}
```
- [ ] **Add the guard helper.** In the same file, `GuardedSimilarity` currently ends (lines 108–111):
```csharp
        if (directionOk && EchoTimeCoverage(hidden, keeper) >= _o.EchoTimeCoverageMin)
            similarity = Math.Max(similarity, TextDistance.ContainmentSimilarity(hidden.Text, keeper.Text));
        return similarity;
    }
```
Immediately after that closing brace (before `private bool IsBleedOf(...)` at line 113) insert:
```csharp

    /// <summary>Short-utterance guard (Steno-round design 2026-07-18 section 2), evaluated on the
    /// NORMALIZED text of the segment that would be suppressed: below EITHER floor the segment is
    /// never auto-suppressed, regardless of similarity, RMS gap, or time coverage. Strict
    /// less-than, mirroring the containment floor in TextDistance.ContainmentSimilarity
    /// (length &lt; 12 || tokens &lt; 3): a text at exactly 12 normalized chars and 3 tokens
    /// remains eligible. Closes the short-vs-short false positive the coverage guard cannot reach
    /// (two brief coextensive lines cover each other fully); the accepted cost is that a real
    /// short echo renders twice - a visible duplicate is evidentiarily safer than a silent hide
    /// of possibly-genuine speech. Normalize emits single interior spaces and no edge spaces, so
    /// the space-split below is the exact token count the containment floor counts.</summary>
    private bool IsBelowAutoSuppressFloor(ProjectedSegment hidden)
    {
        string norm = TextDistance.Normalize(hidden.Text);
        return norm.Length < _o.MinAutoSuppressChars
            || norm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < _o.MinAutoSuppressTokens;
    }
```
- [ ] **Wire the guard into pass 1.** `IsBleedOf` currently opens (lines 113–117):
```csharp
    private bool IsBleedOf(ProjectedSegment local, ProjectedSegment remote)
    {
        bool near = local.StartMs < remote.EndMs + _o.NearWindowMs
                 && remote.StartMs - _o.NearWindowMs < local.EndMs;
        if (!near) return false;
```
Replace with:
```csharp
    private bool IsBleedOf(ProjectedSegment local, ProjectedSegment remote)
    {
        // Short-utterance guard (design 2026-07-18 section 2), FIRST check per the design: the
        // would-be-suppressed side here is the LOCAL copy. Below either floor, no combination of
        // similarity/RMS/coverage evidence may hide it.
        if (IsBelowAutoSuppressFloor(local)) return false;

        bool near = local.StartMs < remote.EndMs + _o.NearWindowMs
                 && remote.StartMs - _o.NearWindowMs < local.EndMs;
        if (!near) return false;
```
- [ ] **Run tests and see PASS.** Same filter command as above — expected: 6 passed. (Note: pass 2 is still unguarded after this task — that is Task 2's failing test.)
- [ ] **Prove no regression in the class.** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~PhantomBleedDedupTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\dedup-short-guard\` — expected: 27 passed (the 21 pre-existing cases — 18 facts plus the 3-case `Normalized_similarity` theory — and the 6 new). Every pre-existing suppressed segment ("I pushed the auth changes last night", the testing-sound echo, etc.) normalizes well above 12 chars / 3 tokens, so nothing flips.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Projection/PhantomBleedDedup.cs tests/LocalScribe.Core.Tests/PhantomBleedDedupTests.cs
git commit -m "fix(core): short-utterance floor exempts brief replies from pass-1 phantom-bleed suppression

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Core — short-utterance guard in pass 2 (`IsEchoOfLocal`)
**Files:**
- Modify `src\LocalScribe.Core\Projection\PhantomBleedDedup.cs`: wire the Task-1 guard as the first statement of `IsEchoOfLocal` (lines 128–136 at the 7605606 anchor; after Task 1 the method sits ~30 lines lower — locate it by the quoted code below).
- Test `tests\LocalScribe.Core.Tests\PhantomBleedDedupTests.cs`: append three `[Fact]`s inside the class, immediately after Task 1's `Floor_measures_normalized_text_not_raw` and before the class's closing brace.

**Interfaces:**
- Consumes: `private bool PhantomBleedDedup.IsBelowAutoSuppressFloor(ProjectedSegment hidden)` (Task 1); `PhantomBleedOptions.MinAutoSuppressChars` / `MinAutoSuppressTokens` (Task 1). Pass-2 evidence path (RMS required on BOTH sides, `MinSimilarity`, symmetric `MinRmsGapDb`, no text-only fallback) is otherwise UNCHANGED, as are the pass-2 anchor rules in `Filter` (only bleed-unmatched locals anchor a remote-hide — note: a short local exempted by Task 1's floor is now always an anchor, which is exactly why pass 2 needs its own floor on the remote side).
- Produces: `IsEchoOfLocal` guarded — the would-be-suppressed side in pass 2 is the REMOTE copy; below either floor it is never hidden.

Steps:
- [ ] **Write the failing tests.** In `tests\LocalScribe.Core.Tests\PhantomBleedDedupTests.cs`, append inside the class, immediately after the closing brace of `Floor_measures_normalized_text_not_raw` (Task 1) and before the class's closing brace:
```csharp

    [Fact]
    public void Floor_short_genuine_remote_reply_survives_pass2_despite_qualifying_rms_gap()
    {
        // Pass 2 (the echo-of-own-voice direction) had the same missing floor: a genuine short
        // remote reply repeating the user's short line, 8 dB apart and coextensive, was hidden.
        // Normalized "yes exactly" = 11 chars / 2 tokens - below both floors - so the remote must
        // never be auto-suppressed. (The louder local is not a pass-1 bleed - the RMS direction
        // is wrong - so it anchors pass 2; only the floor stands between the remote and a hide.)
        var local = Seg(TranscriptSource.Local, 0, 1000, 2200, "Yes, exactly.", -20.0);
        var remote = Seg(TranscriptSource.Remote, 1, 1050, 2250, "Yes, exactly.", -28.0);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { local, remote }).Count);
    }

    [Fact]
    public void Floor_short_pair_missing_rms_survives_pass2_identical_text()
    {
        // With no RMS on either side pass 2 already refuses (no text-only fallback in the remote
        // direction - existing locked behavior). Pinned here for the short-pair shape so BOTH
        // defenses - the RMS requirement and the new floor - stand between an identical short
        // pair and a remote-side hide. (Pass 1 keeps the local via Task 1's floor.)
        var local = Seg(TranscriptSource.Local, 0, 1000, 2200, "Yes, exactly.", null);
        var remote = Seg(TranscriptSource.Remote, 1, 1050, 2250, "Yes, exactly.", null);
        Assert.Equal(2, new PhantomBleedDedup().Filter(new[] { local, remote }).Count);
    }

    [Fact]
    public void Floor_boundary_at_exactly_12_chars_3_tokens_pass2_still_suppresses()
    {
        // Same boundary as pass 1: normalized "call me back" is exactly 12 chars / 3 tokens, so
        // the remote copy stays ELIGIBLE and the coextensive quieter remote echo is still hidden
        // on RMS evidence. Pins strict less-than semantics on the pass-2 side too.
        var local = Seg(TranscriptSource.Local, 0, 1000, 2500, "Call me back.", -20.0);
        var echo = Seg(TranscriptSource.Remote, 1, 1050, 2550, "Call me back.", -28.0);
        var kept = new PhantomBleedDedup().Filter(new[] { local, echo });
        var only = Assert.Single(kept);
        Assert.Equal(TranscriptSource.Local, only.Source);
    }
```
- [ ] **Run it and see it FAIL.** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~Floor_" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\dedup-short-guard\` — expected: 9 run, **1 failed** (`Floor_short_genuine_remote_reply_survives_pass2_despite_qualifying_rms_gap` with `Assert.Equal() Failure` / `Expected: 2` / `Actual:   1` — pass 2 is still unguarded), **8 passed** (Task 1's six, plus the two pass-2 pinning tests: the missing-RMS one holds via pass 2's existing RMS requirement, the at-floor one via today's suppression — both are documented pins, not new behavior).
- [ ] **Wire the guard into pass 2.** In `src\LocalScribe.Core\Projection\PhantomBleedDedup.cs`, `IsEchoOfLocal` currently opens:
```csharp
    private bool IsEchoOfLocal(ProjectedSegment remote, ProjectedSegment local)
    {
        bool near = remote.StartMs < local.EndMs + _o.NearWindowMs
                 && local.StartMs - _o.NearWindowMs < remote.EndMs;
        if (!near) return false;
```
Replace with:
```csharp
    private bool IsEchoOfLocal(ProjectedSegment remote, ProjectedSegment local)
    {
        // Short-utterance guard (design 2026-07-18 section 2), FIRST check per the design: the
        // would-be-suppressed side here is the REMOTE copy. Below either floor, no combination of
        // similarity/RMS/coverage evidence may hide it. Needed independently of pass 1's guard:
        // a floor-exempt short local is now always a pass-2 anchor, so without this line an
        // identical short pair would keep the local only to lose the remote instead.
        if (IsBelowAutoSuppressFloor(remote)) return false;

        bool near = remote.StartMs < local.EndMs + _o.NearWindowMs
                 && local.StartMs - _o.NearWindowMs < remote.EndMs;
        if (!near) return false;
```
- [ ] **Run tests and see PASS.** Same `~Floor_` filter command — expected: 9 passed.
- [ ] **Prove no regression in the class.** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~PhantomBleedDedupTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\dedup-short-guard\` — expected: 30 passed (21 pre-existing cases + 9 new). In particular the pass-2 pins stay green: `Remote_echo_of_the_users_own_speech_is_hidden` (its hidden remote normalizes far above the floor), `Remote_direction_requires_rms_evidence_no_text_only_fallback`, and `A_pair_can_never_vanish_entirely`.
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Projection/PhantomBleedDedup.cs tests/LocalScribe.Core.Tests/PhantomBleedDedupTests.cs
git commit -m "fix(core): apply the short-utterance floor to pass-2 echo suppression

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Regression gate + golden-corpus validation of the provisional floors
**Files:**
- No source edits. Runs the branch gate and encodes the golden-corpus check that turns the provisional floor values into locked ones.
- Reference: `tests\LocalScribe.Core.Tests\GoldenCorpusFixtureTests.cs` (the repo's only golden-corpus harness — `[Trait("Category", "Fixture")]`, real Silero VAD + Whisper `base.en` over the privileged pair at `<ModelsRoot>\golden\`, WER held at `baseline.json` + 0.05 epsilon plus the 30 s silence zero-segment hard bar) and `docs\plans\2026-07-02-stage-2b-golden-corpus.md` (corpus layout and setup).

**Interfaces:**
- Consumes: the full Core and App suites; `GoldenCorpusFixtureTests` (env-gated: requires `<ModelsRoot>\golden\local.wav`, `remote.wav`, `reference-local.txt`, `reference-remote.txt` plus real weights from `fetch-models.ps1` — absent on a clean checkout, which is exactly why `Golden_pair_wer_stays_at_baseline` is one of the 2 known fixture failures).
- Produces: the recorded gate results (agent-run) and the OPEN user-run golden-corpus item for the PR (user-run).

Steps:
- [ ] **Full Core suite.** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\dedup-short-guard\` — expected: all green except at most the 2 known pre-existing fixture failures (`Der_within_baseline_plus_epsilon`, `Golden_pair_wer_stays_at_baseline`); zero NEW failures. If MSB3027 appears, the running app is locking bin DLLs — the isolated BaseOutputPath in this command avoids it; report, never kill processes.
- [ ] **Full App suite (untouched surface, prove it).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\dedup-short-guard\` — expected: fully green (this branch touches no App code; the shared projection pipeline change is exercised through Core).
- [ ] **0-warning build.** `dotnet build LocalScribe.slnx --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\dedup-short-guard\` — expected: Build succeeded, 0 Warning(s), 0 Error(s).
- [ ] **Golden-corpus validation of the provisional floors — USER-RUN GATE (record in the PR).** The golden corpus is REAL privileged call audio that is never committed (`docs\plans\2026-07-02-stage-2b-golden-corpus.md`), so this step runs only on the user's box where `<ModelsRoot>\golden\` exists — mirroring how prior rounds recorded corpus/hardware gates as OPEN user items. The exact invocation:
```
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~GoldenCorpusFixtureTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\dedup-short-guard\
```
  Expected on that box: 2 passed (`Golden_pair_wer_stays_at_baseline` — WER at baseline + 0.05; `Silence_produces_zero_segments_hard_bar`). This proves the pipeline regression bar; because the fixture measures the raw transcript (pre-projection), it cannot itself exercise the render dedup, so the user additionally opens the golden pair's session in the Read view and confirms the floor's observed effect matches the design's accepted cost: any newly VISIBLE lines are short duplicates (below 12 normalized chars or 3 tokens) that the old dedup hid — never a lost line, never a long-line duplicate flood. Only after this run is recorded in the PR do `MinAutoSuppressChars = 12` / `MinAutoSuppressTokens = 3` graduate from PROVISIONAL to locked (design §2); if the corpus shows the floors are wrong, retune them against the corpus ONLY (never ad hoc) in a follow-up commit before merge.
- [ ] **No commit in this task** — it is a verification gate; the plan file itself lands via the `docs(plans)` commit noted in Global Constraints, and any floor retune the golden corpus forces would be its own `fix(core)` commit with the standard trailer.
