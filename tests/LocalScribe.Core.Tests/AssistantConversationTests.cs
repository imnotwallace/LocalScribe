using LocalScribe.Core.Assistant;
namespace LocalScribe.Core.Tests;

public sealed class AssistantConversationTests
{
    private static AssistantChatTurn Turn(string q, string a) =>
        new("id", default, q, a, [], "m", "cpu", "1", false, null, [], [], [], 0);

    [Fact]
    public void No_recap_no_turns_is_empty()
        => Assert.Equal("", AssistantConversation.BuildHistoryBlock(null, []));

    [Fact]
    public void Prior_turns_render_as_labelled_pairs_in_order()
    {
        string block = AssistantConversation.BuildHistoryBlock(null,
            [Turn("who spoke?", "Sam [00:08]."), Turn("when?", "Tuesday [00:12].")]);
        // earlier Q/A appear before later ones, each clearly a prior exchange
        Assert.Contains("who spoke?", block);
        Assert.Contains("Sam [00:08].", block);
        Assert.True(block.IndexOf("who spoke?") < block.IndexOf("when?"));
    }

    [Fact]
    public void Recap_precedes_the_verbatim_turns()
    {
        string block = AssistantConversation.BuildHistoryBlock("earlier: filing due Tuesday",
            [Turn("who agreed?", "Sam and you [00:08].")]);
        Assert.Contains("earlier: filing due Tuesday", block);
        Assert.True(block.IndexOf("earlier: filing due Tuesday") < block.IndexOf("who agreed?"));
    }
}
