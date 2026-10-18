using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalScribe.Core.Assistant;
using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;
namespace LocalScribe.App.ViewModels;

/// <summary>One row of a remote-inference Backend picker.</summary>
public sealed record InferenceBackendChoice(string Code, string Name);

/// <summary>One opt-in remote endpoint card in Settings (design 2026-10-18) - used twice, for the
/// assistant and for re-transcription. Every edit auto-saves through the page's Commit chain. The
/// value shown is this VM's own copy, updated synchronously on each edit, because the page's
/// Commit swaps Settings.Current only after the async disk write.
///
/// The API key is WRITE-ONLY here: typing one encrypts it (ApiKeyProtection, DPAPI) and saves the
/// blob; it is never decrypted back into the UI, so it cannot be read off the screen or out of the
/// visual tree. "Remove key" clears it.</summary>
public sealed partial class RemoteEndpointSettingsViewModel : ObservableObject
{
    private readonly Action<Func<RemoteInferenceSetting, RemoteInferenceSetting>> _commit;
    private readonly Action _turnedOn;
    private RemoteInferenceSetting _current;

    public RemoteEndpointSettingsViewModel(RemoteInferenceSetting initial,
        Action<Func<RemoteInferenceSetting, RemoteInferenceSetting>> commit, Action turnedOn,
        IReadOnlyList<InferenceBackendChoice> choices)
    {
        (_current, _commit, _turnedOn, BackendChoices) = (initial, commit, turnedOn, choices);
        RemoveApiKeyCommand = new RelayCommand(() => Update(r => r with { ApiKeyProtected = null }),
            () => HasApiKey);
    }

    public IReadOnlyList<InferenceBackendChoice> BackendChoices { get; }
    public IRelayCommand RemoveApiKeyCommand { get; }

    public string Backend
    {
        get => _current.Backend;
        set
        {
            if (value == _current.Backend) return;
            Update(r => r with { Backend = value });
            if (value == RemoteInferenceSetting.Remote) _turnedOn();
        }
    }

    public bool IsRemote => _current.Backend == RemoteInferenceSetting.Remote;

    public string Endpoint
    {
        get => _current.Endpoint;
        set => Update(r => r with { Endpoint = value.Trim() });
    }

    /// <summary>Shown under the endpoint while remote is on and the address cannot be used.</summary>
    public string? EndpointProblem => !IsRemote ? null
        : _current.Endpoint.Length == 0 ? "Enter the endpoint address - until then nothing is sent and jobs fail with this reason."
        : RemoteInference.ValidateEndpoint(_current.Endpoint);
    public bool HasEndpointProblem => EndpointProblem is not null;

    /// <summary>http:// is allowed (a private network may already encrypt the link) but said out
    /// loud: over the open internet it would carry transcripts and the API key in the clear.</summary>
    public bool IsUnencrypted => IsRemote && !HasEndpointProblem
        && _current.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    public string Model
    {
        get => _current.Model;
        set => Update(r => r with { Model = value.Trim() });
    }

    /// <summary>Write-only: the getter is always empty, so the box clears once a key is saved.</summary>
    public string ApiKeyEntry
    {
        get => "";
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            string? blob = ApiKeyProtection.Protect(value);
            Update(r => r with { ApiKeyProtected = blob });
        }
    }

    public bool HasApiKey => _current.ApiKeyProtected is not null;
    public string ApiKeyStatus => HasApiKey
        ? "A key is saved, encrypted for your Windows account. Type a new one to replace it."
        : "No key saved. Leave blank if the endpoint needs none.";

    private void Update(Func<RemoteInferenceSetting, RemoteInferenceSetting> change)
    {
        _current = change(_current);
        _commit(change);
        OnPropertyChanged(string.Empty);   // every derived flag/message depends on the whole record
        RemoveApiKeyCommand.NotifyCanExecuteChanged();
    }
}
