using LocalScribe.Core.DeepLink;
using Xunit;

namespace LocalScribe.Core.Tests;

public class DeepLinkParserTests
{
    // Design 2026-07-18 section 4: the parser is an untrusted-input boundary (a drive-by webpage
    // can invoke a registered scheme). Steno's contract adopted wholesale: never throws, typed
    // reject with a fixed reason, two-verb allowlist, sanitized name, query never logged.

    [Fact]
    public void Valid_start_without_name_parses()
        => Assert.Equal(new DeepLinkResult.StartRecording(null),
            DeepLinkParser.Parse("localscribe://record/start"));

    [Fact]
    public void Valid_start_with_name_returns_the_decoded_name()
        => Assert.Equal(new DeepLinkResult.StartRecording("Client intake"),
            DeepLinkParser.Parse("localscribe://record/start?name=Client%20intake"));

    [Fact]
    public void Valid_stop_parses_and_ignores_any_query()
    {
        Assert.Equal(new DeepLinkResult.StopRecording(),
            DeepLinkParser.Parse("localscribe://record/stop"));
        // Unknown/extra params are ignored, never logged, never a reject reason.
        Assert.Equal(new DeepLinkResult.StopRecording(),
            DeepLinkParser.Parse("localscribe://record/stop?name=x&foo=bar"));
    }

    [Fact]
    public void Scheme_host_path_and_query_key_are_case_insensitive()
    {
        Assert.Equal(new DeepLinkResult.StopRecording(),
            DeepLinkParser.Parse("LOCALSCRIBE://RECORD/STOP"));
        Assert.Equal(new DeepLinkResult.StartRecording("hi"),
            DeepLinkParser.Parse("LocalScribe://Record/Start?NAME=hi"));
    }

    [Fact]
    public void Trailing_slash_is_tolerated()
        => Assert.Equal(new DeepLinkResult.StartRecording(null),
            DeepLinkParser.Parse("localscribe://record/start/"));

    [Fact]
    public void Name_keeps_the_allowlisted_punctuation_and_unicode_letters()
    {
        // Kept: letters/marks/digits + . , ( ) @ & ' ! + # -   Dropped (to space): the colon.
        Assert.Equal(new DeepLinkResult.StartRecording("Smith v. Jones (depo) @Court #142 A&B O'Neil! -1"),
            DeepLinkParser.Parse(
                "localscribe://record/start?name=Smith%20v.%20Jones%20(depo)%20%40Court%20%23142%3A%20A%26B%20O'Neil!%20-1"));
        // Accented letters survive (Unicode letter categories, written as escapes - project rule).
        Assert.Equal(new DeepLinkResult.StartRecording("Café Müller"),
            DeepLinkParser.Parse("localscribe://record/start?name=Caf%C3%A9%20M%C3%BCller"));
    }

    [Fact]
    public void Plus_is_a_kept_literal_never_a_space()
        // Steno contract: '+' is on the keep list; Uri.UnescapeDataString semantics (no
        // application/x-www-form-urlencoded plus-to-space rewriting).
        => Assert.Equal(new DeepLinkResult.StartRecording("one+two"),
            DeepLinkParser.Parse("localscribe://record/start?name=one+two"));

    [Fact]
    public void Control_chars_and_injection_shapes_become_collapsed_spaces()
    {
        Assert.Equal(new DeepLinkResult.StartRecording("a b c d"),
            DeepLinkParser.Parse("localscribe://record/start?name=a%09b%00c%0D%0Ad"));
        var r = Assert.IsType<DeepLinkResult.StartRecording>(DeepLinkParser.Parse(
            "localscribe://record/start?name=..%2F..%2Fetc%2Fpasswd%22%3B%20DROP%20TABLE"));
        // Slashes, quotes, and semicolons never survive into a session title.
        Assert.Equal(".. .. etc passwd DROP TABLE", r.SanitizedName);
        Assert.DoesNotContain("/", r.SanitizedName);
        Assert.DoesNotContain("\"", r.SanitizedName);
        Assert.DoesNotContain(";", r.SanitizedName);
    }

    [Fact]
    public void Overlong_name_is_capped_at_120_chars()
    {
        var r = Assert.IsType<DeepLinkResult.StartRecording>(DeepLinkParser.Parse(
            "localscribe://record/start?name=" + new string('a', 200)));
        Assert.Equal(new string('a', 120), r.SanitizedName);
    }

    [Fact]
    public void Name_that_sanitizes_to_nothing_becomes_null()
        // Only dropped chars (slashes) -> spaces -> collapse -> empty -> null.
        => Assert.Equal(new DeepLinkResult.StartRecording(null),
            DeepLinkParser.Parse("localscribe://record/start?name=%2F%2F%2F"));

    [Theory]
    [InlineData("https://record/start")]                    // wrong scheme
    [InlineData("localscribe://transcript/start")]          // unknown host
    [InlineData("localscribe://record/pause")]              // verb not on the allowlist
    [InlineData("localscribe://record")]                    // no verb
    [InlineData("localscribe://record/start/extra")]        // extra path segment
    [InlineData("localscribe:record/start")]                // opaque form, no authority
    [InlineData("not a url")]
    [InlineData("")]
    public void Everything_off_the_allowlist_is_a_typed_reject(string url)
    {
        var invalid = Assert.IsType<DeepLinkResult.Invalid>(DeepLinkParser.Parse(url));
        Assert.False(string.IsNullOrWhiteSpace(invalid.Reason));
        // The reason is a fixed constant - it never echoes the input (query-never-logged contract).
        if (url.Length > 0) Assert.DoesNotContain(url, invalid.Reason);
    }

    [Fact]
    public void Parse_never_throws_even_on_null()
        => Assert.IsType<DeepLinkResult.Invalid>(DeepLinkParser.Parse(null!));

    [Fact]
    public void Malformed_percent_escapes_never_throw_and_sanitize_literally()
        // Uri.UnescapeDataString leaves un-decodable escapes ('%ZZ' has no hex digits after it,
        // '%2' is a truncated escape) as literal chars in the decoded string; sanitization then
        // drops the bare '%' (not on the keep list) to a space, keeping the surviving letters/digits.
        => Assert.Equal(new DeepLinkResult.StartRecording("ZZ 2"),
            DeepLinkParser.Parse("localscribe://record/start?name=%ZZ%2"));

    [Fact]
    public void Name_of_only_combining_marks_survives_the_keep_list()
        // %CC%81 / %CC%82 are the UTF-8 bytes for U+0301 / U+0302 (combining acute/circumflex) -
        // category NonSpacingMark, which is on the sanitizer's keep list. Neither is whitespace,
        // so nothing collapses them away.
        => Assert.Equal(new DeepLinkResult.StartRecording("\u0301\u0302"),
            DeepLinkParser.Parse("localscribe://record/start?name=%CC%81%CC%82"));

    [Fact]
    public void Whitespace_only_name_collapses_to_null()
        // Decodes to two spaces; the collapse+trim pass reduces an all-whitespace name to empty,
        // and empty-after-sanitize is null (never an empty-string title).
        => Assert.Equal(new DeepLinkResult.StartRecording(null),
            DeepLinkParser.Parse("localscribe://record/start?name=%20%20"));
}
