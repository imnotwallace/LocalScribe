using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Filename-template expansion (design 2026-08-04 section 6). Pure string work; the
/// token VALUES come from MaintenanceService.FilenameTokensAsync.</summary>
public sealed class ExportFileNamesTests
{
    private static readonly Dictionary<string, string> Tokens = new(StringComparer.Ordinal)
    {
        ["title"] = "Doe intake", ["date"] = "2026-07-03", ["time"] = "0900",
        ["matter"] = "Doe v Roe (2026/014)", ["version"] = "v2", ["id"] = "s1",
    };

    private static Dictionary<string, string> Untagged()
    {
        var t = new Dictionary<string, string>(Tokens, StringComparer.Ordinal);
        t["matter"] = "";
        return t;
    }

    [Fact]
    public void Every_token_expands()
    {
        Assert.Equal("Doe intake", ExportFileNames.Expand("{title}", Tokens));
        Assert.Equal("2026-07-03", ExportFileNames.Expand("{date}", Tokens));
        Assert.Equal("0900", ExportFileNames.Expand("{time}", Tokens));
        Assert.Equal("Doe v Roe (2026/014)", ExportFileNames.Expand("{matter}", Tokens));
        Assert.Equal("v2", ExportFileNames.Expand("{version}", Tokens));
        Assert.Equal("s1", ExportFileNames.Expand("{id}", Tokens));
    }

    [Fact]
    public void An_unknown_token_is_left_literal_so_the_user_sees_the_typo()
    {
        Assert.Equal("Doe intake {ttle}", ExportFileNames.Expand("{title} {ttle}", Tokens));
    }

    [Fact]
    public void An_empty_token_swallows_the_separator_run_that_followed_it()
    {
        Assert.Equal("Doe intake", ExportFileNames.Expand("{matter}-{title}", Untagged()));
        Assert.Equal("Doe intake", ExportFileNames.Expand("{matter} - {title}", Untagged()));
        Assert.Equal("Doe intake", ExportFileNames.Expand("{title}-{matter}", Untagged()));
    }

    [Fact]
    public void Intentional_separators_between_non_empty_tokens_survive()
    {
        Assert.Equal("2026-07-03 - Doe intake",
            ExportFileNames.Expand("{date} - {title}", Tokens));
        Assert.Equal("2026-07-03_Doe intake", ExportFileNames.Expand("{date}_{title}", Tokens));
    }

    [Fact]
    public void A_template_expanding_to_nothing_falls_back_through_sanitize()
    {
        Assert.Equal("", ExportFileNames.Expand("{matter}", Untagged()));
        Assert.Equal("export", ExportFileNames.Sanitize(ExportFileNames.Expand("{matter}", Untagged())));
    }

    [Fact]
    public void Sanitize_still_runs_last_over_an_expanded_matter()
    {
        // Legal matter references commonly contain '/', which is exactly why Sanitize exists.
        Assert.Equal("Doe v Roe (2026_014)",
            ExportFileNames.Sanitize(ExportFileNames.Expand("{matter}", Tokens)));
    }

    [Fact]
    public void The_default_template_reproduces_the_pre_round_2_filename()
    {
        Assert.Equal(ExportFileNames.Sanitize("Doe intake"),
            ExportFileNames.Sanitize(ExportFileNames.Expand("{title}", Tokens)));
    }
}
