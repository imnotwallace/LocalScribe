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
    private readonly FakeSettingsService _settings = new(new Settings());
    private readonly FakeUiErrorReporter _errors = new();
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static readonly AssistantModelInfo Qwen4B =
        new("Qwen3-4B-Instruct-2507", @"C:\m\q4b.gguf", new string('a', 64), 262144, "Apache-2.0");
    private static readonly AssistantModelInfo Qwen17 =
        new("Qwen3-1.7B-Instruct", @"C:\m\q17.gguf", new string('b', 64), 32768, "Apache-2.0");

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
            assistantHelperProbe: assistantHelperProbe);
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
}
