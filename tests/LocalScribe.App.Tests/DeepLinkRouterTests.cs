using LocalScribe.App.Services;
using LocalScribe.Core.DeepLink;
using LocalScribe.Core.Live;
using Xunit;

namespace LocalScribe.App.Tests;

public class DeepLinkRouterTests
{
    // Design 2026-07-18 section 4 semantics, pure and exhaustive. The stop verb NEVER routes to a
    // direct stop - only ever to a confirm decision (evidentiary rule: a drive-by webpage can
    // invoke a registered scheme; stopping a recording must not be silently triggerable).

    [Fact]
    public void Start_while_idle_starts_and_carries_the_sanitized_title()
    {
        Assert.Equal(new DeepLinkDecision(DeepLinkActionKind.StartRecording, Title: "Client intake"),
            DeepLinkRouter.Route(new DeepLinkResult.StartRecording("Client intake"), SessionState.Idle));
        Assert.Equal(new DeepLinkDecision(DeepLinkActionKind.StartRecording, Title: null),
            DeepLinkRouter.Route(new DeepLinkResult.StartRecording(null), SessionState.Idle));
    }

    [Theory]
    [InlineData(SessionState.Recording)]
    [InlineData(SessionState.Paused)]
    [InlineData(SessionState.Finalizing)]
    public void Start_while_busy_only_notifies(SessionState state)
        => Assert.Equal(new DeepLinkDecision(DeepLinkActionKind.NotifyAlreadyRecording),
            DeepLinkRouter.Route(new DeepLinkResult.StartRecording("x"), state));

    [Theory]
    [InlineData(SessionState.Recording)]
    [InlineData(SessionState.Paused)]
    public void Stop_while_active_asks_for_confirmation_never_stops(SessionState state)
        => Assert.Equal(new DeepLinkDecision(DeepLinkActionKind.ConfirmStop),
            DeepLinkRouter.Route(new DeepLinkResult.StopRecording(), state));

    [Theory]
    [InlineData(SessionState.Idle)]
    [InlineData(SessionState.Finalizing)]
    public void Stop_while_not_recording_only_notifies(SessionState state)
        => Assert.Equal(new DeepLinkDecision(DeepLinkActionKind.NotifyNotRecording),
            DeepLinkRouter.Route(new DeepLinkResult.StopRecording(), state));

    [Fact]
    public void Invalid_is_ignored_and_carries_only_the_fixed_reason()
        => Assert.Equal(new DeepLinkDecision(DeepLinkActionKind.Ignore, Reason: "wrong scheme"),
            DeepLinkRouter.Route(new DeepLinkResult.Invalid("wrong scheme"), SessionState.Idle));

    [Theory]
    [InlineData(SessionState.Recording)]
    [InlineData(SessionState.Paused)]
    [InlineData(SessionState.Finalizing)]
    public void Invalid_is_ignored_regardless_of_session_state(SessionState state)
        // The Invalid arm of Route's switch never inspects state - unlike Start/Stop, an
        // off-allowlist link is always ignored, busy or not.
        => Assert.Equal(new DeepLinkDecision(DeepLinkActionKind.Ignore, Reason: "wrong scheme"),
            DeepLinkRouter.Route(new DeepLinkResult.Invalid("wrong scheme"), state));
}
