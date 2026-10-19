using LocalScribe.App.ViewModels;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>One opt-in remote endpoint card (design 2026-10-18): edits commit, turning remote on
/// raises the advisory exactly once, and the API key is write-only and only ever saved encrypted.</summary>
public sealed class RemoteEndpointSettingsViewModelTests
{
    private RemoteInferenceSetting _saved = new();
    private int _advisories;

    private RemoteEndpointSettingsViewModel Make(RemoteInferenceSetting? initial = null)
    {
        _saved = initial ?? new RemoteInferenceSetting();
        return new RemoteEndpointSettingsViewModel(_saved, change => _saved = change(_saved), () => _advisories++,
            [new(RemoteInferenceSetting.Local, "Local"), new(RemoteInferenceSetting.Remote, "Remote")]);
    }

    [Fact]
    public void Defaults_to_local_with_nothing_to_complain_about()
    {
        var vm = Make();
        Assert.Equal(RemoteInferenceSetting.Local, vm.Backend);
        Assert.False(vm.IsRemote);
        Assert.Null(vm.EndpointProblem);
        Assert.False(vm.HasApiKey);
    }

    [Fact]
    public void Turning_remote_on_commits_and_raises_the_advisory_once()
    {
        var vm = Make();
        vm.Backend = RemoteInferenceSetting.Remote;
        vm.Backend = RemoteInferenceSetting.Remote;    // same value: no second save, no second toast
        Assert.Equal(RemoteInferenceSetting.Remote, _saved.Backend);
        Assert.Equal(1, _advisories);

        vm.Backend = RemoteInferenceSetting.Local;
        Assert.Equal(RemoteInferenceSetting.Local, _saved.Backend);
        Assert.Equal(1, _advisories);
    }

    [Fact]
    public void The_view_reflects_an_edit_before_the_async_save_lands()
    {
        // The page's Commit swaps Settings.Current only after the disk write; the card must not
        // read back a stale value in between.
        var vm = new RemoteEndpointSettingsViewModel(new RemoteInferenceSetting(), _ => { }, () => { },
            [new(RemoteInferenceSetting.Local, "Local")]);
        vm.Endpoint = "  https://inference.example  ";
        Assert.Equal("https://inference.example", vm.Endpoint);
    }

    [Theory]
    [InlineData("", "Enter the endpoint")]
    [InlineData("inference.example", "http")]
    [InlineData("https://inference.example", null)]
    public void Endpoint_problems_show_only_while_remote_is_on(string endpoint, string? problem)
    {
        var vm = Make(new RemoteInferenceSetting { Backend = RemoteInferenceSetting.Remote });
        vm.Endpoint = endpoint;
        if (problem is null) Assert.Null(vm.EndpointProblem);
        else Assert.Contains(problem, vm.EndpointProblem);

        vm.Backend = RemoteInferenceSetting.Local;
        Assert.Null(vm.EndpointProblem);
    }

    [Fact]
    public void Plain_http_is_allowed_but_flagged_as_unencrypted()
    {
        var vm = Make(new RemoteInferenceSetting { Backend = RemoteInferenceSetting.Remote });
        vm.Endpoint = "http://inference.example:8080";
        Assert.True(vm.IsUnencrypted);
        vm.Endpoint = "https://inference.example";
        Assert.False(vm.IsUnencrypted);
    }

    [Fact]
    public void The_api_key_is_write_only_and_saved_encrypted()
    {
        var vm = Make();
        vm.ApiKeyEntry = "sk-secret";

        Assert.Equal("", vm.ApiKeyEntry);                       // never read back into the UI
        Assert.True(vm.HasApiKey);
        Assert.NotNull(_saved.ApiKeyProtected);
        Assert.DoesNotContain("sk-secret", _saved.ApiKeyProtected);
        Assert.Equal("sk-secret", ApiKeyProtection.Unprotect(_saved.ApiKeyProtected));

        vm.ApiKeyEntry = "   ";                                 // a blank box changes nothing
        Assert.True(vm.HasApiKey);

        Assert.True(vm.RemoveApiKeyCommand.CanExecute(null));
        vm.RemoveApiKeyCommand.Execute(null);
        Assert.Null(_saved.ApiKeyProtected);
        Assert.False(vm.HasApiKey);
        Assert.False(vm.RemoveApiKeyCommand.CanExecute(null));
    }
}
