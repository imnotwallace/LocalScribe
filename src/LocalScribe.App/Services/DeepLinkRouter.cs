using LocalScribe.Core.DeepLink;
using LocalScribe.Core.Live;

namespace LocalScribe.App.Services;

/// <summary>What the wiring should do with a routed deep link (design 2026-07-18 section 4).</summary>
public enum DeepLinkActionKind
{
    /// <summary>Run the EXACT manual start path (SessionViewModel.StartCommand) with Title prefilled.</summary>
    StartRecording,
    /// <summary>Show the confirm toast ([Stop recording] [Keep recording]); ONLY the explicit
    /// click stops - the deep link itself never does (evidentiary rule).</summary>
    ConfirmStop,
    /// <summary>start while a session is active/finalizing: notification toast, no action.</summary>
    NotifyAlreadyRecording,
    /// <summary>stop while idle/finalizing: notification toast, no action.</summary>
    NotifyNotRecording,
    /// <summary>Invalid link: do nothing; Reason (a fixed parser constant) is the ONLY loggable
    /// artifact - the URL and query are never logged.</summary>
    Ignore,
}

public sealed record DeepLinkDecision(DeepLinkActionKind Kind, string? Title = null, string? Reason = null);

/// <summary>Pure deep-link policy: parse result + session state -> decision. The state read is a
/// snapshot; the executing side re-checks the command's own CanExecute gate, which stays the
/// authority if a manual action raced the dispatch.</summary>
public static class DeepLinkRouter
{
    public static DeepLinkDecision Route(DeepLinkResult result, SessionState state) => result switch
    {
        DeepLinkResult.StartRecording s when state == SessionState.Idle
            => new(DeepLinkActionKind.StartRecording, Title: s.SanitizedName),
        DeepLinkResult.StartRecording => new(DeepLinkActionKind.NotifyAlreadyRecording),
        DeepLinkResult.StopRecording when state is SessionState.Recording or SessionState.Paused
            => new(DeepLinkActionKind.ConfirmStop),
        DeepLinkResult.StopRecording => new(DeepLinkActionKind.NotifyNotRecording),
        DeepLinkResult.Invalid i => new(DeepLinkActionKind.Ignore, Reason: i.Reason),
        _ => new(DeepLinkActionKind.Ignore, Reason: "unknown result"),
    };
}
