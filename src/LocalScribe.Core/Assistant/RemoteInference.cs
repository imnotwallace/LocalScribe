using LocalScribe.Core.Model;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Assistant;

/// <summary>A resolved opt-in remote route (design 2026-10-18): the endpoint to use, or the reason
/// it cannot be used. A Problem is shown to the user as-is; it never falls back to local silently
/// (a user who opted into remote and got local - or the reverse - would be misled about where
/// their data went).</summary>
public sealed record RemoteRoute(RemoteEndpointConfig? Config, string? Problem);

/// <summary>Everything App and Core need to know about the opt-in remote helper, WITHOUT any network
/// code: App and Core never open a socket (NoNetworkInAppOrCoreTests). Remote requests are carried
/// out by LocalScribe.Inference.exe, a separate process the App starts only for a job the user
/// routed to a remote endpoint, speaking the AssistantWire stdio contract.</summary>
public static class RemoteInference
{
    public const string HelperExeName = "LocalScribe.Inference.exe";

    public const string MissingHelperMessage =
        "Remote inference is turned on, but the remote inference helper (" + HelperExeName + ") is not "
        + "deployed beside the app. Reinstall LocalScribe, or set LOCALSCRIBE_INFERENCE to a folder "
        + "containing it. Nothing was sent.";

    /// <summary>Settings resolution, re-read for EVERY job so a change in Settings applies to the
    /// next job without a restart. Null = the assistant runs locally.</summary>
    public static RemoteRoute? AssistantRoute(Settings settings)
    {
        var s = settings.Inference.Assistant;
        return s.IsRemote ? Resolve(s, s.Model) : null;
    }

    /// <summary>Null = remote re-transcription is not turned on. The model is required here: it
    /// names the transcript version the run produces, so "whatever the server defaults to" would
    /// leave the version without honest provenance.</summary>
    public static RemoteRoute? TranscriptionRoute(Settings settings, string? modelOverride)
    {
        var s = settings.Inference.Transcription;
        if (!s.IsRemote) return null;
        string model = string.IsNullOrWhiteSpace(modelOverride) ? s.Model : modelOverride;
        if (string.IsNullOrWhiteSpace(model))
            return new RemoteRoute(null,
                "Remote re-transcription needs a model name - set one in Settings > Remote inference "
                + "or in this dialog.");
        return Resolve(s, model);
    }

    private static RemoteRoute Resolve(RemoteInferenceSetting s, string model)
    {
        if (ValidateEndpoint(s.Endpoint) is { } invalid) return new RemoteRoute(null, invalid);
        string? key;
        try { key = ApiKeyProtection.Unprotect(s.ApiKeyProtected); }
        catch (InvalidOperationException ex) { return new RemoteRoute(null, ex.Message); }
        return new RemoteRoute(new RemoteEndpointConfig(s.Endpoint.Trim().TrimEnd('/'), model.Trim(), key), null);
    }

    /// <summary>Null when usable, else the reason. Only absolute http/https base URLs. Credentials
    /// in the URL are refused (they would be stored in plaintext and recorded in provenance - the
    /// key field exists for that), and so are query strings and fragments, which the helper would
    /// otherwise have to splice paths around.</summary>
    public static string? ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "The remote endpoint must be a full http:// or https:// address.";
        if (uri.UserInfo.Length > 0)
            return "Put the API key in the API key field, not in the endpoint address.";
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return "The remote endpoint must be a base address with no ? or # part.";
        return null;
    }

    /// <summary>scheme://host[:port] - the part of an endpoint recorded in provenance and shown in
    /// notices. The path is dropped: it says nothing about where the data went.</summary>
    public static string Origin(string endpoint)
        => Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out Uri? uri)
            ? uri.GetLeftPart(UriPartial.Authority)
            : endpoint.Trim();

    /// <summary>LOCALSCRIBE_INFERENCE (a folder containing the exe), else beside the app binary
    /// (build.ps1 copies it there, as it does LocalScribe.Fetch.exe), else tools\inference\ at the
    /// repo root for dev. Null when absent: a remote job then fails with MissingHelperMessage.</summary>
    public static string? FindHelperExe()
        => FindHelperExe(AppContext.BaseDirectory, Environment.GetEnvironmentVariable("LOCALSCRIBE_INFERENCE"));

    /// <summary>Testable core; production calls the parameterless overload.</summary>
    public static string? FindHelperExe(string baseDir, string? envOverride)
    {
        if (!string.IsNullOrEmpty(envOverride))
        {
            string fromEnv = Path.Combine(envOverride, HelperExeName);
            if (File.Exists(fromEnv)) return Path.GetFullPath(fromEnv);
        }

        string beside = Path.Combine(baseDir, HelperExeName);
        if (File.Exists(beside)) return beside;

        for (var d = new DirectoryInfo(baseDir); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "LocalScribe.slnx")))
            {
                string dev = Path.Combine(d.FullName, "tools", "inference", HelperExeName);
                return File.Exists(dev) ? dev : null;
            }
        return null;
    }
}
