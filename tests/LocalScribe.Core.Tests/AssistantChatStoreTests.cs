using LocalScribe.Core.Assistant;
using LocalScribe.Core.Storage;

public class AssistantChatStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string ChatsPath => Path.Combine(_root, "assistant", "chats.json");

    private static AssistantChatTurn Turn(string id, string question) => new(
        id, new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.Zero), question,
        "The parties agreed [00:01:05]",
        [new AnswerLine("The parties agreed",
            [new CitationChip("00:01:05", true, "s1", 3, "agreed")], true, false, null)],
        "qwen3-4b-instruct-2507-q4_k_m.gguf", "cuda", "3", false, null, ["s1"], [], [], 0);

    [Fact]
    public async Task Missing_file_loads_as_an_empty_log()
    {
        var store = new AssistantChatStore(ChatsPath);
        var log = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(AssistantChatStore.Version, log.SchemaVersion);
        Assert.Empty(log.Turns);
    }

    [Fact]
    public async Task Append_creates_the_folder_and_roundtrips_validated_lines()
    {
        var store = new AssistantChatStore(ChatsPath);
        await store.AppendAsync(Turn("t1", "what was agreed"), CancellationToken.None);
        await store.AppendAsync(Turn("t2", "when is payment due"), CancellationToken.None);

        var log = await store.LoadAsync(CancellationToken.None);
        Assert.Equal(2, log.Turns.Count);
        Assert.Equal("what was agreed", log.Turns[0].Question);
        Assert.Equal("cuda", log.Turns[0].Backend);                  // provenance survives
        var chip = Assert.Single(Assert.Single(log.Turns[0].Lines).Chips);
        Assert.Equal(("00:01:05", true, "s1", 3), (chip.Stamp, chip.Verified, chip.SessionId, chip.Seq));
    }

    [Fact]
    public async Task Newer_schema_is_rejected_loud()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ChatsPath)!);
        await File.WriteAllTextAsync(ChatsPath, "{\"schemaVersion\": 99, \"turns\": []}");
        var store = new AssistantChatStore(ChatsPath);
        await Assert.ThrowsAsync<NotSupportedException>(() => store.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Turns_written_before_the_fall_field_existed_still_load_as_not_fallen()
    {
        // CudaFellToCpu is a 2026-07-24 ADDITIVE trailing field: a chats.json written by an older
        // build (no "cudaFellToCpu" member on the turn at all) must still load, at the same
        // schemaVersion, with the flag false - never a crash, never a false positive.
        Directory.CreateDirectory(Path.GetDirectoryName(ChatsPath)!);
        await File.WriteAllTextAsync(ChatsPath, """
            {"schemaVersion":1,"turns":[{"id":"t1","askedAtUtc":"2026-07-19T10:00:00+00:00",
            "question":"q","answerMarkdown":"a [00:01:05]","lines":[],"model":"m.gguf","backend":"cpu",
            "promptVersion":"3","excerptMode":false,"disclosure":null,"includedSessionIds":["s1"],
            "omittedSessionIds":[],"missingSummarySessionIds":[],"unverifiableClaims":0}]}
            """);

        var store = new AssistantChatStore(ChatsPath);
        var turn = Assert.Single((await store.LoadAsync(CancellationToken.None)).Turns);
        Assert.Equal("t1", turn.Id);
        Assert.False(turn.CudaFellToCpu);
    }

    [Fact]
    public void StoragePaths_place_chats_in_the_assistant_folders()
    {
        var paths = new StoragePaths(_root);
        Assert.Equal(Path.Combine(_root, "sessions", "s1", "assistant", "chats.json"),
            paths.SessionChatsJson("s1"));
        Assert.Equal(Path.Combine(_root, "matters", "m1", "assistant", "chats.json"),
            paths.MatterChatsJson("m1"));
    }
}
