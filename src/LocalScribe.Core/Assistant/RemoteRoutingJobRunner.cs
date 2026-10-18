using System.Runtime.CompilerServices;
using LocalScribe.Core.Model;

namespace LocalScribe.Core.Assistant;

/// <summary>The one place an assistant job is routed local-or-remote (design 2026-10-18). Settings
/// are read per job, so a change applies to the next job. A remote route stamps the request with
/// Backend "remote" and the endpoint, and runs it on the remote helper; the helper's done event
/// then reports backend "remote" and the model that answered, which is what summaries and chats
/// record. Embeddings are never routed - semantic search stays local. A route that cannot be used
/// (bad endpoint, undecryptable key, missing helper) fails the job visibly: it never falls back to
/// the local model, and nothing is sent.</summary>
public sealed class RemoteRoutingJobRunner(IAssistantJobRunner local, IAssistantJobRunner? remote, Func<Settings> settings)
    : IAssistantJobRunner
{
    public async IAsyncEnumerable<AssistantEvent> RunAsync(AssistantRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        RemoteRoute? route = request.Op == "embed" ? null : RemoteInference.AssistantRoute(settings());
        IAsyncEnumerable<AssistantEvent> events;
        if (route is null)
            events = local.RunAsync(request, ct);
        else if (route.Problem is { } problem)
        {
            yield return new AssistantError(problem);
            yield break;
        }
        else if (remote is null)
        {
            yield return new AssistantError(RemoteInference.MissingHelperMessage);
            yield break;
        }
        else
            events = remote.RunAsync(request with { Backend = AssistantWire.RemoteBackend, Remote = route.Config }, ct);

        await foreach (var evt in events.WithCancellation(ct))
            yield return evt;
    }
}
