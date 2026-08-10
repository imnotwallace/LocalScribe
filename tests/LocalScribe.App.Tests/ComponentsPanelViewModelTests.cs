using System.IO;
using System.Linq;
using LocalScribe.App.Services;
using LocalScribe.App.ViewModels;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Records dispatched actions and runs them only when explicitly pumped, one turn at a
/// time. Deliberately duplicated per file (house convention: no cross-file test helper) -
/// mirrors DiarisationEngineGateTests. Lock-guarded because a fire-and-forget load's
/// pool-thread continuation can enqueue while the test thread is inside Pump; dequeue under the
/// lock, invoke outside it so a re-entrant dispatch cannot deadlock.</summary>
sealed class ComponentsQueuedDispatch
{
    private readonly object _gate = new();
    private readonly Queue<Action> _queue = new();
    public Action<Action> Dispatch => a => { lock (_gate) _queue.Enqueue(a); };
    public bool PumpOne()
    {
        Action next;
        lock (_gate)
        {
            if (_queue.Count == 0) return false;
            next = _queue.Dequeue();
        }
        next();
        return true;
    }
    public void Pump() { while (PumpOne()) { } }
}

/// <summary>The Settings Components panel (Tier 1 plan D, T1-10, 2026-08-05): installed/missing
/// state, size, and a Download button that runs the out-of-process fetch helper with progress
/// and resume. Every collaborator is injected, so this never reads the developer's real machine
/// or starts a real process.</summary>
public sealed class ComponentsPanelViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-comppanel-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly ComponentPin MediumPin =
        new("whisper-medium-en", "Whisper medium.en", "ggml-medium.en.bin",
            "https://example.invalid/m.bin", new string('a', 64), 1_000_000, "MIT");

    private sealed class ScriptedHelper : IComponentFetchHelper
    {
        public List<string> Lines = ["{\"type\":\"result\",\"path\":\"C:\\\\x\"}"];
        public int ExitCode;
        public int Runs;
        public Task<int> RunAsync(string jobJson, Action<string> onStdoutLine, CancellationToken ct)
        {
            Runs++;
            foreach (string line in Lines) onStdoutLine(line);
            return Task.FromResult(ExitCode);
        }
    }

    private (ComponentsPanelViewModel Vm, ComponentsQueuedDispatch D, ScriptedHelper H, FakeUiErrorReporter E)
        MakeVm(bool mediumPresent = false, IReadOnlyList<ComponentPin>? pins = null)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mediumPresent) present.Add("ggml-medium.en.bin");
        var probe = new ComponentProbe(
            resolveModel: name => Path.Combine(_root, name),
            findFfmpeg: () => null, findAssistant: () => null,
            diarizerExe: Path.Combine(_root, "LocalScribe.Diarizer.exe"),
            fileBytes: p => present.Contains(Path.GetFileName(p)) ? 42L : null);
        var helper = new ScriptedHelper();
        var dispatch = new ComponentsQueuedDispatch();
        var errors = new FakeUiErrorReporter();
        var vm = new ComponentsPanelViewModel(
            loadPins: _ => Task.FromResult(pins ?? (IReadOnlyList<ComponentPin>)[MediumPin]),
            probe, destPathFor: pin => Path.Combine(_root, pin.File),
            new ComponentFetchClient(helper), errors, dispatch.Dispatch);
        return (vm, dispatch, helper, errors);
    }

    [Fact]
    public async Task Rows_show_installed_state_a_human_size_and_a_download_button_only_where_a_pin_exists()
    {
        var (vm, d, _, _) = MakeVm();
        await vm.LastLoad;
        d.Pump();

        var medium = Assert.Single(vm.Rows, r => r.Id == "whisper-medium-en");
        Assert.False(medium.Installed);
        Assert.True(medium.CanDownload);
        Assert.Equal("1.0 MB", medium.SizeText);

        var assistant = Assert.Single(vm.Rows, r => r.Id == "assistant");
        Assert.False(assistant.CanDownload);            // probe-only: no pinned blob to fetch
        Assert.False(string.IsNullOrWhiteSpace(assistant.Detail));
    }

    [Fact]
    public async Task A_downloadable_row_states_its_licence_before_anything_is_fetched()
    {
        // The packaging design note (2026-08-06, decision 5) is explicit: the license field is
        // carried per model and must surface in the UI AT DOWNLOAD TIME - shipping Gemma weights
        // silently is a licensing question, not a technical one. A probe-only row has no licence
        // to state because there is nothing to fetch.
        var (vm, d, _, _) = MakeVm();
        await vm.LastLoad;
        d.Pump();

        Assert.Equal("MIT", Assert.Single(vm.Rows, r => r.Id == "whisper-medium-en").License);
        Assert.Null(Assert.Single(vm.Rows, r => r.Id == "ffmpeg").License);
    }

    // REMOVED 2026-08-11: An_installed_component_offers_no_download asserted exactly the rule that
    // made a corrupted model unrecoverable from the UI, and carried no rationale beyond restating
    // the implementation. Superseded by
    // An_installed_component_can_still_be_downloaded_again_so_a_corrupt_file_is_recoverable and
    // A_probe_only_row_still_offers_nothing_to_press, which keep the half of it that was real -
    // a row with no pinned blob still offers nothing.

    [Fact]
    public async Task A_download_reports_progress_and_re_probes_so_the_row_flips_to_installed()
    {
        var (vm, d, helper, errors) = MakeVm();
        await vm.LastLoad;
        d.Pump();
        var row = vm.Rows.First(r => r.Id == "whisper-medium-en");
        helper.Lines =
        [
            "{\"type\":\"progress\",\"bytes\":500000,\"totalBytes\":1000000}",
            "{\"type\":\"result\",\"path\":\"C:\\\\x\"}",
        ];

        await vm.DownloadCommand.ExecuteAsync(row);
        d.Pump();

        Assert.Equal(1, helper.Runs);
        Assert.False(row.IsDownloading);
        Assert.Equal(new[] { NoticeSeverity.Success }, errors.InfoSeverities);
    }

    [Fact]
    public async Task A_failed_download_reports_the_helpers_reason_and_leaves_the_row_not_installed()
    {
        var (vm, d, helper, errors) = MakeVm();
        await vm.LastLoad;
        d.Pump();
        var row = vm.Rows.First(r => r.Id == "whisper-medium-en");
        helper.ExitCode = 1;
        helper.Lines = ["{\"type\":\"error\",\"message\":\"SHA256 mismatch for m.bin - file deleted\"}"];

        await vm.DownloadCommand.ExecuteAsync(row);
        d.Pump();

        Assert.False(row.Installed);
        Assert.False(row.IsDownloading);
        Assert.Contains(errors.Reports, r => r.Ex.Message.Contains("SHA256 mismatch"));
    }

    [Fact]
    public async Task A_probe_only_row_cannot_be_downloaded_even_if_the_command_is_invoked()
    {
        // Belt and braces: the button is hidden, but a bound command must refuse anyway rather
        // than spawn a helper with a null url.
        var (vm, d, helper, _) = MakeVm();
        await vm.LastLoad;
        d.Pump();

        await vm.DownloadCommand.ExecuteAsync(vm.Rows.First(r => r.Id == "assistant"));

        Assert.Equal(0, helper.Runs);
    }

    [Fact]
    public async Task A_build_with_no_pin_manifest_still_renders_the_probe_only_rows()
    {
        var (vm, d, _, _) = MakeVm(pins: []);
        await vm.LastLoad;
        d.Pump();

        Assert.Equal(3, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.False(r.CanDownload));
    }

    [Fact]
    public void Sizes_render_invariantly_so_the_panel_reads_the_same_on_every_machine()
    {
        Assert.Equal("-", ComponentsPanelViewModel.FormatSize(0));
        Assert.Equal("1.0 MB", ComponentsPanelViewModel.FormatSize(1_000_000));
        Assert.Equal("3.2 GB", ComponentsPanelViewModel.FormatSize(3_190_000_000));
    }

    /// <summary>THE DEFECT (2026-08-11): "installed" is presence + a non-zero size, NOT a hash
    /// check - the probe deliberately never reads multi-gigabyte files. A corrupted or truncated
    /// model therefore reads as installed, and because the Download button was offered only while
    /// !Installed, that row then had NO button at all. The one state a user most needs to act on
    /// was the one state the panel refused to act on, and the recovery was to find and delete the
    /// file by hand.</summary>
    [Fact]
    public async Task An_installed_component_can_still_be_downloaded_again_so_a_corrupt_file_is_recoverable()
    {
        var (vm, d, _, _) = MakeVm(mediumPresent: true);
        await vm.LastLoad;
        d.Pump();

        var medium = Assert.Single(vm.Rows, r => r.Id == "whisper-medium-en");
        Assert.True(medium.Installed);
        Assert.True(medium.CanDownload);
    }

    [Fact]
    public async Task The_button_says_reinstall_once_a_component_is_present()
    {
        // Offering a button labelled "Download" against something already marked Installed reads
        // as a mistake; the label has to say what pressing it means.
        var (vm, d, _, _) = MakeVm(mediumPresent: true);
        await vm.LastLoad;
        d.Pump();

        Assert.Equal("Reinstall", Assert.Single(vm.Rows, r => r.Id == "whisper-medium-en").DownloadLabel);
    }

    [Fact]
    public async Task Download_label_is_download_when_nothing_is_installed()
    {
        var (vm, d, _, _) = MakeVm();
        await vm.LastLoad;
        d.Pump();

        Assert.Equal("Download", Assert.Single(vm.Rows, r => r.Id == "whisper-medium-en").DownloadLabel);
    }

    /// <summary>The guard was doubled: CanDownload hid the button AND RunDownloadAsync refused an
    /// installed row, so binding the command elsewhere would still have done nothing.</summary>
    [Fact]
    public async Task Re_downloading_an_installed_component_actually_runs_the_fetch_helper()
    {
        var (vm, d, helper, _) = MakeVm(mediumPresent: true);
        await vm.LastLoad;
        d.Pump();

        var medium = Assert.Single(vm.Rows, r => r.Id == "whisper-medium-en");
        vm.DownloadCommand.Execute(medium);
        await vm.LastDownload;
        d.Pump();

        Assert.Equal(1, helper.Runs);
    }

    [Fact]
    public async Task A_probe_only_row_still_offers_nothing_to_press()
    {
        // Widening the installed rule must not accidentally offer a Download for ffmpeg or a
        // helper exe, which have no pinned blob and could never be fetched.
        var (vm, d, _, _) = MakeVm();
        await vm.LastLoad;
        d.Pump();

        Assert.False(Assert.Single(vm.Rows, r => r.Id == "assistant").CanDownload);
    }

    [Fact]
    public async Task Refresh_re_probes_so_a_component_installed_outside_the_app_appears()
    {
        // RefreshCommand existed but was bound to nothing in XAML, which also made the smoke
        // runbook's "press Refresh" step unperformable.
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var probe = new ComponentProbe(
            resolveModel: name => Path.Combine(_root, name),
            findFfmpeg: () => null, findAssistant: () => null,
            diarizerExe: Path.Combine(_root, "LocalScribe.Diarizer.exe"),
            fileBytes: p => present.Contains(Path.GetFileName(p)) ? 42L : null);
        var dispatch = new ComponentsQueuedDispatch();
        var vm = new ComponentsPanelViewModel(
            loadPins: _ => Task.FromResult((IReadOnlyList<ComponentPin>)[MediumPin]),
            probe, destPathFor: pin => Path.Combine(_root, pin.File),
            new ComponentFetchClient(new ScriptedHelper()), new FakeUiErrorReporter(), dispatch.Dispatch);
        await vm.LastLoad;
        dispatch.Pump();
        Assert.False(Assert.Single(vm.Rows, r => r.Id == "whisper-medium-en").Installed);

        present.Add("ggml-medium.en.bin");          // arrived while the panel was open
        vm.RefreshCommand.Execute(null);
        await vm.LastLoad;
        dispatch.Pump();

        Assert.True(Assert.Single(vm.Rows, r => r.Id == "whisper-medium-en").Installed);
    }
}
