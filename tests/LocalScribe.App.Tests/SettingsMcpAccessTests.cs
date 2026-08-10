using System.IO;
using System.Text.Json;
using LocalScribe.App.ViewModels;
using LocalScribe.Core.Audio;
using LocalScribe.Core.Live;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using LocalScribe.Core.Tests;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Settings' "MCP Access" section (MCP server round, Task 10): the ONLY writer of
/// mcp/consent.json. Default dark - a fresh install has no consent file and nothing is exposed.
/// Enabling requires an explicit confirm; disabling never does, and disabling must not clear the
/// remembered allowlist (re-enabling must never silently expose MORE than before - the list is
/// preserved, only exposure toggles).
///
/// A synchronous dispatch fake is used (the SettingsPageViewModelTests precedent), since these
/// tests assert on saved-to-disk state, not on the ordering of a queued dispatch.</summary>
public sealed class SettingsMcpAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ls-setmcp-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSettingsService _settings;
    private readonly FakeUiErrorReporter _errors = new();
    private readonly List<string> _confirmPrompts = new();
    private bool _confirmAnswer = true;

    private StoragePaths Paths => new(Path.Combine(_root, "storage"));

    public SettingsMcpAccessTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "models"));
        _settings = new FakeSettingsService(new Settings { StorageRoot = Path.Combine(_root, "storage") });
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private SettingsPageViewModel MakeVm()
    {
        var paths = Paths;
        var maintenance = new Services.MaintenanceService(paths, _settings, new FakeRecycleBin(), TimeProvider.System);
        return new SettingsPageViewModel(_settings, maintenance, new FakeLaunchAtLogin(),
            pickFolder: () => null, openFolder: _ => { }, _errors,
            dispatch: a => a(), new FakeCaptureDeviceEnumerator(),
            modelsRoot: Path.Combine(_root, "models"),
            assistantHelperProbe: () => null,
            confirmMcpEnable: message => { _confirmPrompts.Add(message); return _confirmAnswer; });
    }

    private async Task<SettingsPageViewModel> LoadedVmAsync()
    {
        var vm = MakeVm();
        await vm.McpLoad;
        return vm;
    }

    private Task SeedMatterAsync(string id, string name)
        => new MatterStore(Paths.MattersDir).CreateAsync(new Matter
        {
            Id = id,
            Name = name,
            DateCreatedUtc = DateTimeOffset.UnixEpoch,
        });

    private async Task<JsonDocument> ReadConsentJsonAsync()
    {
        string text = await File.ReadAllTextAsync(Paths.McpConsentJson);
        return JsonDocument.Parse(text);
    }

    [Fact]
    public async Task Enabling_mcp_requires_confirm_and_writes_consent_json()
    {
        var vm = await LoadedVmAsync();

        vm.McpEnabled = true;
        await vm.McpSave;

        Assert.Single(_confirmPrompts);
        Assert.Equal(SettingsPageViewModel.McpEnableWarning, _confirmPrompts[0]);
        Assert.True(File.Exists(Paths.McpConsentJson));

        using var doc = await ReadConsentJsonAsync();
        Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Declining_the_confirm_leaves_mcp_disabled_and_writes_nothing()
    {
        _confirmAnswer = false;
        var vm = await LoadedVmAsync();

        vm.McpEnabled = true;
        await vm.McpSave;

        Assert.Single(_confirmPrompts);
        Assert.False(vm.McpEnabled);
        Assert.False(File.Exists(Paths.McpConsentJson));
    }

    [Fact]
    public async Task Ticking_a_matter_updates_allowed_matter_ids()
    {
        await SeedMatterAsync("m1", "Smith v. Jones");
        await SeedMatterAsync("m2", "Doe v. Roe");

        var vm = await LoadedVmAsync();
        vm.McpEnabled = true;
        await vm.McpSave;

        Assert.Equal(2, vm.McpMatters.Count);
        vm.McpMatters[0].IsAllowed = true;
        await vm.McpSave;

        using var doc = await ReadConsentJsonAsync();
        var ids = doc.RootElement.GetProperty("allowed_matter_ids").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { vm.McpMatters[0].Id }, ids);
    }

    [Fact]
    public async Task Snippet_contains_exe_path_and_storage_root()
    {
        var vm = await LoadedVmAsync();
        string root = Paths.Root;
        // Was Path.Combine(AppContext.BaseDirectory, "LocalScribe.Mcp.exe") - which pinned the
        // DEFECT rather than the requirement, and is why it survived: the assertion was written to
        // match the implementation. The server is published into <app>\mcp\, and no exe is deployed
        // beside a test host, so the snippet must name the shipping path.
        string exe = Path.Combine(AppContext.BaseDirectory, "mcp", "LocalScribe.Mcp.exe");

        Assert.Contains("LocalScribe.Mcp.exe", vm.McpConfigSnippet);
        // JsonSerializer escapes backslashes in the raw text, so compare against the SAME escaped
        // form ("contains the storage root" - not a literal, unescaped Windows path substring).
        string escapedRoot = JsonSerializer.Serialize(root).Trim('"');
        Assert.Contains(escapedRoot, vm.McpConfigSnippet);

        using var doc = JsonDocument.Parse(vm.McpConfigSnippet);
        var localscribe = doc.RootElement.GetProperty("mcpServers").GetProperty("localscribe");
        Assert.Equal(exe, localscribe.GetProperty("command").GetString());
        var args = localscribe.GetProperty("args").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "--storage-root", root }, args);
    }

    /// <summary>Review finding (IMPORTANT): a failed consent save must never leave the UI claiming
    /// LESS exposure than what the server is actually still enforcing. Consent is already
    /// enabled with a matter allowlisted on disk; the save that would disable it is forced to
    /// throw (the tmp-file rename step can never complete because the tmp PATH is pre-occupied
    /// by a directory - the same "make the target a directory so File.WriteAllTextAsync throws"
    /// seam MetadataEditorViewModelTests uses). The VM must revert McpEnabled back to what
    /// consent.json actually still says (true), not leave it showing the optimistic false.</summary>
    [Fact]
    public async Task Failed_consent_save_reverts_the_ui_to_what_is_actually_persisted()
    {
        await SeedMatterAsync("m1", "Smith v. Jones");
        var vm = await LoadedVmAsync();
        vm.McpEnabled = true;
        await vm.McpSave;
        vm.McpMatters[0].IsAllowed = true;
        await vm.McpSave;
        Assert.Empty(_errors.Reports);           // both prior saves succeeded cleanly

        // Block every future save: pre-create the atomic-write tmp path as a DIRECTORY, so
        // McpConsentStore.SaveAsync's File.WriteAllTextAsync(tmp, ...) throws.
        Directory.CreateDirectory(Paths.McpConsentJson + ".tmp");

        vm.McpEnabled = false;                   // attempt to disable - this save WILL fail
        await vm.McpSave;

        // RED (pre-fix): this failed with vm.McpEnabled == false - the checkbox claimed
        // revoked while consent.json on disk still said enabled:true underneath it.
        Assert.True(vm.McpEnabled);
        Assert.NotEmpty(_errors.Reports);

        using var doc = await ReadConsentJsonAsync();
        Assert.True(doc.RootElement.GetProperty("enabled").GetBoolean());
        var ids = doc.RootElement.GetProperty("allowed_matter_ids").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { vm.McpMatters[0].Id }, ids);   // allowlist untouched too
    }

    /// <summary>Mirror of the above for the enable direction: with no consent file at all, force
    /// the very first save to fail (pre-occupy the mcp/ directory itself with a FILE, so
    /// McpConsentStore.SaveAsync's Directory.CreateDirectory(paths.McpDir) throws). The UI must
    /// end up back at disabled - never showing an enabled state that was never actually
    /// persisted - and consent.json must still not exist.</summary>
    [Fact]
    public async Task Failed_consent_save_on_enable_leaves_the_ui_disabled()
    {
        var vm = await LoadedVmAsync();
        Directory.CreateDirectory(Paths.Root);
        await File.WriteAllTextAsync(Paths.McpDir, "not a directory");

        vm.McpEnabled = true;                    // confirm accepted, but the save WILL fail
        await vm.McpSave;

        Assert.Single(_confirmPrompts);
        Assert.False(vm.McpEnabled);
        Assert.False(File.Exists(Paths.McpConsentJson));
        Assert.NotEmpty(_errors.Reports);
    }

    [Fact]
    public async Task Disabling_writes_enabled_false_but_keeps_the_allowlist()
    {
        await SeedMatterAsync("m1", "Smith v. Jones");

        var vm = await LoadedVmAsync();
        vm.McpEnabled = true;
        await vm.McpSave;
        vm.McpMatters[0].IsAllowed = true;
        await vm.McpSave;

        _confirmPrompts.Clear();
        vm.McpEnabled = false;
        await vm.McpSave;

        Assert.Empty(_confirmPrompts);                 // disabling is never confirm-gated
        using var doc = await ReadConsentJsonAsync();
        Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        var ids = doc.RootElement.GetProperty("allowed_matter_ids").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { vm.McpMatters[0].Id }, ids);   // preserved, not cleared
    }

    /// <summary>THE DEFECT (2026-08-11): the snippet named &lt;app&gt;\LocalScribe.Mcp.exe, but
    /// build.ps1 publishes the server into &lt;app&gt;\mcp\ (it is self-contained, so it cannot share
    /// the app's own root without its runtime colliding). Every user who pressed "Copy config" got
    /// a command pointing at a path that does not exist on any installed machine, and the failure
    /// surfaces inside the MCP client - as "server failed to start" - not in LocalScribe, so it is
    /// close to undiagnosable from here. Nothing tested this property, which is how it shipped.</summary>
    [Fact]
    public void The_config_snippet_names_the_mcp_subfolder_the_installer_publishes_into()
    {
        var vm = MakeVm();

        using var doc = JsonDocument.Parse(vm.McpConfigSnippet);
        string command = doc.RootElement.GetProperty("mcpServers")
            .GetProperty("localscribe").GetProperty("command").GetString()!;

        Assert.EndsWith(Path.Combine("mcp", "LocalScribe.Mcp.exe"), command);
    }

    /// <summary>The snippet must stay copyable when the server is not deployed (a source build
    /// does not publish it): it then names the place the exe OUGHT to be, the ModelPaths.ResolveRoot
    /// precedent, rather than emitting an empty command the client would fail on cryptically.</summary>
    [Fact]
    public void The_config_snippet_still_names_a_path_when_the_server_is_not_deployed()
    {
        var vm = MakeVm();

        using var doc = JsonDocument.Parse(vm.McpConfigSnippet);
        string command = doc.RootElement.GetProperty("mcpServers")
            .GetProperty("localscribe").GetProperty("command").GetString()!;

        Assert.False(string.IsNullOrWhiteSpace(command));
        Assert.True(Path.IsPathRooted(command));
    }
}
