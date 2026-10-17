// Out-of-process local-LLM helper (design 2026-07-18 section 7.1). Reads one JSON request
// PER STDIN LINE (unlike Diarizer's one-shot read-to-end: keepAlive chat sends further
// requests on the same stdin), streams JSON-lines events to stdout via the Core wire codec,
// and NEVER writes files or opens sockets. keepAlive:false exits after done; keepAlive:true
// stays resident, reusing the loaded model + KV prefix for follow-up "answer" requests.
// Exit 0 = clean; 1 = a job/protocol error was emitted before exiting.
//
// Task 5 (2026-10-18): inference backend is selected via environment variables passed by the
// parent process (CompositionRoot). The assistant process may import LocalScribe.Inference; the
// no-network contract only restricts LocalScribe.App and LocalScribe.Core.
using System.Text;
using LocalScribe.Assistant;
using LocalScribe.Core.Assistant;
using LocalScribe.Inference;

// UTF-8 (no BOM) on BOTH pipes so non-ASCII in transcripts / model output survives the wire
// unmangled (Task-7 review M2). The App side pins the matching StandardInput/StandardOutput
// encoding in ProcessAssistantHelper; both ends must agree. No BOM: a leading BOM would break
// the first request-line parse. Set before any read/write touches the redirected streams.
Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var stdout = Console.Out;
void Emit(AssistantEvent evt)
{
    stdout.WriteLine(AssistantWire.SerializeEvent(evt));
    stdout.Flush();   // the App reads line-by-line; never let events sit in the buffer
}

// Read inference backend config from environment (set by CompositionRoot at startup).
var backend = Environment.GetEnvironmentVariable("INFERENCE_BACKEND") ?? "local";
var endpoint = Environment.GetEnvironmentVariable("INFERENCE_ENDPOINT") ?? "";
var apiKey = Environment.GetEnvironmentVariable("INFERENCE_APIKEY") ?? "";
var model = Environment.GetEnvironmentVariable("INFERENCE_MODEL") ?? "";

// Mutable variables: updated each loop iteration from the request. Used by the local
// provider's lazy-load engine factory (called once on first InferAsync).
string? requestModelPath = null;
int requestCtxTokens = 0;
string requestBackend = "auto";

IInferenceProvider inferenceProvider;
if (backend == "remote" && !string.IsNullOrEmpty(endpoint))
{
    // Create the remote provider upfront so any configuration errors surface at startup, not
    // during the first request (design: named failures, no silent fallback).
    inferenceProvider = InferenceProviderFactory.CreateRemote(
        endpoint: endpoint,
        apiKey: apiKey,
        model: model,
        timeout: TimeSpan.FromMinutes(5));
}
else
{
    // Local backend: create the provider. Model loading is lazy (happens on first InferAsync).
    // The engineFactory callback creates LlamaEngine with the request's model path, ctx tokens,
    // and backend. These values come from the mutable captured variables updated each loop
    // iteration. ConfigureNativeLoad is called once by the engine factory on first inference.
    inferenceProvider = InferenceProviderFactory.CreateLocal(() =>
    {
        LlamaEngine.ConfigureNativeLoad(requestBackend);
        return LlamaEngine.Load(requestModelPath ?? "", requestCtxTokens, requestBackend,
            phase => Emit(new AssistantProgress(phase, 0, 0)));
    });
}

// EmbedEngine is still used for embedding requests (not affected by the inference provider).
EmbedEngine? embedEngine = null;

try
{
    string? line;
    while ((line = await Console.In.ReadLineAsync()) is not null)
    {
        if (string.IsNullOrWhiteSpace(line)) continue;
        var request = AssistantWire.ParseRequestLine(line);
        if (request is null)
        {
            Emit(new AssistantError("BAD_REQUEST: stdin line is not a valid assistant request"));
            return 1;
        }
        try
        {
            // Capture request config for the lazy-load engine factory.
            requestModelPath = request.ModelPath;
            requestCtxTokens = request.CtxTokens;
            requestBackend = request.Backend;

            if (request.Op == "embed")
            {
                // Embedding shares the process shell but never the chat engine - the 4B LLM is
                // NOT loaded for embed requests (design 2026-07-25 memory rule).
                embedEngine ??= EmbedEngine.Load(request.ModelPath,
                    phase => Emit(new AssistantProgress(phase, 0, 0)));
                var (kind, texts, dim) = EmbedEngine.ReadPayload(request.PayloadJson);
                var vectors = await embedEngine.EmbedAsync(kind, texts, dim, CancellationToken.None);
                Emit(new LocalScribe.Core.Assistant.AssistantEmbedResult(vectors,
                    LocalScribe.Core.Search.Semantic.EmbeddingMethod.For(request.ModelPath,
                        dim > 0 ? dim : (vectors.FirstOrDefault()?.Length ?? 0))));
                Emit(new AssistantDone("cpu", 0, texts.Count));
                if (!request.KeepAlive) return 0;
                continue;
            }

            // Route inference through IInferenceProvider (remote or local).
            // The provider was created at startup; model loading is lazy (first InferAsync).
            Emit(new AssistantProgress("loading", 0, 0));
            // Parse the request payload to get the prompt text and maxTokens.
            var (promptText, maxTokens) = LlamaEngine.ReadPayload(request.PayloadJson);
            var requestMessages = new List<Message>
            {
                new Message("user", promptText),
            };
            var inferenceResult = await inferenceProvider.InferAsync(
                new InferenceRequest(requestMessages, 0.7, maxTokens, new ProviderOptions(
                    endpoint, apiKey, model, TimeSpan.FromMinutes(5))),
                CancellationToken.None);
            // Stream the result as chunks (same shape as LlamaEngine's async enumeration).
            int outputTokens = 0;
            var text = inferenceResult.Text;
            int chunkSize = 1;
            for (int i = 0; i < text.Length; i += chunkSize)
            {
                int end = Math.Min(i + chunkSize, text.Length);
                Emit(new AssistantChunk(text.Substring(i, end - i)));
                outputTokens++;
            }
            // Use provider token counts (remote returns accurate counts; local approximates).
            int promptTokens = inferenceResult.PromptTokens > 0
                ? inferenceResult.PromptTokens
                : text.Length / 4;
            int completionTokens = inferenceResult.CompletionTokens > 0
                ? inferenceResult.CompletionTokens
                : outputTokens;
            Emit(new AssistantDone(
                backend == "remote" ? "remote" : "cpu",
                promptTokens,
                completionTokens));
        }
        catch (Exception ex)
        {
            // Full chain to STDERR (never stdout - that is the wire): native-load failures
            // surface as a bare TypeInitializationException on Message alone; the inner
            // chain is what actually names the missing DLL (2026-07-23 diagnosis).
            Console.Error.WriteLine(ex.ToString());
            Emit(new AssistantError("JOB_FAILED: " + ex.Message));
            return 1;
        }
        if (!request.KeepAlive) return 0;
    }
    return 0;
}
catch (Exception ex)
{
    Emit(new AssistantError("HELPER_CRASH: " + ex.Message));
    return 1;
}
finally
{
    await inferenceProvider.DisposeAsync();
    embedEngine?.Dispose();
}
