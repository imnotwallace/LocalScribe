using LocalScribe.Core.Audio;
using LocalScribe.Core.Model;
using LocalScribe.Core.Pipeline;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Transcription;

public class TranscriptMergerTests
{
    private static TranscribedSegment Ts(SourceKind src, long startMs, long endMs, string text)
    {
        var pcm = new float[160];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = 0.5f;
        return new TranscribedSegment(new AudioSegment(src, startMs, endMs, pcm),
            new TranscriptionResult(text, "en", 0.02), "small.en");
    }

    private static string TempJsonl() =>
        Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}", "transcript.jsonl");

    [Fact]
    public async Task Seq_is_finalization_order_but_view_is_startMs_order()
    {
        string path = TempJsonl();
        try
        {
            var merger = new TranscriptMerger(new TranscriptStore(path));
            await merger.InitializeAsync(default);

            // Remote finalizes FIRST but starts LATER; Local finalizes second, starts earlier.
            var a = await merger.AppendSegmentAsync(Ts(SourceKind.Remote, 500, 1500, "remote"), default);
            var b = await merger.AppendSegmentAsync(Ts(SourceKind.Local, 0, 400, "local"), default);

            Assert.Equal(0, a.Seq);                              // write order
            Assert.Equal(1, b.Seq);
            Assert.Equal(new[] { "local", "remote" },            // display order (spec 5)
                merger.View.Select(l => l.Text));

            var onDisk = await new TranscriptStore(path).ReadAllAsync(default);
            Assert.Equal(new[] { 0, 1 }, onDisk.Select(l => l.Seq));   // JSONL stays write-order
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Insert_event_reports_the_sorted_position()
    {
        string path = TempJsonl();
        try
        {
            var merger = new TranscriptMerger(new TranscriptStore(path));
            await merger.InitializeAsync(default);
            var inserts = new List<int>();
            merger.LineInserted += (i, _) => inserts.Add(i);

            await merger.AppendSegmentAsync(Ts(SourceKind.Remote, 500, 1500, "later"), default);
            await merger.AppendSegmentAsync(Ts(SourceKind.Local, 0, 400, "earlier"), default);

            Assert.Equal(new[] { 0, 0 }, inserts);               // second lands BEHIND the first
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Segment_line_carries_label_lang_noSpeech_and_rms()
    {
        string path = TempJsonl();
        try
        {
            var merger = new TranscriptMerger(new TranscriptStore(path));
            await merger.InitializeAsync(default);
            var line = await merger.AppendSegmentAsync(Ts(SourceKind.Remote, 0, 1000, "hi"), default);

            Assert.Equal(TranscriptSource.Remote, line.Source);
            Assert.Equal("Them", line.SpeakerLabel);             // structural attribution
            Assert.Equal("en", line.Lang);
            Assert.Equal(0.02, line.NoSpeechProb);
            Assert.Equal(-6.0, line.RmsDb!.Value, 1);            // 0.5 amplitude ~ -6 dB
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Markers_interleave_and_tie_break_after_segments()
    {
        string path = TempJsonl();
        try
        {
            var merger = new TranscriptMerger(new TranscriptStore(path));
            await merger.InitializeAsync(default);
            await merger.AppendSegmentAsync(Ts(SourceKind.Local, 1000, 2000, "a"), default);
            await merger.AppendMarkerAsync(Markers.TranscriptionLagging, 1000, default);

            Assert.Equal(2, merger.View.Count);
            Assert.Equal(TranscriptKind.Segment, merger.View[0].Kind);   // same startMs: System sorts last
            Assert.Equal(TranscriptKind.Marker, merger.View[1].Kind);
        }
        finally { CleanParent(path); }
    }

    [Fact]
    public async Task Initialize_seeds_seq_from_existing_jsonl()
    {
        string path = TempJsonl();
        try
        {
            var store = new TranscriptStore(path);
            await store.AppendAsync(TranscriptLine.Segment(0, TranscriptSource.Local, 0, 1, "old", "Me"), default);

            var merger = new TranscriptMerger(store);
            await merger.InitializeAsync(default);
            var line = await merger.AppendSegmentAsync(Ts(SourceKind.Local, 5, 6, "new"), default);
            Assert.Equal(1, line.Seq);
        }
        finally { CleanParent(path); }
    }

    private static void CleanParent(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
