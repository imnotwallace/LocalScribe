// tests/LocalScribe.Core.Tests/EditStoreSplitTests.cs
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

public class EditStoreSplitTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 15, 0, 0, TimeSpan.Zero);

    // Mirrors EditStoreTests.FinalizedSessionDirAsync: a temp session dir with a finalized
    // session.json, a meta.json, and a transcript.jsonl seeded with the given lines.
    private static async Task<string> FinalizedSessionDirAsync(params TranscriptLine[] lines)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        await new SessionStore(Path.Combine(dir, "session.json")).SaveAsync(new SessionRecord
        {
            Id = "s", App = AppKind.Webex, StartedAtUtc = T0, EndedAtUtc = T0.AddMinutes(30),
        }, default);
        await new MetadataStore(Path.Combine(dir, "meta.json")).SaveAsync(
            SessionMeta.CreateDefault(AppKind.Webex, T0, self: null), default);
        var transcript = new TranscriptStore(Path.Combine(dir, "transcript.jsonl"));
        foreach (var line in lines) await transcript.AppendAsync(line, default);
        return dir;
    }

    [Fact]
    public async Task ApplySplit_WritesEntry_AndClearsPriorCorrection()
    {
        string dir = await FinalizedSessionDirAsync(
            TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "Two speakers here.", "Them"));
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await store.ApplyTextCorrectionAsync(3, "Two speakers here (fixed).", default);

            await store.ApplySplitAsync(3, TranscriptSource.Remote,
            [
                new SplitPart { Text = "Two speakers", StartMs = 15000, DerivedStart = false },
                new SplitPart { Text = "here.", StartMs = 16000, DerivedStart = true },
            ], default);

            var edits = await store.LoadAsync(default);
            Assert.True(edits!.Splits.ContainsKey("3"));
            Assert.False(edits.Corrections.ContainsKey("3"));   // absorbed
            Assert.Equal(2, edits.Splits["3"].Parts.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ApplySplit_RejectsWhitespaceChild()
    {
        string dir = await FinalizedSessionDirAsync(
            TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "Hello there.", "Them"));
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ApplySplitAsync(3, TranscriptSource.Remote,
            [
                new SplitPart { Text = "Hello", StartMs = 15000, DerivedStart = false },
                new SplitPart { Text = "   ", StartMs = 16000, DerivedStart = true },
            ], default));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ApplySplit_RejectsFirstStartNotMachineStart()
    {
        string dir = await FinalizedSessionDirAsync(
            TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "Hello there.", "Them"));
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ApplySplitAsync(3, TranscriptSource.Remote,
            [
                new SplitPart { Text = "Hello", StartMs = 15500, DerivedStart = false },   // != 15000
                new SplitPart { Text = "there.", StartMs = 16000, DerivedStart = true },
            ], default));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ApplySplit_RejectsOutOfRangeOrNonMonotonicStart()
    {
        string dir = await FinalizedSessionDirAsync(
            TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "Hello there.", "Them"));
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await Assert.ThrowsAsync<ArgumentException>(() => store.ApplySplitAsync(3, TranscriptSource.Remote,
            [
                new SplitPart { Text = "Hello", StartMs = 15000, DerivedStart = false },
                new SplitPart { Text = "there.", StartMs = 99000, DerivedStart = true },   // > endMs 17000
            ], default));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task RemoveSplit_RestoresSingleSegment_AndIsNoOpWhenAbsent()
    {
        string dir = await FinalizedSessionDirAsync(
            TranscriptLine.Segment(3, TranscriptSource.Remote, 15000, 17000, "Hello there.", "Them"));
        try
        {
            var store = new EditStore(dir, new ManualUtcTimeProvider(T0));
            await store.ApplySplitAsync(3, TranscriptSource.Remote,
            [
                new SplitPart { Text = "Hello", StartMs = 15000, DerivedStart = false },
                new SplitPart { Text = "there.", StartMs = 16000, DerivedStart = true },
            ], default);

            Assert.True(await store.RemoveSplitAsync(3, default));
            var edits = await store.LoadAsync(default);
            Assert.Empty(edits!.Splits);
            Assert.False(await store.RemoveSplitAsync(3, default));   // second time: no-op
        }
        finally { Directory.Delete(dir, true); }
    }
}
