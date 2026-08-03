using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class TranscriptStoreTests
{
    private static string TempJsonl() =>
        Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "transcript.jsonl");

    [Fact]
    public async Task Append_writes_one_physical_line_per_record()
    {
        string path = TempJsonl();
        try
        {
            var store = new TranscriptStore(path);
            await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1000, "hi", "Me"), default);
            await store.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Remote, 500, 1500, "yo", "Them"), default);

            string[] physical = await File.ReadAllLinesAsync(path);
            Assert.Equal(2, physical.Length);                 // compact: exactly two lines
            Assert.DoesNotContain("\n", physical[0]);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task ReadAll_returns_write_order_and_append_is_additive()
    {
        string path = TempJsonl();
        try
        {
            var store = new TranscriptStore(path);
            await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1, "a", "Me"), default);
            var reopened = new TranscriptStore(path);          // simulate re-open (crash/restart)
            await reopened.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 2, 3, "b", "Me"), default);

            var all = await reopened.ReadAllAsync(default);
            Assert.Equal(new[] { 0, 1 }, all.Select(l => l.Seq));
            Assert.Equal("a", all[0].Text);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task NextSeq_is_zero_when_empty_and_max_plus_one_otherwise()
    {
        string path = TempJsonl();
        try
        {
            var store = new TranscriptStore(path);
            Assert.Equal(0, await store.NextSeqAsync(default));
            await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1, "a", "Me"), default);
            await store.AppendAsync(TranscriptLine.Marker(1, 5, Markers.RecoveredSession), default);
            Assert.Equal(2, await store.NextSeqAsync(default));
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Torn_final_line_is_skipped_and_counted_not_thrown()
    {
        string path = TempJsonl();
        try
        {
            var store = new TranscriptStore(path);
            await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1, "a", "Me"), default);
            // Simulate a crash mid-append: partial JSON, no trailing newline.
            await File.AppendAllTextAsync(path, "{\"seq\":1,\"source\":\"Rem");

            var result = await store.ReadAllDetailedAsync(default);
            Assert.Single(result.Lines);                       // the good line survives
            Assert.Equal(1, result.MalformedLineCount);        // the torn tail is surfaced
            Assert.Equal(1, await store.NextSeqAsync(default)); // seq counter unaffected by the tear
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Append_after_torn_tail_self_heals_onto_a_new_line()
    {
        string path = TempJsonl();
        try
        {
            var store = new TranscriptStore(path);
            await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1, "a", "Me"), default);
            await File.AppendAllTextAsync(path, "{\"seq\":1,\"source\":\"Rem");   // torn tail

            await store.AppendAsync(TranscriptLine.Segment(1, TranscriptSource.Local, 2, 3, "b", "Me"), default);

            var result = await store.ReadAllDetailedAsync(default);
            Assert.Equal(new[] { 0, 1 }, result.Lines.Select(l => l.Seq));   // new record intact
            Assert.Equal(1, result.MalformedLineCount);                      // torn bytes preserved on disk
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Read_succeeds_while_a_writer_holds_the_file_for_append()
    {
        // File.ReadAllLinesAsync opens FileShare.Read - other READERS are fine, writers are
        // locked out. Adding a live-session export made that reachable: an append landing during
        // an export would throw and lose an evidentiary line (design 2026-08-03 section 11).
        // NeedsNewlinePrefix in this same file already uses FileShare.ReadWrite; ReadAllDetailedAsync
        // must mirror it so a concurrent File.AppendAllTextAsync never gets locked out by a read.
        string path = TempJsonl();
        try
        {
            var store = new TranscriptStore(path);
            await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1, "one", "Me"), default);

            // Hold the file exactly as File.AppendAllTextAsync does internally.
            using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);

            var lines = await store.ReadAllAsync(default);
            Assert.Single(lines);
        }
        finally { CleanParent(path); }
    }

    private static void CleanParent(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
