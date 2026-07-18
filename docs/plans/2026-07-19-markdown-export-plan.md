# Markdown Export Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement design §3 (`feat/markdown-export`) of `docs/plans/2026-07-18-steno-round-design.md`: a full-document `.md` transcript export at DocxRenderer parity. (1) `MarkdownRenderer` gains a `Write(...)` method consuming the same `SessionProjectionLoader` triple (`Header` / `TextView` / `Rows`) plus options and footer text, emitting the metadata block, the non-optional machine-generated disclaimer, and the `Settings.DocxFooterText` footer, honoring `IncludeTimestamps`/`IncludeMarkers`; (2) `ExportFormat` gains `Markdown`, the export dialog gets a third radio, a `.md` `SavePathRequest` filter, and the SAME two option toggles docx shows; (3) `MaintenanceService.ExportMarkdownAsync` mirrors `ExportDocxAsync` exactly (session gate, output-file-only cleanup, shared projection load, versioned footer note). The existing save-time `MarkdownRenderer.Render` → `transcript.md` path is untouched.
**Architecture:** Core gains ONE pure function: `MarkdownRenderer.Write(header, meta, rows, timestampsMode, footerText, options) : string` — a string-returning sibling of `DocxRenderer.Write`, copying its metadata/disclaimer/footer CONTENT rules exactly (title from `SessionTextView.Title`, `(none)` placeholders, Description-only-when-present, `DocxRenderer.Disclaimer` verbatim) while the turn/marker shapes reuse the established markdown dialect of the save-time `Render` (`**[stamp] Name:** text` / `_[marker]_`). The two-bool `DocxOptions` record is deliberately REUSED (it is format-neutral: two toggles) so the dialog's checkboxes map to one options type for both textual formats. App side: `MaintenanceService.ExportMarkdownAsync` is a line-for-line mirror of `ExportDocxAsync` (`ExportWithOutputCleanupAsync` + `RunForSessionAsync` + `SessionProjectionLoader.LoadAsync` + the identical versioned-footer composition), writing UTF-8 (no BOM) bytes; `ExportDialogViewModel` branches three ways on `Format` and exposes `ShowOptionToggles` (true for Docx AND Markdown) that the dialog XAML binds instead of `IsDocx` (`IsDocx` itself is kept — nothing breaks); `ExportFormatToBool` gains a `Markdown` converter instance for the new radio.
**Tech Stack:** C#/.NET 10, WPF, CommunityToolkit.Mvvm, xUnit.

## Global Constraints

- **Target branch:** `feat/markdown-export`, created off master AFTER the in-flight `feat/ux-round-2026-07-18` merges. The design spec `docs/plans/2026-07-18-steno-round-design.md` lands on master with that merge; only THIS plan (`docs/plans/2026-07-19-markdown-export-plan.md`) needs adding to the branch (a `docs(plans): markdown-export implementation plan` commit) if it is not there yet.
- **Merge order for the round (design §1):** markdown-export merges SECOND of seven — after `fix/dedup-short-utterance-guard`, before `feat/deep-link`.
- **Line anchors are grounded @ 7605606** (`docs(design): Steno round ...` on the ux-round branch; every file this plan touches is verified byte-identical between 7605606 and the branch tip, so the anchors survive the merge). Re-verify each anchor's quoted context before editing — if drifted, locate by the quoted code, not the number.
- **Save-time path untouched (hard rule, design §3):** `MarkdownRenderer.Render(header, rows, timestampsMode)` and its caller `src\LocalScribe.Core\Storage\SessionWriter.cs:28` (→ `transcript.md`) must not change in any way — behavior is pinned by `RendererTests.Markdown_renders_header_turns_and_markers` (exact-string) and the `SessionProjectionLoader` byte-identity suite. Task 1 ADDS a method to the class; it edits nothing existing.
- **Verbatim display (locked evidentiary rule):** the renderer output is verbatim projected text — no filtering, no cleanup, and no markdown-escaping of row text (Task 1 pins this with a test). Rows arrive pre-resolved from `TranscriptProjection.Build`; nothing here re-runs vocabulary/edits/dedup.
- **Export never touches storageRoot:** on failure/cancel the mirrors delete the OUTPUT file only, and only if THIS export created it (the `markCreated` contract in `ExportWithOutputCleanupAsync`, `MaintenanceService.cs:773-785`).
- 0-warning build gate must hold.
- Tests: xUnit. Filtered run: `dotnet test "<testproj>" --filter "FullyQualifiedName~<Name>" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\markdown-export\`
- **Full App-suite gate runs (XamlHygieneTests):** `RepoPaths.SolutionRoot()` walks UP from the test assembly directory to find `.git`, so an App-suite run that includes `XamlHygieneTests` MUST NOT use the Temp isolated BaseOutputPath (it sits outside the repo — 5 false failures). Run full App-suite gates with the default repo-internal output path; keep the isolated path for filtered runs. If the default path hits MSB3027 (app running, locked bin), report and wait — never kill processes.
- IMPORTANT: the LocalScribe app may be running and LOCK bin DLLs (MSB3027 copy error — NOT a compile error). Always use the isolated BaseOutputPath above (every command below already appends it); NEVER kill the user's running app or any other process.
- Never use Unicode emojis in test code or scripts (project rule). All new UI strings are ASCII.
- Test projects: App = `tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj`; Core = `tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj`. There is NO `InternalsVisibleTo` anywhere in this repo — new members that tests call directly must be `public`.
- Commit style: `feat(core)`/`feat(app)`/`test(...)`/`docs(...)`; every commit message ends with exactly this trailer line:
```
Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
```
- Core suite has 2 known pre-existing fixture failures (unrelated); App suite must be fully green.

---

### Task 1: Core — `MarkdownRenderer.Write` (full-document export render at DocxRenderer parity)
**Files:**
- Modify `src\LocalScribe.Core\Projection\MarkdownRenderer.cs` (append the new method + one private helper inside the class, after the existing `Render` method's closing brace at line 32 and before the class's closing brace at line 33; nothing existing changes).
- Create test `tests\LocalScribe.Core.Tests\MarkdownRendererWriteTests.cs` (new file; mirrors `DocxRendererTests`' `Sample()` fixture so parity is asserted against the same data).

**Interfaces:**
- Produces: `public static string MarkdownRenderer.Write(TranscriptHeader header, SessionTextView meta, IReadOnlyList<DisplayRow> rows, string timestampsMode, string footerText, DocxOptions options)` — the export document as one string (`\n` line endings, matching `Render`). Content contract (encoded exactly in the tests):
  - `# {meta.Title}` heading, blank line, then a metadata bullet list copying `DocxRenderer.Write`'s block exactly: `- **App:** {header.App}`, `- **Date:** {header.StartedAtLocal:yyyy-MM-dd HH:mm}` (invariant), `- **Matter(s):**` (`(none)` when empty, else comma-joined), `- **Participants:**` (same rule), `- **Medium:** {meta.Medium}`, and `- **Description:**` only when non-empty. Bullets (not bare lines) so each renders on its own line in any markdown viewer without trailing-space hacks.
  - Blank line, then `_{DocxRenderer.Disclaimer}_` — the SAME non-optional disclaimer constant, italic.
  - Turns in the save-time dialect: blank line between rows; `**[{stamp}] {DisplayName}:** {text}` with timestamps on, `**{DisplayName}:** {text}` with them off (`TimestampFormat.Stamp`, same `timestampsMode` passthrough as docx); markers `_[{text}]_`, dropped entirely (no stray blank line) when `IncludeMarkers` is false.
  - Footer (markdown has no page footer): blank line, `---` horizontal rule, blank line, `{footerText}`; the whole block omitted when `footerText` is empty.
  - Row text and footer text are VERBATIM — never escaped or filtered (evidentiary rule; same posture as `Render`).
- Consumes: existing `TranscriptHeader`, `SessionTextView`, `DisplayRow`, `TimestampFormat.Stamp`, `DocxOptions`, `DocxRenderer.Disclaimer` (all in `LocalScribe.Core.Projection` — no new usings needed beyond the file's existing `System.Globalization`/`System.Text`).

Steps:
- [ ] **Write the failing tests.** Create `tests\LocalScribe.Core.Tests\MarkdownRendererWriteTests.cs` with exactly:
```csharp
using LocalScribe.Core.Projection;

public class MarkdownRendererWriteTests
{
    private static readonly DateTimeOffset Started =
        new(2026, 6, 30, 14, 32, 0, TimeSpan.Zero);   // fixed offset -> deterministic

    /// <summary>The same sample data DocxRendererTests renders - parity is asserted
    /// against identical input (design 2026-07-18 section 3).</summary>
    private static (TranscriptHeader H, SessionTextView V, DisplayRow[] R) Sample()
    {
        var h = new TranscriptHeader("Weekly Sync", "Teams", Started, 2220000, "small.en", "CUDA");
        var v = new SessionTextView("Weekly Sync", new[] { "Acme (2026-014)" },
            new[] { "Sam (Local)", "Bob (Remote)" }, Started, Started.AddMinutes(37), 2220000,
            "Teams", "", null);
        var r = new[]
        {
            new DisplayRow { StartMs = 1000, DisplayName = "Sam", Text = "Morning everyone." },
            new DisplayRow { IsMarker = true, StartMs = 30000, Text = "audio device changed" },
            new DisplayRow { StartMs = 38000, DisplayName = "Bob", Text = "Question on tokens." },
        };
        return (h, v, r);
    }

    [Fact]
    public void Writes_metadata_disclaimer_turns_and_footer()
    {
        var (h, v, r) = Sample();
        string md = MarkdownRenderer.Write(h, v, r, "relative", "PRIVILEGED & CONFIDENTIAL",
            new DocxOptions());

        string expected =
            "# Weekly Sync\n" +
            "\n" +
            "- **App:** Teams\n" +
            "- **Date:** 2026-06-30 14:32\n" +
            "- **Matter(s):** Acme (2026-014)\n" +
            "- **Participants:** Sam (Local), Bob (Remote)\n" +
            "- **Medium:** Teams\n" +
            "\n" +
            "_" + DocxRenderer.Disclaimer + "_\n" +
            "\n" +
            "**[00:01] Sam:** Morning everyone.\n" +
            "\n" +
            "_[audio device changed]_\n" +
            "\n" +
            "**[00:38] Bob:** Question on tokens.\n" +
            "\n" +
            "---\n" +
            "\n" +
            "PRIVILEGED & CONFIDENTIAL\n";
        Assert.Equal(expected, md);
    }

    [Fact]
    public void Toggles_off_omit_timestamps_and_markers()
    {
        var (h, v, r) = Sample();
        string md = MarkdownRenderer.Write(h, v, r, "relative", "F",
            new DocxOptions { IncludeTimestamps = false, IncludeMarkers = false });

        Assert.DoesNotContain("[00:01]", md);
        Assert.DoesNotContain("audio device changed", md);
        Assert.Contains("**Sam:** Morning everyone.\n", md);      // turn label present, no stamp
        Assert.Contains("**Bob:** Question on tokens.\n", md);
        Assert.DoesNotContain("\n\n\n", md);                      // dropped marker leaves no gap
    }

    [Fact]
    public void Empty_matters_participants_render_none_and_empty_footer_omits_the_rule()
    {
        var h = new TranscriptHeader("T", "Webex", Started, 60000, "base.en", "CPU");
        var v = new SessionTextView("T", Array.Empty<string>(), Array.Empty<string>(),
            Started, null, 60000, "Webex", "Initial interview.", null);
        string md = MarkdownRenderer.Write(h, v, Array.Empty<DisplayRow>(), "relative", "",
            new DocxOptions());

        Assert.Contains("- **Matter(s):** (none)\n", md);
        Assert.Contains("- **Participants:** (none)\n", md);
        Assert.Contains("- **Description:** Initial interview.\n", md);   // present only when set
        Assert.DoesNotContain("---", md);                         // empty footer -> no rule block
        Assert.EndsWith("_\n", md);                               // document ends at the disclaimer
    }

    [Fact]
    public void Row_text_is_verbatim_never_escaped_or_filtered()
    {
        // Evidentiary rule (design 2026-07-18 section 1): the renderer emits verbatim projected
        // text - even characters that happen to be markdown syntax are never escaped or dropped.
        var (h, v, _) = Sample();
        var rows = new[] { new DisplayRow { StartMs = 1000, DisplayName = "Sam",
            Text = "Use **bold** and _underscores_ verbatim." } };
        string md = MarkdownRenderer.Write(h, v, rows, "relative", "", new DocxOptions());
        Assert.Contains("**[00:01] Sam:** Use **bold** and _underscores_ verbatim.\n", md);
    }
}
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~MarkdownRendererWrite" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\markdown-export\` — expected: `error CS0117: 'MarkdownRenderer' does not contain a definition for 'Write'`.
- [ ] **Add the method.** In `src\LocalScribe.Core\Projection\MarkdownRenderer.cs` the class currently ends (lines 31–33):
```csharp
        return sb.ToString();
    }
}
```
Replace with:
```csharp
        return sb.ToString();
    }

    /// <summary>Full-document EXPORT render at DocxRenderer parity (design 2026-07-18 section 3):
    /// the SAME metadata block content rules, the SAME non-optional machine-generated disclaimer,
    /// and the footer text after a horizontal rule (markdown has no page footer; the block is
    /// omitted when the footer text is empty). Metadata renders as a bullet list so each line
    /// stands alone in any viewer without trailing-space hard breaks; turns and markers reuse the
    /// save-time Render dialect above, gated by the two DocxOptions toggles (the options record is
    /// format-neutral - two bools - and shared deliberately). Rows arrive pre-resolved from
    /// TranscriptProjection.Build and are emitted VERBATIM - never filtered, cleaned, or
    /// markdown-escaped (locked evidentiary rule). The save-time Render(...) -> transcript.md
    /// path above is a separate, untouched surface.</summary>
    public static string Write(TranscriptHeader header, SessionTextView meta,
        IReadOnlyList<DisplayRow> rows, string timestampsMode, string footerText, DocxOptions options)
    {
        var sb = new StringBuilder();
        sb.Append("# ").Append(meta.Title).Append('\n').Append('\n');
        AppendMeta(sb, "App", header.App);
        AppendMeta(sb, "Date",
            header.StartedAtLocal.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        AppendMeta(sb, "Matter(s)",
            meta.Matters.Count == 0 ? "(none)" : string.Join(", ", meta.Matters));
        AppendMeta(sb, "Participants",
            meta.Participants.Count == 0 ? "(none)" : string.Join(", ", meta.Participants));
        AppendMeta(sb, "Medium", meta.Medium);
        if (!string.IsNullOrEmpty(meta.Description)) AppendMeta(sb, "Description", meta.Description);
        sb.Append('\n').Append('_').Append(DocxRenderer.Disclaimer).Append('_').Append('\n');

        foreach (var row in rows)
        {
            if (row.IsMarker)
            {
                if (options.IncludeMarkers)
                    sb.Append('\n').Append("_[").Append(row.Text).Append("]_").Append('\n');
                continue;   // toggled-off marker: dropped entirely, no stray blank line
            }
            string label = options.IncludeTimestamps
                ? "[" + TimestampFormat.Stamp(row.StartMs, timestampsMode, header.StartedAtLocal)
                    + "] " + row.DisplayName
                : row.DisplayName ?? "";
            sb.Append('\n').Append("**").Append(label).Append(":** ").Append(row.Text).Append('\n');
        }

        if (!string.IsNullOrEmpty(footerText))
            sb.Append('\n').Append("---").Append('\n').Append('\n').Append(footerText).Append('\n');
        return sb.ToString();
    }

    private static void AppendMeta(StringBuilder sb, string label, string value)
        => sb.Append("- **").Append(label).Append(":** ").Append(value).Append('\n');
}
```
- [ ] **Run tests and see PASS.** Same filter as above — expected: 4 passed. Then prove the save-time path is untouched: `dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --filter "FullyQualifiedName~RendererTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\markdown-export\` (the exact-string `Markdown_renders_header_turns_and_markers` must stay green) and `--filter "FullyQualifiedName~SessionProjectionLoaderTests"` (byte-identity guard).
- [ ] **Commit.**
```
git add src/LocalScribe.Core/Projection/MarkdownRenderer.cs tests/LocalScribe.Core.Tests/MarkdownRendererWriteTests.cs
git commit -m "feat(core): MarkdownRenderer.Write - full-document markdown export at DocxRenderer parity

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: App — `MaintenanceService.ExportMarkdownAsync` (mirror of `ExportDocxAsync`)
**Files:**
- Modify `src\LocalScribe.App\Services\MaintenanceService.cs`: add `using System.Text;` after line 4 (`using System.IO.Compression;`); insert the new method immediately after `ExportDocxAsync`'s closing `}, ct));` (line 705) and before the `MatterExportResult` doc comment (line 707).
- Test `tests\LocalScribe.App.Tests\MaintenanceServiceTests.cs` (append two `[Fact]`s inside the class before its closing brace, reusing the in-file `MakeService`/`WriteFinalizedSessionAsync` helpers) and `tests\LocalScribe.App.Tests\MaintenanceServiceVersionsTests.cs` (append one `[Fact]` before the closing brace at line 208, reusing `SeedVersionedAsync`/`MakeService(Settings)`).

**Interfaces:**
- Produces: `public Task MaintenanceService.ExportMarkdownAsync(string sessionId, string destPath, DocxOptions options, CancellationToken ct)` — line-for-line mirror of `ExportDocxAsync` (lines 680–705): same `ExportWithOutputCleanupAsync` wrapper (output-file-only cleanup, pre-existing Save-As target preserved on early failure), same `RunForSessionAsync` per-session gate, same session-exists pre-check message ("The session no longer exists."), same `SessionProjectionLoader.LoadAsync` shared read, and the IDENTICAL versioned-footer composition (`TranscriptVersions.ShortId(loaded.VersionId)` + `loaded.Header.Model`; root version → the configured `Settings.DocxFooterText` alone). Renders BEFORE opening the output stream, then writes UTF-8 without BOM.
- Consumes: `MarkdownRenderer.Write` (Task 1), existing `paths`/`settings`/`time` primary-ctor fields, `ExportWithOutputCleanupAsync`, `RunForSessionAsync`, `TranscriptVersions.Root`/`ShortId`.

Steps:
- [ ] **Write the failing tests.** Append inside `MaintenanceServiceTests` (before the class's closing brace) in `tests\LocalScribe.App.Tests\MaintenanceServiceTests.cs`:
```csharp
    [Fact]
    public async Task ExportMarkdown_writes_the_transcript_with_footer_from_settings()
    {
        // Mirror of ExportDocx_writes_a_valid_docx_with_footer_from_settings (design 2026-07-18
        // section 3): shared projection load, disclaimer, and the settings footer after the rule.
        var (svc, paths) = MakeService();
        await WriteFinalizedSessionAsync(paths, "s1", "One");
        string dest = Path.Combine(_root, "out", "one.md");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await svc.ExportMarkdownAsync("s1", dest, new DocxOptions(), CancellationToken.None);

        string md = await File.ReadAllTextAsync(dest);
        Assert.StartsWith("# One\n", md);                                  // meta.Title heading
        Assert.Contains("_" + DocxRenderer.Disclaimer + "_", md);          // non-optional disclaimer
        Assert.EndsWith("---\n\nPRIVILEGED & CONFIDENTIAL\n", md);         // FakeSettingsService default
    }

    [Fact]
    public async Task ExportMarkdown_missing_session_throws_and_preserves_a_preexisting_output_file()
    {
        // Same cleanup contract as the zip/docx exports: an early failure (before the output
        // stream opens) must leave a pre-existing Save-As target intact; storageRoot untouched.
        var (svc, _) = MakeService();
        string dest = Path.Combine(_root, "out", "keep.md");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await File.WriteAllTextAsync(dest, "pre-existing user file");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ExportMarkdownAsync("ghost", dest, new DocxOptions(), CancellationToken.None));

        Assert.True(File.Exists(dest));
        Assert.Equal("pre-existing user file", await File.ReadAllTextAsync(dest));
    }
```
Then append inside `MaintenanceServiceVersionsTests` (before the closing brace at line 208, directly after `ExportDocx_footer_names_the_active_version_and_model`) in `tests\LocalScribe.App.Tests\MaintenanceServiceVersionsTests.cs`:
```csharp
    [Fact]
    public async Task ExportMarkdown_footer_names_the_active_version_and_model()
    {
        // The markdown mirror must compose the SAME versioned footer ExportDocxAsync does
        // (Transcript version <short> (<model>)), and read the ACTIVE version's transcript.
        string id = await SeedVersionedAsync();
        var svc = MakeService(new Settings { DocxFooterText = "PRIVILEGED" });
        string dest = Path.Combine(_root, "out.md");

        await svc.ExportMarkdownAsync(id, dest, new DocxOptions(), CancellationToken.None);
        string md = await File.ReadAllTextAsync(dest);
        Assert.Contains("V2 words.", md);                                  // active v2, not root
        Assert.EndsWith("---\n\nPRIVILEGED - Transcript version v2 (tiny.en)\n", md);

        // v1-active session: the footer is EXACTLY the configured text (no version note).
        await svc.SetActiveVersionAsync(id, "v1", CancellationToken.None);
        string dest1 = Path.Combine(_root, "out-v1.md");
        await svc.ExportMarkdownAsync(id, dest1, new DocxOptions(), CancellationToken.None);
        string md1 = await File.ReadAllTextAsync(dest1);
        Assert.Contains("Root words.", md1);
        Assert.EndsWith("---\n\nPRIVILEGED\n", md1);
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ExportMarkdown" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\markdown-export\` — expected: `error CS1061: 'MaintenanceService' does not contain a definition for 'ExportMarkdownAsync'`.
- [ ] **Add the using.** In `MaintenanceService.cs` the using block currently opens:
```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using LocalScribe.Core.Diarisation;
```
Insert `using System.Text;` between `using System.IO.Compression;` and `using LocalScribe.Core.Diarisation;`.
- [ ] **Add the method.** In `MaintenanceService.cs`, `ExportDocxAsync` currently ends and the next member begins (lines 702–709):
```csharp
            DocxRenderer.Write(fs, loaded.Header, loaded.TextView, loaded.Rows, settings.Current.Timestamps,
                footerText, pageSize, options);
            return true;
        }, ct));

    /// <summary>Result of a matter zip: how many sessions were archived vs skipped (live-recording /
    /// pending-recovery / deleted mid-export). Surfaced in the completion Info message.</summary>
    public sealed record MatterExportResult(int Added, int Skipped);
```
Replace with:
```csharp
            DocxRenderer.Write(fs, loaded.Header, loaded.TextView, loaded.Rows, settings.Current.Timestamps,
                footerText, pageSize, options);
            return true;
        }, ct));

    /// <summary>Export one session as a formatted .md transcript (design 2026-07-18 section 3).
    /// Line-for-line mirror of ExportDocxAsync: session gate, output-file-only cleanup on failure,
    /// shared SessionProjectionLoader read, and the IDENTICAL versioned footer composition. The
    /// document is rendered BEFORE the output stream opens, so a projection/render failure leaves
    /// a pre-existing Save-As target intact (markCreated contract). UTF-8 without BOM.</summary>
    public Task ExportMarkdownAsync(string sessionId, string destPath, DocxOptions options,
        CancellationToken ct)
        => ExportWithOutputCleanupAsync(destPath, markCreated => RunForSessionAsync(sessionId, async inner =>
        {
            if (!File.Exists(paths.SessionJson(sessionId)))
                throw new InvalidOperationException("The session no longer exists.");
            var loaded = await SessionProjectionLoader.LoadAsync(paths, settings.Current, time, sessionId, inner);
            // Versioned session (design 2026-07-13 section 3.3): the footer must state which
            // transcript version this document renders - the SAME composition as ExportDocxAsync,
            // so the two textual exports can never disagree about provenance.
            string versionNote =
                $"Transcript version {TranscriptVersions.ShortId(loaded.VersionId)} ({loaded.Header.Model})";
            string footerText = loaded.VersionId == TranscriptVersions.Root
                ? settings.Current.DocxFooterText
                : string.IsNullOrEmpty(settings.Current.DocxFooterText)
                    ? versionNote
                    : settings.Current.DocxFooterText + " - " + versionNote;
            string markdown = MarkdownRenderer.Write(loaded.Header, loaded.TextView, loaded.Rows,
                settings.Current.Timestamps, footerText, options);
            using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            markCreated();
            await fs.WriteAsync(Encoding.UTF8.GetBytes(markdown), inner);   // GetBytes emits no BOM
            return true;
        }, ct));

    /// <summary>Result of a matter zip: how many sessions were archived vs skipped (live-recording /
    /// pending-recovery / deleted mid-export). Surfaced in the completion Info message.</summary>
    public sealed record MatterExportResult(int Added, int Skipped);
```
- [ ] **Run tests and see PASS.** Same filter — expected: 3 passed. Then run both whole classes to prove no regression: `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~MaintenanceService" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\markdown-export\` (covers `MaintenanceServiceTests` + `MaintenanceServiceVersionsTests`).
- [ ] **Commit.**
```
git add src/LocalScribe.App/Services/MaintenanceService.cs tests/LocalScribe.App.Tests/MaintenanceServiceTests.cs tests/LocalScribe.App.Tests/MaintenanceServiceVersionsTests.cs
git commit -m "feat(app): MaintenanceService.ExportMarkdownAsync mirrors the docx export path

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: App — `ExportFormat.Markdown` + dialog VM branch + shared option toggles
**Files:**
- Modify `src\LocalScribe.App\ViewModels\ExportDialogViewModel.cs`: extend the enum (line 7); add `ShowOptionToggles` beside `IsDocx` (lines 36–37); rewrite `ExportAsync` (lines 43–66) to branch three ways.
- Modify `src\LocalScribe.App\ExportFormatToBool.cs`: add the `Markdown` converter instance after line 12.
- Test `tests\LocalScribe.App.Tests\ExportDialogViewModelTests.cs` (append two `[Fact]`s before the closing brace at line 89, reusing the in-file `MakeAsync`/`CollectingReporter` helpers).

**Interfaces:**
- Produces:
  - `public enum ExportFormat { Zip, Docx, Markdown }` (append-only; `Zip`/`Docx` values and their consumers unchanged — verified: the only `ExportFormat` consumers are this VM, `ExportFormatToBool`, and `ExportDialog.xaml`).
  - `public bool ExportDialogViewModel.ShowOptionToggles => Format is ExportFormat.Docx or ExportFormat.Markdown;` — the generalized gate the dialog's toggle panel binds in Task 4. `IsDocx` is KEPT and still raised (nothing breaks in the Task 3→4 window; the XAML still binds it until Task 4 rebinds).
  - Markdown branch in `ExportAsync`: `SavePathRequest(ExportFileNames.Sanitize(_sessionTitle) + ".md", "Markdown (*.md)|*.md")` → `MaintenanceService.ExportMarkdownAsync` with the same `DocxOptions` the docx branch builds from the two checkboxes.
  - `public static readonly ExportFormatToBool ExportFormatToBool.Markdown` converter instance for the new radio.
- Consumes: `MaintenanceService.ExportMarkdownAsync` (Task 2), existing `ExportFileNames.Sanitize`, `SavePathRequest`, `DocxOptions`.

Steps:
- [ ] **Write the failing tests.** Append inside `ExportDialogViewModelTests` (before the closing brace) in `tests\LocalScribe.App.Tests\ExportDialogViewModelTests.cs`:
```csharp
    [Fact]
    public async Task Markdown_export_sanitized_md_filename_filter_and_written_file()
    {
        // Design 2026-07-18 section 3: same Save-As shape as docx (sanitized title default name),
        // .md filter, and the file lands via MaintenanceService.ExportMarkdownAsync.
        var (svc, _, rep) = await MakeAsync();
        SavePathRequest? seen = null;
        string dest = Path.Combine(_root, "out.md");
        var vm = new ExportDialogViewModel("s1", "Doe: intake/2026", svc,
            req => { seen = req; return dest; }, _ => { }, rep, a => a())
        { Format = ExportFormat.Markdown };

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Equal("Doe_ intake_2026.md", seen!.DefaultFileName);        // ':' and '/' -> '_'
        Assert.Equal("Markdown (*.md)|*.md", seen.Filter);
        Assert.True(File.Exists(dest));
        Assert.StartsWith("# Doe intake\n", await File.ReadAllTextAsync(dest));   // meta title, not the raw arg
        Assert.Single(rep.Infos);
        Assert.Empty(rep.Errors);
    }

    [Fact]
    public async Task Option_toggles_show_for_docx_and_markdown_not_zip()
    {
        // The dialog's two checkboxes apply to BOTH textual formats (design 2026-07-18 section 3);
        // ShowOptionToggles generalizes the old IsDocx gate without removing it.
        var (svc, _, rep) = await MakeAsync();
        var vm = new ExportDialogViewModel("s1", "T", svc, _ => null, _ => { }, rep, a => a());
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(vm.ShowOptionToggles);                                // Zip default: hidden
        vm.Format = ExportFormat.Docx;
        Assert.True(vm.ShowOptionToggles);
        Assert.True(vm.IsDocx);
        vm.Format = ExportFormat.Markdown;
        Assert.True(vm.ShowOptionToggles);
        Assert.False(vm.IsDocx);                                           // IsDocx stays format-accurate
        vm.Format = ExportFormat.Zip;
        Assert.False(vm.ShowOptionToggles);
        Assert.Contains(nameof(ExportDialogViewModel.ShowOptionToggles), raised);
        Assert.Contains(nameof(ExportDialogViewModel.IsDocx), raised);
    }
```
- [ ] **Run it and see it FAIL (build error).** `dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --filter "FullyQualifiedName~ExportDialogViewModelTests" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\markdown-export\` — expected: `error CS0117: 'ExportFormat' does not contain a definition for 'Markdown'` (plus CS1061 on `ShowOptionToggles`).
- [ ] **Extend the enum.** In `ExportDialogViewModel.cs` replace line 7:
```csharp
public enum ExportFormat { Zip, Docx }
```
with:
```csharp
public enum ExportFormat { Zip, Docx, Markdown }
```
- [ ] **Add the generalized toggle gate.** Replace lines 36–37:
```csharp
    public bool IsDocx => Format == ExportFormat.Docx;
    partial void OnFormatChanged(ExportFormat value) => OnPropertyChanged(nameof(IsDocx));
```
with:
```csharp
    public bool IsDocx => Format == ExportFormat.Docx;
    /// <summary>The dialog's IncludeTimestamps/IncludeMarkers checkboxes apply to BOTH textual
    /// formats (design 2026-07-18 section 3) - this generalizes the old IsDocx visibility gate
    /// (kept above, unbroken) for the XAML toggle panel.</summary>
    public bool ShowOptionToggles => Format is ExportFormat.Docx or ExportFormat.Markdown;
    partial void OnFormatChanged(ExportFormat value)
    {
        OnPropertyChanged(nameof(IsDocx));
        OnPropertyChanged(nameof(ShowOptionToggles));
    }
```
- [ ] **Branch the export.** Replace `ExportAsync` (lines 43–66):
```csharp
    private async Task ExportAsync()
    {
        var request = Format == ExportFormat.Zip
            ? new SavePathRequest(_sessionId + ".zip", "Zip archive (*.zip)|*.zip")
            : new SavePathRequest(ExportFileNames.Sanitize(_sessionTitle) + ".docx", "Word document (*.docx)|*.docx");
        string? dest = _pickSavePath(request);
        if (string.IsNullOrWhiteSpace(dest)) return;                  // user cancelled Save-As

        IsBusy = true;
        try
        {
            if (Format == ExportFormat.Zip)
                await _maintenance.ExportSessionArchiveAsync(_sessionId, dest, CancellationToken.None);
            else
                await _maintenance.ExportDocxAsync(_sessionId, dest,
                    new DocxOptions { IncludeTimestamps = IncludeTimestamps, IncludeMarkers = IncludeMarkers },
                    CancellationToken.None);
            _errors.Info("Exported to " + dest);
            _revealFile(dest);
            _dispatch(() => Closed?.Invoke());
        }
        catch (Exception ex) { _errors.Report("Export", ex); }
        finally { IsBusy = false; }
    }
```
with:
```csharp
    private async Task ExportAsync()
    {
        var request = Format switch
        {
            ExportFormat.Zip => new SavePathRequest(_sessionId + ".zip", "Zip archive (*.zip)|*.zip"),
            ExportFormat.Markdown => new SavePathRequest(
                ExportFileNames.Sanitize(_sessionTitle) + ".md", "Markdown (*.md)|*.md"),
            _ => new SavePathRequest(
                ExportFileNames.Sanitize(_sessionTitle) + ".docx", "Word document (*.docx)|*.docx"),
        };
        string? dest = _pickSavePath(request);
        if (string.IsNullOrWhiteSpace(dest)) return;                  // user cancelled Save-As

        IsBusy = true;
        try
        {
            // One options build for both textual formats - the checkboxes mean the same thing.
            var options = new DocxOptions
            { IncludeTimestamps = IncludeTimestamps, IncludeMarkers = IncludeMarkers };
            switch (Format)
            {
                case ExportFormat.Zip:
                    await _maintenance.ExportSessionArchiveAsync(_sessionId, dest, CancellationToken.None);
                    break;
                case ExportFormat.Markdown:
                    await _maintenance.ExportMarkdownAsync(_sessionId, dest, options, CancellationToken.None);
                    break;
                default:
                    await _maintenance.ExportDocxAsync(_sessionId, dest, options, CancellationToken.None);
                    break;
            }
            _errors.Info("Exported to " + dest);
            _revealFile(dest);
            _dispatch(() => Closed?.Invoke());
        }
        catch (Exception ex) { _errors.Report("Export", ex); }
        finally { IsBusy = false; }
    }
```
- [ ] **Add the converter instance.** In `ExportFormatToBool.cs` replace lines 11–12:
```csharp
    public static readonly ExportFormatToBool Zip = new() { _target = ExportFormat.Zip };
    public static readonly ExportFormatToBool Docx = new() { _target = ExportFormat.Docx };
```
with:
```csharp
    public static readonly ExportFormatToBool Zip = new() { _target = ExportFormat.Zip };
    public static readonly ExportFormatToBool Docx = new() { _target = ExportFormat.Docx };
    public static readonly ExportFormatToBool Markdown = new() { _target = ExportFormat.Markdown };
```
- [ ] **Run tests and see PASS.** Same filter — expected: 5 passed (the class's 3 existing + 2 new; the zip/docx/cancel tests pin that the Zip and Docx branches are behavior-identical to before).
- [ ] **Commit.**
```
git add src/LocalScribe.App/ViewModels/ExportDialogViewModel.cs src/LocalScribe.App/ExportFormatToBool.cs tests/LocalScribe.App.Tests/ExportDialogViewModelTests.cs
git commit -m "feat(app): ExportFormat.Markdown - dialog VM branch, .md Save-As, shared option toggles

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: XAML — Markdown radio + shared toggle visibility in `ExportDialog.xaml`, whole-branch gate
**Files:**
- Modify `src\LocalScribe.App\ExportDialog.xaml` only (two edits below; `ExportDialog.xaml.cs` untouched).
- No new unit test (XAML rendering is not unit-tested here). The gate is: 0-warning build + full App + Core suites green + the manual smoke below.

**Interfaces:**
- Consumes: `ExportFormatToBool.Markdown` and `ShowOptionToggles` (Task 3), existing `BoolToVis` converter.
- Produces: no new types. The toggle panel's `IsDocx` binding is rebound to `ShowOptionToggles` (the last `IsDocx` XAML consumer — the VM property remains, tested, for surface stability).

Steps:
- [ ] **Edit 1 — third radio.** In `ExportDialog.xaml` replace lines 14–15:
```xml
        <RadioButton Content="Word document (.docx transcript)" GroupName="Fmt"
                     IsChecked="{Binding Format, Converter={x:Static vm:ExportFormatToBool.Docx}}" Margin="0,2" />
```
with:
```xml
        <RadioButton Content="Word document (.docx transcript)" GroupName="Fmt"
                     IsChecked="{Binding Format, Converter={x:Static vm:ExportFormatToBool.Docx}}" Margin="0,2" />
        <RadioButton Content="Markdown (.md transcript)" GroupName="Fmt"
                     IsChecked="{Binding Format, Converter={x:Static vm:ExportFormatToBool.Markdown}}" Margin="0,2" />
```
- [ ] **Edit 2 — shared toggle visibility.** Replace line 16 (now line 18 after Edit 1):
```xml
        <StackPanel Margin="16,8,0,0" Visibility="{Binding IsDocx, Converter={StaticResource BoolToVis}}">
```
with:
```xml
        <!-- The two toggles apply to BOTH textual formats (design 2026-07-18 section 3):
             docx AND markdown; hidden for zip, which archives the session folder as-is. -->
        <StackPanel Margin="16,8,0,0" Visibility="{Binding ShowOptionToggles, Converter={StaticResource BoolToVis}}">
```
- [ ] **Build 0-warning + full App/Core suites green.** Run:
```
dotnet build "src\LocalScribe.App\LocalScribe.App.csproj" --nologo -warnaserror -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\markdown-export\
dotnet test "tests\LocalScribe.App.Tests\LocalScribe.App.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\markdown-export\
dotnet test "tests\LocalScribe.Core.Tests\LocalScribe.Core.Tests.csproj" --nologo -p:BaseOutputPath=C:\Users\SAMUE~1.SAM\AppData\Local\Temp\localscribe-isobin\markdown-export\
```
Expected: build 0 warnings; App suite fully green (incl. `XamlHygieneTests` — the new markup adds no brushes or styles at all); Core suite green except the 2 known pre-existing fixture failures.
- [ ] **Manual smoke (WPF — not unit-testable).** Launch the app, open a finished session's Export dialog, then:
  1. **Three radios:** Zip / Word / Markdown all present; the two checkboxes appear for Word AND Markdown and disappear for Zip, flipping live as radios change.
  2. **Markdown export:** pick Markdown → Save-As defaults to `<sanitized title>.md` with a Markdown filter → export → the completion info shows and Explorer reveals the file.
  3. **Content check:** open the `.md` in a markdown viewer — `# title` heading, the metadata bullet list (App/Date/Matter(s)/Participants/Medium), the italic disclaimer, `**[stamp] Name:**` turns, `_[marker]_` lines, and the footer text under a horizontal rule. Re-export with both toggles off — no stamps, no markers, labels intact.
  4. **Versioned session (if one exists):** the footer ends with `- Transcript version vN (model)`, matching the docx export of the same session.
  5. **Save-time path untouched:** the session folder's own `transcript.md` is byte-identical before/after the export (the export writes only to the chosen destination).
- [ ] **Commit.**
```
git add src/LocalScribe.App/ExportDialog.xaml
git commit -m "feat(app): Markdown radio + shared toggles visibility in the export dialog XAML

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

## Self-review

**(a) Spec coverage — every design §3 bullet maps to tasks:**
- "`MarkdownRenderer` gains a `Write(...)` overload at DocxRenderer parity: consumes the same `SessionProjectionLoader` triple (`Header` / `TextView` / `Rows`), emits metadata block, disclaimer, footer (reuses `Settings.DocxFooterText`), honoring `IncludeTimestamps` / `IncludeMarkers`" → **Task 1** (signature takes exactly the triple's three members + `timestampsMode` + `footerText` + `DocxOptions`; metadata/disclaimer content rules copied from `DocxRenderer.Write` lines 47–58 including the `(none)` placeholders and Description-only-when-present; `DocxRenderer.Disclaimer` referenced, not duplicated; footer via rule block). The concrete markdown shape (bullet-list metadata, italic disclaimer, save-time turn dialect, `---` footer) is a plan-time decision encoded in exact-string tests, per the round's testing strategy ("markdown renderer parity", design §8). `Settings.DocxFooterText` reuse happens at the call site (Task 2), exactly where docx composes it.
- "The existing save-time `MarkdownRenderer.Render` → `transcript.md` path is untouched" → **Task 1** is append-only inside the class (the quoted edit replaces only the class-closing brace region); Task 1's PASS step re-runs `RendererTests` (exact-string pin) + `SessionProjectionLoaderTests` (byte-identity guard); Global Constraints carries the hard rule.
- "`ExportFormat` gains `Markdown`; the export dialog shows the same option toggles as docx; `SavePathRequest` filter `*.md`" → **Task 3** (enum append + `ShowOptionToggles` generalization of the `IsDocx` gate, `IsDocx` kept so no existing binding/consumer breaks + `"Markdown (*.md)|*.md"` filter + sanitized default name mirroring docx) + **Task 4** (third radio via a new `ExportFormatToBool.Markdown` instance; toggle panel rebinds to `ShowOptionToggles`).
- "`MaintenanceService.ExportMarkdownAsync` mirrors `ExportDocxAsync` exactly: `RunForSessionAsync` session gate, `ExportWithOutputCleanupAsync` (output-file-only cleanup on failure), `SessionProjectionLoader.LoadAsync`, `ExportFileNames.Sanitize` default name" → **Task 2** (all four, plus the versioned-footer composition duplicated verbatim so md/docx provenance can never disagree; the `Sanitize` default name lives in the VM branch, Task 3, exactly where docx's does). Cleanup contract tested (pre-existing Save-As target survives an early failure); version-note and root-version footers tested against the same `SeedVersionedAsync` fixture the docx test uses.
- §1 binding rules: verbatim/no-filtering pinned by Task 1's `Row_text_is_verbatim_never_escaped_or_filtered`; nothing touches transcript files (export writes only to `destPath`); no markers written, no consent paths involved; degradation not applicable (a failed export throws visibly through `IUiErrorReporter.Report`, unchanged catch in `ExportAsync`).
- §8 testing strategy: "markdown renderer parity" → Task 1's four pure tests against `DocxRendererTests`' identical sample data; service + VM integration tests mirror the existing docx coverage one-for-one.

**(b) Placeholder scan:** no TBD / "add validation" / "similar to Task N" anywhere — every step carries full test code, full implementation code, and quotes the exact current code being replaced (`MarkdownRenderer.cs` 31–33; `MaintenanceService.cs` usings 1–5 and 702–709; `ExportDialogViewModel.cs` 7, 36–37, 43–66; `ExportFormatToBool.cs` 11–12; `ExportDialog.xaml` 14–16). Every run command names its exact filter, the isolated `markdown-export` BaseOutputPath, and the expected failure/pass output.

**(c) Type consistency across tasks:** `MarkdownRenderer.Write(TranscriptHeader, SessionTextView, IReadOnlyList<DisplayRow>, string, string, DocxOptions) : string` (Task 1) is called in Task 2 with `loaded.Header : TranscriptHeader`, `loaded.TextView : SessionTextView`, `loaded.Rows : IReadOnlyList<DisplayRow>` (all members of `LoadedProjection`, `SessionProjectionLoader.cs:12-24`), `settings.Current.Timestamps : string`, the composed `footerText : string`, and the `DocxOptions` parameter — matching positionally and by type. `ExportMarkdownAsync(string, string, DocxOptions, CancellationToken) : Task` (Task 2) matches Task 3's call with `_sessionId`, `dest`, the shared `options`, `CancellationToken.None`. `TranscriptVersions.Root`/`ShortId` and `loaded.VersionId : string` are the same members `ExportDocxAsync` already reads in this file (no new usings beyond `System.Text` for `Encoding`; `MarkdownRenderer` rides the existing `using LocalScribe.Core.Projection;`). `Encoding.UTF8.GetBytes` emits no BOM (BOM comes only from `GetPreamble`/`StreamWriter` defaults), satisfying the UTF-8-no-BOM claim, and `FileStream.WriteAsync(byte[], CancellationToken)` resolves via the `ReadOnlyMemory<byte>` overload. `ExportFormat.Markdown` is append-only — verified that the enum's only consumers are `ExportDialogViewModel`, `ExportFormatToBool`, and `ExportDialog.xaml`, all updated here; the `switch` on `Format` keeps `default` → docx so exhaustiveness warnings cannot arise. `ShowOptionToggles` and `IsDocx` are plain get-only computed properties raised from the single `OnFormatChanged`; tests assert both notifications. All new members tests touch are `public` (no InternalsVisibleTo). Test fixtures reused, not invented: `MakeService`/`WriteFinalizedSessionAsync` (`MaintenanceServiceTests.cs:18-42`), `MakeService(Settings)`/`SeedVersionedAsync` (`MaintenanceServiceVersionsTests.cs:24-53` — active `v2-tiny.en-...` with "V2 words." / root "Root words.", exactly what the EndsWith/Contains asserts consume), `MakeAsync` (`ExportDialogViewModelTests.cs:16-33`, meta title "Doe intake" → the `# Doe intake` heading assert). All good.
