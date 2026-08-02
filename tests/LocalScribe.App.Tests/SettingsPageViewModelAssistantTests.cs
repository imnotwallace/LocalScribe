using System.IO;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

public sealed class SettingsPageViewModelAssistantTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-set-assist-").FullName;
    private readonly FakeSettingsService _settings;
    private readonly FakeUiErrorReporter _errors = new();

    // Hermetic isolation (review finding): the VM ctor unconditionally runs LoadMcpAsync, which
    // reads mcp/consent.json and matters/matters.json off StorageRoot. A default
    // Settings().StorageRoot resolves to the REAL %USERPROFILE%/LocalScribe, so this suite would
    // otherwise touch the developer's real legal-transcript matter index even though it never
    // exercises MCP itself.
    public SettingsPageViewModelAssistantTests()
        => _settings = new FakeSettingsService(new Settings { StorageRoot = Path.Combine(_root, "storage") });

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly AssistantModelInfo Qwen4B =
        new("Qwen3-4B-Instruct-2507", @"C:\m\q4b.gguf", new string('a', 64), 262144, "Apache-2.0");
    private static readonly AssistantModelInfo Qwen17 =
        new("Qwen3-1.7B-Instruct", @"C:\m\q17.gguf", new string('b', 64), 32768, "Apache-2.0");
    private static readonly AssistantModelInfo EmbedModel =
        new("bge-small-en", @"C:\m\bge-small-en.gguf", new string('c', 64), 512, "MIT", Role: "embedding");

    private SettingsPageViewModel MakeVm(AssistantManifestCache? cache = null,
        Func<string?>? assistantHelperProbe = null)
    {
        var maintenance = new Services.MaintenanceService(
            new StoragePaths(Path.Combine(_root, "storage")), _settings, new FakeRecycleBin(),
            TimeProvider.System);
        return new SettingsPageViewModel(_settings, maintenance, new FakeLaunchAtLogin(),
            pickFolder: () => null, openFolder: _ => { }, _errors,
            dispatch: a => a(), new FakeCaptureDeviceEnumerator(),
            modelsRoot: Path.Combine(_root, "models"), assistantModels: cache,
            // Deterministic default (Task 5 review finding 2): without this, an unspecified probe
            // falls through to the real AssistantHelperLocator.FindExe() and the real filesystem
            // (including the repo tools\assistant\ dev fallback), making the suite machine-dependent.
            assistantHelperProbe: assistantHelperProbe ?? (() => null));
    }

    [Fact]
    public void Assistant_helper_note_reports_present_and_absent_separately_from_models()
    {
        var present = MakeVm(assistantHelperProbe: () => @"C:\app\assistant\LocalScribe.Assistant.exe");
        Assert.Contains(@"C:\app\assistant\LocalScribe.Assistant.exe", present.AssistantHelperNote);

        var absent = MakeVm(assistantHelperProbe: () => null);
        Assert.Contains("dotnet publish src/LocalScribe.Assistant", absent.AssistantHelperNote);
    }

    [Fact]
    public void Assistant_helper_note_is_not_frozen_at_construction()
    {
        // Task 5 review finding (IMPORTANT): the Assistant tab and the assistant chat both
        // re-probe the helper live on every use, so a helper deployed after startup works
        // immediately there. If Settings freezes the note at construction, it contradicts what
        // the user just watched work elsewhere - the exact "one surface lies about another"
        // defect this test pins against.
        string? path = null;
        var vm = MakeVm(assistantHelperProbe: () => path);
        Assert.Equal(AssistantHelperLocator.MissingMessage, vm.AssistantHelperNote);

        path = @"C:\app\assistant\LocalScribe.Assistant.exe";
        vm.RefreshAssistantHelperNote();
        Assert.Contains(path, vm.AssistantHelperNote);
    }

    [Fact]
    public async Task Toggle_and_model_pick_persist_via_the_commit_pattern()
    {
        var cache = new AssistantManifestCache(_ => Task.FromResult(
            new AssistantModelManifest([Qwen4B, Qwen17], Qwen4B, [])));
        var vm = MakeVm(cache);
        await vm.AssistantModelsLoad;

        vm.AssistantEnabled = false;
        await vm.LastSave;
        Assert.False(_settings.Current.Assistant.Enabled);

        vm.AssistantModel = "Qwen3-1.7B-Instruct";
        await vm.LastSave;
        Assert.Equal("Qwen3-1.7B-Instruct", _settings.Current.Assistant.Model);

        // Picking the locked default stores null (the "no explicit pick" sentinel).
        vm.AssistantModel = "Qwen3-4B-Instruct-2507";
        await vm.LastSave;
        Assert.Null(_settings.Current.Assistant.Model);
        Assert.Equal("Qwen3-4B-Instruct-2507", vm.AssistantModel);   // getter echoes the default
    }

    [Fact]
    public async Task Installed_models_populate_the_picker()
    {
        var cache = new AssistantManifestCache(_ => Task.FromResult(
            new AssistantModelManifest([Qwen4B, Qwen17], Qwen4B, [])));
        var vm = MakeVm(cache);
        await vm.AssistantModelsLoad;
        Assert.Equal(new[] { "Qwen3-4B-Instruct-2507", "Qwen3-1.7B-Instruct" },
            vm.AssistantModelChoices);
        Assert.True(vm.HasAssistantModels);
        Assert.Equal("", vm.AssistantModelsNote);
    }

    [Fact]
    public async Task Embedding_model_is_excluded_from_the_chat_picker()
    {
        // Task 10 sub-fix (controller-adjudicated): AssistantModelManifest.Installed now mixes
        // chat and embedding roles - a manifest with both must offer ONLY the chat model here.
        var cache = new AssistantManifestCache(_ => Task.FromResult(
            new AssistantModelManifest([Qwen4B, EmbedModel], Qwen4B, [], EmbedModel)));
        var vm = MakeVm(cache);
        await vm.AssistantModelsLoad;
        Assert.Equal(new[] { "Qwen3-4B-Instruct-2507" }, vm.AssistantModelChoices);
        Assert.DoesNotContain("bge-small-en", vm.AssistantModelChoices);
        Assert.True(vm.HasAssistantModels);
    }

    [Fact]
    public async Task No_model_shows_fetch_instructions_and_disables_the_picker()
    {
        // Design 7.6: fetch instructions when no model is present; features off with explainer.
        var vm = MakeVm(new AssistantManifestCache(_ => Task.FromResult(
            new AssistantModelManifest([], null, []))));
        await vm.AssistantModelsLoad;
        Assert.False(vm.HasAssistantModels);
        Assert.Contains("fetch-models.ps1 -Assistant", vm.AssistantModelsNote);
        Assert.Contains("Qwen3-4B-Instruct-2507", vm.AssistantModelsNote);
    }

    [Fact]
    public async Task Picker_displays_the_first_installed_chat_model_when_the_saved_default_is_not_installed()
    {
        // UX round 2026-08-02 item 3.1: the user never picked a model (Assistant.Model == null),
        // so the getter returns the locked default name - but only Qwen3-1.7B is installed. Core
        // resolves this exact situation to chat.FirstOrDefault() (AssistantModels.cs:87-88), so
        // the app RUNS Qwen3-1.7B while the picker painted blank. Display must agree with Core.
        var cache = new AssistantManifestCache(_ => Task.FromResult(
            new AssistantModelManifest([Qwen17], Qwen17, [])));
        var vm = MakeVm(cache);
        await vm.AssistantModelsLoad;

        Assert.Equal("Qwen3-1.7B-Instruct", vm.AssistantModel);
        Assert.Contains(vm.AssistantModel, vm.AssistantModelChoices);
        // Display-coerce ONLY: page-open never rewrites settings.json (evidentiary rule).
        Assert.Equal(0, _settings.SaveCount);
        Assert.Null(_settings.Current.Assistant.Model);
    }

    [Fact]
    public async Task Picker_keeps_the_saved_name_before_load_and_when_no_chat_model_is_installed()
    {
        // Before the manifest scan lands the choices are empty - the getter must still return a
        // non-null string (the box is disabled via HasAssistantModels until then, so the
        // transient state is a DISABLED box, never an enabled blank one).
        var vm = MakeVm(new AssistantManifestCache(_ => Task.FromResult(
            new AssistantModelManifest([], null, []))));
        Assert.Equal("Qwen3-4B-Instruct-2507", vm.AssistantModel);   // at construction

        await vm.AssistantModelsLoad;
        Assert.Equal("Qwen3-4B-Instruct-2507", vm.AssistantModel);   // empty manifest: unchanged
        Assert.False(vm.HasAssistantModels);
        Assert.Equal(0, _settings.SaveCount);
    }
}
