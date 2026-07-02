using LocalScribe.Core.Model;
using LocalScribe.Core.Vocabulary;

public class VocabularyProviderTests
{
    private static Matter MatterWith(string id, Vocabulary v) => new() { Id = id, Name = id, Vocabulary = v };

    [Fact]
    public void Prompt_unions_global_and_matter_terms_deduped()
    {
        var global = new Vocabulary { Terms = new[] { "OAuth", "arraignment" } };
        var matter = MatterWith("M1", new Vocabulary { Terms = new[] { "arraignment", "Doe" } });   // dupe drops
        var vp = new VocabularyProvider(global, new Dictionary<string, Matter> { ["M1"] = matter });

        string prompt = vp.BuildInitialPrompt(new[] { "M1" });
        Assert.Equal("OAuth, arraignment, Doe", prompt);
    }

    [Fact]
    public void Prompt_is_bounded_by_max_tokens()
    {
        var global = new Vocabulary { Terms = new[] { "one", "two", "three", "four" } };
        var vp = new VocabularyProvider(global, new Dictionary<string, Matter>(), maxPromptTokens: 2);
        Assert.Equal("one, two", vp.BuildInitialPrompt(Array.Empty<string>()));
    }

    [Fact]
    public void Corrections_apply_whole_word_case_insensitive_and_matter_overrides_global()
    {
        var global = new Vocabulary { Corrections = new Dictionary<string, string> { ["auth"] = "OAuth" } };
        var matter = MatterWith("M1", new Vocabulary { Corrections = new Dictionary<string, string> { ["auth"] = "AUTH-OVERRIDE" } });
        var vp = new VocabularyProvider(global, new Dictionary<string, Matter> { ["M1"] = matter });

        // matter override wins; whole word only (authentication untouched); case-insensitive match
        Assert.Equal("AUTH-OVERRIDE and authentication",
            vp.ApplyCorrections("Auth and authentication", new[] { "M1" }));
    }

    [Fact]
    public void Corrections_with_punctuation_edged_keys_still_match()
    {
        // \b would silently never match "c#" (non-word edge); lookarounds must.
        var global = new Vocabulary { Corrections = new Dictionary<string, string> { ["c#"] = "C#" } };
        var vp = new VocabularyProvider(global, new Dictionary<string, Matter>());
        Assert.Equal("we use C# daily", vp.ApplyCorrections("we use c# daily", Array.Empty<string>()));
        Assert.Equal("c#x untouched", vp.ApplyCorrections("c#x untouched", Array.Empty<string>()));   // not whole-word
    }

    [Fact]
    public void No_vocab_is_identity()
    {
        var vp = new VocabularyProvider(new Vocabulary(), new Dictionary<string, Matter>());
        Assert.Equal("", vp.BuildInitialPrompt(Array.Empty<string>()));
        Assert.Equal("hello world", vp.ApplyCorrections("hello world", Array.Empty<string>()));
    }
}
