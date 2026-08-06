using System.Text.Json;
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>The parser for the fetch helper's stdout (Tier 1 plan D, T1-10, 2026-08-05). Split
/// out of the process object exactly the way SherpaHelperDiariser is split out of
/// ProcessDiarisationHelper: the wire contract is testable over a scripted fake, and the real
/// child process is smoke-only.</summary>
public sealed class ComponentFetchClientTests
{
    private static readonly ComponentPin Pin =
        new("m", "Medium", "ggml-medium.en.bin", "https://example.invalid/m.bin", new string('a', 64), 400);

    private sealed class ScriptedHelper(int exitCode, params string[] lines) : IComponentFetchHelper
    {
        public string? JobJson;
        public Task<int> RunAsync(string jobJson, Action<string> onStdoutLine, CancellationToken ct)
        {
            JobJson = jobJson;
            foreach (string line in lines) onStdoutLine(line);
            return Task.FromResult(exitCode);
        }
    }

    private sealed class Collector : IProgress<ComponentFetchProgress>
    {
        public List<ComponentFetchProgress> Seen { get; } = new();
        public void Report(ComponentFetchProgress value) => Seen.Add(value);
    }

    [Fact]
    public async Task The_job_is_serialized_with_the_camelCase_names_the_helper_expects()
    {
        var helper = new ScriptedHelper(0, "{\"type\":\"result\",\"path\":\"C:\\\\x\"}");
        await new ComponentFetchClient(helper).FetchAsync(Pin, "C:\\x", new Collector(), default);

        var job = JsonDocument.Parse(helper.JobJson!).RootElement;
        Assert.Equal("https://example.invalid/m.bin", job.GetProperty("url").GetString());
        Assert.Equal("C:\\x", job.GetProperty("destPath").GetString());
        Assert.Equal(new string('a', 64), job.GetProperty("sha256").GetString());
        Assert.Equal(400, job.GetProperty("expectedBytes").GetInt64());
    }

    [Fact]
    public async Task Progress_lines_are_forwarded_as_a_fraction()
    {
        var progress = new Collector();
        var helper = new ScriptedHelper(0,
            "{\"type\":\"progress\",\"bytes\":100,\"totalBytes\":400}",
            "{\"type\":\"progress\",\"bytes\":400,\"totalBytes\":400}",
            "{\"type\":\"result\",\"path\":\"C:\\\\x\"}");

        await new ComponentFetchClient(helper).FetchAsync(Pin, "C:\\x", progress, default);

        Assert.Equal(2, progress.Seen.Count);
        Assert.Equal(0.25, progress.Seen[0].Fraction, 3);
        Assert.Equal(1.0, progress.Seen[1].Fraction, 3);
    }

    [Fact]
    public async Task An_error_line_becomes_an_exception_carrying_the_helpers_own_message()
    {
        var helper = new ScriptedHelper(1,
            "{\"type\":\"error\",\"message\":\"SHA256 mismatch for m.bin - file deleted\"}");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ComponentFetchClient(helper).FetchAsync(Pin, "C:\\x", new Collector(), default));

        Assert.Contains("SHA256 mismatch", ex.Message);
    }

    [Fact]
    public async Task A_nonzero_exit_with_no_error_line_still_fails_rather_than_reporting_success()
    {
        // Fail closed: a helper that dies without saying why must NOT leave the panel showing a
        // component as installed.
        var helper = new ScriptedHelper(9);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ComponentFetchClient(helper).FetchAsync(Pin, "C:\\x", new Collector(), default));

        Assert.Contains("9", ex.Message);
    }

    [Fact]
    public async Task A_zero_exit_with_no_result_line_fails_too()
    {
        var helper = new ScriptedHelper(0);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ComponentFetchClient(helper).FetchAsync(Pin, "C:\\x", new Collector(), default));
    }

    [Fact]
    public async Task An_unparseable_line_is_ignored_rather_than_failing_the_download()
    {
        // The child writes only JSON, but a native runtime warning could still reach stdout.
        // Losing a 2.5 GB download to a stray line would be absurd.
        var helper = new ScriptedHelper(0, "warning: something", "{\"type\":\"result\",\"path\":\"C:\\\\x\"}");

        await new ComponentFetchClient(helper).FetchAsync(Pin, "C:\\x", new Collector(), default);
    }
}
