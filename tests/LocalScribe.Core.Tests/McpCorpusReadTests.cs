using LocalScribe.Core.Assistant;
using LocalScribe.Core.Mcp;
using LocalScribe.Core.Model;
using LocalScribe.Core.Search.Semantic;
using LocalScribe.Core.Storage;

public sealed class McpCorpusReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    private StoragePaths Paths => new(_root);
    private static readonly DateTimeOffset T0 = new(2026, 7, 20, 9, 0, 0, TimeSpan.Zero);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class NoEmbeddings : IMcpEmbeddingProvider
    {
        public Task<(LocalScribe.Core.Search.Semantic.IEmbeddingClient, string)> GetAsync(CancellationToken ct)
            => throw new McpToolException("semantic unavailable", "error");
    }

    private McpCorpus Corpus()
    {
        var settings = new Settings { StorageRoot = _root };
        var time = new ManualUtcTimeProvider(T0.AddDays(1));
        return new McpCorpus(Paths, settings, time, new McpConsentStore(Paths),
            new McpLexicalCatalog(Paths, settings, time), new SemanticIndexStore(Paths),
            new MatterStore(Paths.MattersDir), new SummaryStore(Paths), new NoEmbeddings());
    }

    private async Task SeedAsync(int lineCount = 6)
    {
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", "m-001", T0, "webex",
            Enumerable.Range(0, lineCount).Select(i => $"line number {i}").ToArray());
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);
    }

    /// <summary>Seeds the basic 6-line session, then splits seq 2 ("line number 2", 2000-3000ms,
    /// TranscriptSource.Local) into two human-authored parts via the same non-destructive edits.json
    /// overlay the real read view writes (EditStore.ApplySplitAsync - see EditStoreSplitTests /
    /// TranscriptProjectionSplitTests). Reused instead of hand-writing projection internals.</summary>
    private async Task SeedSplitAsync()
    {
        await SeedAsync();
        var store = new LocalScribe.Core.Storage.EditStore(Paths.SessionDir("s1"),
            new ManualUtcTimeProvider(T0));
        await store.ApplySplitAsync(2, TranscriptSource.Local,
        [
            new SplitPart { Text = "line number", StartMs = 2000, DerivedStart = false },
            new SplitPart { Text = "2", StartMs = 2500, DerivedStart = true },
        ], default);
    }

    /// <summary>One human-authored line, no machine transcript content: ReadTranscriptAsync must
    /// surface it with a null Seq/PartIndex and its minted insertion id (Task 5, design 2026-08-11
    /// section 2.3).</summary>
    private async Task<string> SessionWithInsertionAsync(string text, long startMs)
    {
        const string id = "s-ins";
        TestSessionSeeder.EnsureMatter(Paths, "m-001", "Smith v Jones");
        await TestSessionSeeder.WriteInsertionOnlySession(Paths, id, "Insertion call", "m-001", T0,
            "webex", minDurationMs: startMs + 60_000);
        await TestSessionSeeder.InsertLineAsync(Paths, new ManualUtcTimeProvider(T0), id, text, startMs);
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = ["m-001"] }, default);
        return id;
    }

    /// <summary>Six machine segments (seq 0-5, SeedAsync's default id "s1") plus TWO human-authored
    /// lines, positioned so each of the two aroundSeq/seq-range guards this task adds has a way to
    /// fail:
    ///  - a TRAILING line past every real segment's StartMs (5_500ms vs. the last one's 5_000ms), so
    ///    it is always the LAST unit in the flattened stream - the position that would stretch a
    ///    seq-bounded span if its placeholder Seq 0 were allowed to satisfy "u.Seq &lt;= toSeq"
    ///    (A_seq_bounded_read_is_never_ANCHORED_by_an_inserted_row).
    ///  - a LEADING line tied at StartMs 0 with the real seq 0 row - an inserted row sorts BEFORE a
    ///    machine row at an identical StartMs (TranscriptProjection.InsertedRank), so it is always
    ///    the FIRST unit. Without the aroundSeq centring guard, a first-match scan for Seq==0 would
    ///    land on THIS placeholder instead of the real seq 0 row
    ///    (AroundSeq_never_centres_on_an_inserted_row) - a trailing line alone can never exercise
    ///    that path, because the real seq 0 row is always found first regardless of the guard.</summary>
    private async Task<string> SessionWithSegmentsAndTrailingInsertionAsync()
    {
        await SeedAsync(lineCount: 6);
        var time = new ManualUtcTimeProvider(T0);
        await TestSessionSeeder.InsertLineAsync(Paths, time, "s1", "Leading off-mic remark.", 0);
        await TestSessionSeeder.InsertLineAsync(Paths, time, "s1", "Trailing inserted line.", 5_500);
        return "s1";
    }

    /// <summary>Three machine segments (seq 0-2) plus a human-authored line whose StartMs (500ms)
    /// sorts strictly between seq 0 (StartMs 0) and seq 1 (StartMs 1000) - it lands INSIDE an
    /// index-based [0,2] span and must travel with it rather than being dropped (the opposite
    /// failure mode from the anchor guard).</summary>
    private async Task<string> SessionWithInsertionBetweenSeqs0And2Async()
    {
        await SeedAsync(lineCount: 3);
        await TestSessionSeeder.InsertLineAsync(Paths, new ManualUtcTimeProvider(T0), "s1",
            "Off-mic aside.", 500);
        return "s1";
    }

    [Fact]
    public async Task ReadTranscript_returns_an_inserted_row_with_a_null_seq_and_its_insertion_id()
    {
        string sessionId = await SessionWithInsertionAsync("Off-mic answer.", startMs: 50_000);
        var res = await Corpus().ReadTranscriptAsync(sessionId, null, null, null, 0, null, default);

        var row = Assert.Single(res.Rows, r => r.Text == "Off-mic answer.");
        Assert.Equal("speech", row.Kind);
        Assert.Null(row.Seq);
        Assert.Null(row.PartIndex);
        Assert.Equal("i000001", row.InsertionId);
    }

    [Fact]
    public async Task A_seq_bounded_read_is_never_ANCHORED_by_an_inserted_row()
    {
        // An inserted unit has no seq. Letting a placeholder 0 satisfy "u.Seq <= toSeq" would let a
        // line at the END of a long transcript extend the span and return far more than was asked for.
        string sessionId = await SessionWithSegmentsAndTrailingInsertionAsync();
        var res = await Corpus().ReadTranscriptAsync(sessionId, fromSeq: null, toSeq: 1,
            aroundSeq: null, context: 0, cursor: null, default);

        Assert.All(res.Rows, r => Assert.True(r.Seq is null or <= 1));
        Assert.DoesNotContain(res.Rows, r => r.Text == "Trailing inserted line.");
    }

    [Fact]
    public async Task An_inserted_row_INSIDE_a_seq_span_is_still_returned()
    {
        // The opposite failure: silently omitting human-authored content from a bounded read is
        // concealment. The span is index-based, so anything between the anchors travels with them.
        string sessionId = await SessionWithInsertionBetweenSeqs0And2Async();
        var res = await Corpus().ReadTranscriptAsync(sessionId, fromSeq: 0, toSeq: 2,
            aroundSeq: null, context: 0, cursor: null, default);

        Assert.Contains(res.Rows, r => r.InsertionId == "i000001");
    }

    [Fact]
    public async Task AroundSeq_never_centres_on_an_inserted_row()
    {
        string sessionId = await SessionWithSegmentsAndTrailingInsertionAsync();
        var res = await Corpus().ReadTranscriptAsync(sessionId, null, null, aroundSeq: 0,
            context: 0, cursor: null, default);

        Assert.Equal(0, Assert.Single(res.Rows).Seq);
    }

    [Fact]
    public async Task Reads_a_seq_range_with_speaker_names()
    {
        await SeedAsync();
        var r = await Corpus().ReadTranscriptAsync("s1", 1, 3, null, 0, null, default);
        Assert.All(r.Rows.Where(x => x.Kind == "speech"),
            x => Assert.InRange(x.Seq!.Value, 1, 3));
        Assert.Contains(r.Rows, x => x.Text.Contains("line number 2"));
        Assert.All(r.Rows.Where(x => x.Kind == "speech"), x => Assert.False(string.IsNullOrEmpty(x.Speaker)));
        Assert.Null(r.NextCursor);
    }

    [Fact]
    public async Task Around_seq_returns_the_context_window()
    {
        await SeedAsync();
        var r = await Corpus().ReadTranscriptAsync("s1", null, null, 3, 1, null, default);
        var seqs = r.Rows.Where(x => x.Seq is not null).Select(x => x.Seq!.Value).ToList();
        Assert.Equal([2, 3, 4], seqs);
    }

    [Fact]
    public async Task Split_segment_parts_are_distinguishable_in_a_read()
    {
        await SeedSplitAsync();
        // A from/to range pinned to the split seq: both parts share Seq 2 and must come back
        // as two rows, distinguished only by PartIndex.
        var r = await Corpus().ReadTranscriptAsync("s1", 2, 2, null, 0, null, default);
        var splitRows = r.Rows.Where(x => x.Seq == 2).OrderBy(x => x.PartIndex).ToList();
        Assert.Equal(2, splitRows.Count);
        Assert.Equal(0, splitRows[0].PartIndex);
        Assert.Equal(1, splitRows[1].PartIndex);
        Assert.Equal("line number", splitRows[0].Text);
        Assert.Equal("2", splitRows[1].Text);
    }

    [Fact]
    public async Task Around_seq_with_part_index_centers_on_the_requested_part()
    {
        await SeedSplitAsync();
        // seq 2 part 1 ("2", 2500-3000ms) sits one unit later than part 0 ("line number",
        // 2000-2500ms) in the flattened unit stream, so a context-1 window centered on part 1
        // reaches into seq 3 while the equivalent part-0 window does not.
        var onPart1 = await Corpus().ReadTranscriptAsync("s1", null, null, 2, 1, null, default,
            aroundPartIndex: 1);
        var onPart0 = await Corpus().ReadTranscriptAsync("s1", null, null, 2, 1, null, default,
            aroundPartIndex: 0);

        Assert.Contains(onPart1.Rows, x => x.Seq == 2 && x.PartIndex == 1);
        var texts1 = onPart1.Rows.Select(x => x.Text).ToList();
        var texts0 = onPart0.Rows.Select(x => x.Text).ToList();
        Assert.NotEqual(texts0, texts1);
        Assert.Contains(onPart1.Rows, x => x.Text.Contains("line number 3"));
        Assert.DoesNotContain(onPart0.Rows, x => x.Text.Contains("line number 3"));
    }

    [Fact]
    public async Task Around_seq_without_part_index_keeps_first_part_behavior()
    {
        await SeedSplitAsync();
        // No aroundPartIndex given: existing callers must keep centering on the first unit with
        // that seq (the split's part 0), unaffected by this parameter's introduction.
        var r = await Corpus().ReadTranscriptAsync("s1", null, null, 2, 1, null, default);
        var texts = r.Rows.Select(x => x.Text).ToList();
        Assert.Equal(["line number 1", "line number", "2"], texts);
        Assert.Equal(0, r.Rows.First(x => x.Seq == 2).PartIndex);
    }

    [Fact]
    public async Task Char_cap_pages_with_a_version_pinned_cursor()
    {
        await SeedAsync(lineCount: 40);
        // Tiny cap via the test seam so we don't need 15k chars of fixture text.
        var r = await Corpus().ReadTranscriptAsync("s1", null, null, null, 0, null, default,
            maxChars: 60);
        Assert.True(r.Rows.Count < 40);
        Assert.NotNull(r.NextCursor);
        Assert.StartsWith(r.VersionId + ":", r.NextCursor);
        var r2 = await Corpus().ReadTranscriptAsync("s1", null, null, null, 0, r.NextCursor, default,
            maxChars: 60);
        Assert.NotEqual(r.Rows[0].Seq, r2.Rows[0].Seq);
    }

    /// <summary>The paging bug: endExclusive used to stay at the transcript's end whenever a
    /// cursor was present, so the second (and every later) page of a bounded from_seq/to_seq read
    /// ran past to_seq all the way to the end of the session instead of stopping at the requested
    /// span. Seeds enough lines that the [from_seq, to_seq] span itself exceeds a tiny maxChars,
    /// forcing multiple pages, then asserts every seq across every page stays inside the span.</summary>
    [Fact]
    public async Task Bounded_span_paging_stops_at_to_seq_and_never_escapes_the_span()
    {
        await SeedAsync(lineCount: 40);
        const int fromSeq = 10, toSeq = 20;
        var seqs = new List<int>();
        string? cursor = null;
        int pages = 0;
        do
        {
            var page = await Corpus().ReadTranscriptAsync("s1", fromSeq, toSeq, null, 0, cursor, default,
                maxChars: 40);
            seqs.AddRange(page.Rows.Where(x => x.Seq is not null).Select(x => x.Seq!.Value));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null && pages < 20);

        Assert.True(pages > 1, "fixture must force multiple pages to exercise cursor paging");
        Assert.All(seqs, s => Assert.InRange(s, fromSeq, toSeq));
        Assert.Equal(toSeq, seqs.Max());          // the span's last page still reaches to_seq
        Assert.DoesNotContain(seqs, s => s > toSeq);   // and never runs past it to the end of the session
    }

    [Fact]
    public async Task Cursor_from_a_different_version_is_rejected()
    {
        await SeedAsync();
        var ex = await Assert.ThrowsAsync<McpToolException>(() =>
            Corpus().ReadTranscriptAsync("s1", null, null, null, 0, "vOLD:2", default));
        Assert.Contains("version changed", ex.Message);
    }

    [Fact]
    public async Task Hidden_session_read_is_indistinguishable_from_missing()
    {
        await SeedAsync();
        TestSessionSeeder.WriteBasicSession(Paths, "s2", "Hidden", null, T0, "webex", "secret");
        var exHidden = await Assert.ThrowsAsync<McpToolException>(() =>
            Corpus().ReadTranscriptAsync("s2", null, null, null, 0, null, default));
        var exMissing = await Assert.ThrowsAsync<McpToolException>(() =>
            Corpus().ReadTranscriptAsync("nope", null, null, null, 0, null, default));
        Assert.Equal(exMissing.Message, exHidden.Message);
        Assert.Equal(McpConsentFilter.NotFoundMessage, exHidden.Message);
    }

    [Fact]
    public async Task GetSummary_returns_newest_version_with_provenance()
    {
        await SeedAsync();
        var store = new SummaryStore(Paths);
        await store.AppendAsync("s1", new SummaryVersion("v-a", T0, "v1",
            new AssistantModelRef("qwen.gguf", "aa", "cuda"), 2, "OLD", Stale: true), default);
        await store.AppendAsync("s1", new SummaryVersion("v-b", T0.AddHours(1), "v1",
            new AssistantModelRef("qwen.gguf", "aa", "cpu"), 2, "NEW summary", Stale: false,
            CudaFellToCpu: true), default);
        var r = await Corpus().GetSummaryAsync("s1", default);
        Assert.Equal("NEW summary", r.ContentMarkdown);
        Assert.True(r.CudaFellToCpu);
        Assert.Equal("cpu", r.Backend);
    }

    [Fact]
    public async Task GetSummary_on_summaryless_session_says_so()
    {
        await SeedAsync();
        var ex = await Assert.ThrowsAsync<McpToolException>(() => Corpus().GetSummaryAsync("s1", default));
        Assert.Contains("no summary", ex.Message);
    }

    // A genuinely legacy (pre-v3) session.json + meta.json - matches ReadOnlyProjectionTests'
    // fixture shape. A CURRENT-schema fixture (like SeedAsync's) would leave every file
    // untouched no matter what persistMigration value ReadTranscriptAsync passes through,
    // because SessionStore/MetadataStore only write when the on-disk schema version is stale
    // (see SessionStore.ReadWithSynthesizedMetaAsync / MetadataStore.LoadAsync) - so only a
    // legacy fixture actually exercises the persistMigration:false plumbing this test exists to pin.
    private const string LegacyMetaJson = "{\"schemaVersion\":1,\"title\":\"Old intake\"}";
    private static string LegacySessionJson(string id) => $@"{{
        ""schemaVersion"": 1,
        ""id"": ""{id}"",
        ""app"": ""Webex"",
        ""startedAtUtc"": ""2026-07-01T09:00:00Z"",
        ""endedAtUtc"": ""2026-07-01T09:05:00Z"",
        ""durationMs"": 300000,
        ""sources"": [""Local"", ""Remote""],
        ""model"": ""small.en"",
        ""backend"": ""CPU"",
        ""language"": ""en"",
        ""audioRetained"": true,
        ""title"": ""Legacy call"",
        ""segmentCount"": 1,
        ""markerCount"": 0
    }}";

    [Fact]
    public async Task Reading_a_transcript_never_writes_to_the_session_folder()
    {
        string sessionDir = Paths.SessionDir("s1");
        Directory.CreateDirectory(sessionDir);
        await File.WriteAllTextAsync(Paths.SessionJson("s1"), LegacySessionJson("s1"));
        await File.WriteAllTextAsync(Paths.MetaJson("s1"), LegacyMetaJson);
        await new TranscriptStore(Paths.TranscriptJsonl("s1")).AppendAsync(
            TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "Hello there.", "Me"), default);
        // Unassigned (no matter) session: legacy meta.json predates matterIds, so AllowUnassigned
        // is what makes it visible - proves the read-only guarantee holds on the consent path too.
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowUnassigned = true }, default);

        // Snapshot the WHOLE storage root (minus mcp\, legitimately writable) - not just the
        // session's own folder - so a write reachable elsewhere (e.g. matters\) cannot hide.
        var before = SnapshotFiles(Paths.Root);

        await Corpus().ReadTranscriptAsync("s1", null, null, null, 0, null, default);

        AssertFilesUnchanged(Paths.Root, before);
    }

    /// <summary>A hand-written LEGACY matter (schemaVersion 1) tagged to a session: the session
    /// read must migrate it to the current schema in memory but never write-migrate matter.json
    /// nor upsert the shared matters\matters.json index (the write-on-read hole
    /// SessionProjectionLoader.LoadAsync had at line 75 before the fix - TestSessionSeeder.EnsureMatter
    /// always stamps the current version via MatterStore.CreateAsync, so no existing fixture could
    /// reach the migrating branch; this one is hand-written specifically to reach it).</summary>
    [Fact]
    public async Task Reading_a_session_with_a_legacy_matter_writes_nothing()
    {
        const string matterId = "m-legacy";
        Directory.CreateDirectory(Path.Combine(Paths.MattersDir, matterId));
        await File.WriteAllTextAsync(Path.Combine(Paths.MattersDir, matterId, "matter.json"),
            $"{{\"schemaVersion\":1,\"id\":\"{matterId}\",\"name\":\"Legacy Matter\"}}");
        TestSessionSeeder.WriteBasicSession(Paths, "s1", "Call", matterId, T0, "webex", "hello there");
        await new McpConsentStore(Paths).SaveAsync(new McpConsentDocument
        { Enabled = true, AllowedMatterIds = [matterId] }, default);

        var before = SnapshotFiles(Paths.Root);

        var r = await Corpus().ReadTranscriptAsync("s1", null, null, null, 0, null, default);

        Assert.NotEmpty(r.Rows);
        AssertFilesUnchanged(Paths.Root, before);
    }

    // Whole-storage-root, mcp\-excluded snapshot helpers - mirrors ReadOnlyProjectionTests'
    // (this class's own read-only assertions were narrowed to one session's folder before the
    // fix, which is exactly why the matters\ write-on-read hole was missed).
    private static Dictionary<string, byte[]> SnapshotFiles(string root)
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !IsUnderMcpDir(root, f))
                .ToDictionary(f => f, File.ReadAllBytes)
            : new Dictionary<string, byte[]>();

    private static void AssertFilesUnchanged(string root, Dictionary<string, byte[]> before)
    {
        var after = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(f => !IsUnderMcpDir(root, f)).ToList()
            : new List<string>();
        Assert.Equal(before.Keys.OrderBy(x => x), after.OrderBy(x => x));
        foreach (var (path, bytes) in before)
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static bool IsUnderMcpDir(string root, string filePath)
    {
        string mcpDir = new StoragePaths(root).McpDir;
        string rel = Path.GetRelativePath(mcpDir, filePath);
        return !rel.StartsWith("..", StringComparison.Ordinal) && rel != ".";
    }
}
