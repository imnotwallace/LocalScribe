using System.IO;
using System.Linq;
using LocalScribe.App.Services;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Installed/missing state for the Settings Components panel (Tier 1 plan D, T1-10,
/// 2026-08-05). Every probe already existed - this class only assembles them into rows, so it is
/// built entirely from injected delegates and never touches the developer's real machine.</summary>
public sealed class ComponentProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-comp-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static ComponentPin Pin(string id, string file, long bytes)
        => new(id, id, file, "https://example.invalid/" + file, new string('a', 64), bytes);

    /// <summary>presentFiles is a plain string[] rather than a params array: every call site here
    /// passes it BY NAME, and C# forbids a named argument in the expanded form of a params
    /// parameter (a bare `presentFiles: "x"` is CS1503, string to string[]).</summary>
    private ComponentProbe Make(bool ffmpeg = false, bool assistant = false,
        string[]? presentFiles = null)
    {
        var present = new HashSet<string>(presentFiles ?? [], StringComparer.OrdinalIgnoreCase);
        return new ComponentProbe(
            resolveModel: name => Path.Combine(_root, name),
            findFfmpeg: () => ffmpeg ? Path.Combine(_root, "ffmpeg") : null,
            findAssistant: () => assistant ? Path.Combine(_root, "assistant", "x.exe") : null,
            diarizerExe: Path.Combine(_root, "LocalScribe.Diarizer.exe"),
            fileBytes: path => present.Contains(Path.GetFileName(path)) ? 1234L : null);
    }

    [Fact]
    public void A_pinned_model_present_on_disk_reports_installed_with_its_real_size()
    {
        var rows = Make(presentFiles: ["ggml-medium.en.bin"])
            .Probe([Pin("whisper-medium-en", "ggml-medium.en.bin", 999)]);

        var row = Assert.Single(rows.Where(r => r.Id == "whisper-medium-en"));
        Assert.True(row.Installed);
        Assert.Equal(1234, row.Bytes);          // measured, not the manifest's figure
    }

    [Fact]
    public void A_pinned_model_absent_reports_missing_with_the_manifest_size_so_the_user_can_budget()
    {
        var rows = Make().Probe([Pin("whisper-medium-en", "ggml-medium.en.bin", 999)]);

        var row = Assert.Single(rows.Where(r => r.Id == "whisper-medium-en"));
        Assert.False(row.Installed);
        Assert.Equal(999, row.Bytes);
        Assert.NotNull(row.Pin);                // downloadable: the panel shows a Download button
    }

    [Fact]
    public void Ffmpeg_the_diarizer_and_the_assistant_are_probe_only_rows_with_no_pin()
    {
        // These three ship in the installer or via tools/fetch-ffmpeg.ps1 - there is no pinned
        // blob to fetch, so the panel must show state and a remedy, never a Download button that
        // cannot work.
        var rows = Make().Probe([]);

        foreach (string id in new[] { "ffmpeg", "diarizer", "assistant" })
        {
            var row = Assert.Single(rows.Where(r => r.Id == id));
            Assert.False(row.Installed);
            Assert.Null(row.Pin);
            Assert.False(string.IsNullOrWhiteSpace(row.Detail));   // a remedy, not a blank cell
        }
    }

    [Fact]
    public void A_present_helper_reports_installed_and_carries_no_remedy_text()
    {
        var rows = Make(ffmpeg: true).Probe([]);

        var ffmpeg = Assert.Single(rows.Where(r => r.Id == "ffmpeg"));
        Assert.True(ffmpeg.Installed);
        Assert.Null(ffmpeg.Detail);
    }

    [Fact]
    public void The_assistant_needs_BOTH_its_helper_and_its_model_before_it_reports_installed()
    {
        // build.ps1 publishes the helper into the installer but does NOT bundle its ~2.5 GB chat
        // model, so on a clean machine the exe is present and the feature cannot answer anything.
        // A row that reported "installed" off the exe alone would be a green light on a dead
        // feature, and the assistant smoke item would assert something that cannot pass.
        var chat = Pin(ComponentProbe.AssistantChatPinId, "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2_500_000_000);

        var row = Assert.Single(Make(assistant: true).Probe([chat]).Where(r => r.Id == "assistant"));

        Assert.False(row.Installed);
        Assert.Contains("model", row.Detail);         // says WHICH half is missing
        Assert.Contains(chat.Name, row.Detail);       // and names the row that fixes it
    }

    [Fact]
    public void The_assistant_reports_installed_once_helper_and_model_are_both_present()
    {
        var chat = Pin(ComponentProbe.AssistantChatPinId, "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 2_500_000_000);

        var row = Assert.Single(
            Make(assistant: true, presentFiles: ["Qwen3-4B-Instruct-2507-Q4_K_M.gguf"])
                .Probe([chat]).Where(r => r.Id == "assistant"));

        Assert.True(row.Installed);
        Assert.Null(row.Detail);
    }

    [Fact]
    public void An_empty_manifest_still_yields_the_three_probe_only_rows()
    {
        // A build that shipped without component-manifest.json must still render a useful panel.
        Assert.Equal(3, Make().Probe([]).Count);
    }
}
