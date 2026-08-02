using LocalScribe.Core.Transcription;

public class WhisperModelCatalogTests
{
    [Fact]
    public void Describe_returns_the_user_approved_copy_for_the_recommended_model()
    {
        var info = WhisperModelCatalog.Describe("large-v3-turbo");
        Assert.Equal("large-v3-turbo", info.Name);
        Assert.Equal("Best accuracy at fast speed - recommended", info.Subtitle);
        Assert.Equal(0, info.Rank);           // best rank of the real models
        Assert.False(info.EnglishOnly);       // turbo is multilingual
    }

    [Fact]
    public void Describe_covers_every_fetchable_model_and_the_settings_auto_sentinel()
    {
        // Every stem fetch-models.ps1 covers (tiny/base/small/medium, .en variants) plus the
        // two large import models must carry a real subtitle - a cataloged model must never
        // render a bare row.
        string[] cataloged =
        [
            "tiny", "tiny.en", "base", "base.en", "small", "small.en",
            "medium", "medium.en", "large-v3", "large-v3-turbo",
        ];
        foreach (string name in cataloged)
        {
            var info = WhisperModelCatalog.Describe(name);
            Assert.Equal(name, info.Name);
            Assert.NotEqual("", info.Subtitle);
            Assert.InRange(info.Rank, 0, 9);
        }
        var auto = WhisperModelCatalog.Describe("auto");
        Assert.Equal("Choose automatically for this PC", auto.Subtitle);
        Assert.Equal(-1, auto.Rank);          // sentinel: sorts ahead but never in AvailableModels
    }

    [Fact]
    public void Describe_ranks_strictly_by_accuracy_and_flags_english_only_weights()
    {
        // Rank drives "best available on disk" defaults: lower = more accurate. Same accuracy
        // ordering as ModelLadder.Rungs, with the .en variant ranked just ahead of its
        // multilingual sibling and large-v3-turbo ahead of everything.
        string[] byRank =
        [
            "large-v3-turbo", "large-v3", "medium.en", "medium", "small.en",
            "small", "base.en", "base", "tiny.en", "tiny",
        ];
        for (int i = 0; i < byRank.Length; i++)
            Assert.Equal(i, WhisperModelCatalog.Describe(byRank[i]).Rank);
        foreach (string name in byRank)
            Assert.Equal(name.EndsWith(".en", StringComparison.Ordinal),
                WhisperModelCatalog.Describe(name).EnglishOnly);
    }

    [Fact]
    public void Describe_passes_unknown_names_through_with_worst_rank()
    {
        // OPEN-set hard rule: any user-dropped ggml file must stay selectable. Unknown names
        // get a passthrough entry (never a throw or filter), an empty subtitle, and the worst
        // Rank so a best-Rank default never prefers them over a cataloged model.
        var info = WhisperModelCatalog.Describe("distil-large-v3.5");
        Assert.Equal("distil-large-v3.5", info.Name);
        Assert.Equal("", info.Subtitle);
        Assert.Equal(int.MaxValue, info.Rank);
        Assert.False(info.EnglishOnly);

        Assert.True(WhisperModelCatalog.Describe("custom.en").EnglishOnly);   // ".en" convention
    }

    [Fact]
    public void DescribeAll_projects_ordinal_sorted_by_name()
    {
        // The shared picker projection keeps the Ordinal-by-name ordering all three pickers
        // already used, so pinned picker-content orderings survive the type change.
        var all = WhisperModelCatalog.DescribeAll(["small.en", "large-v3-turbo", "zz-custom"]);
        Assert.Equal(new[] { "large-v3-turbo", "small.en", "zz-custom" },
            all.Select(i => i.Name));
        Assert.Equal("Best accuracy at fast speed - recommended", all[0].Subtitle);
        Assert.Equal("", all[2].Subtitle);    // passthrough rides along
    }
}
