// Out-of-process local-LLM helper (design 2026-07-18 section 7.1). Reads one JSON request
// PER STDIN LINE (unlike Diarizer's one-shot read-to-end: keepAlive chat sends further
// requests on the same stdin), streams JSON-lines events to stdout via the Core wire codec,
// and NEVER writes files or opens sockets. keepAlive:false exits after done; keepAlive:true
// stays resident, reusing the loaded model + KV prefix for follow-up "answer" requests.
// Exit 0 = clean; 1 = a job/protocol error was emitted before exiting.
using System.Text;
using LocalScribe.Assistant;
using LocalScribe.Core.Assistant;

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

LlamaEngine? engine = null;
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
            // First request loads the model (progress phases surface the slow parts);
            // keepAlive follow-ups reuse it - the warm-chat KV contract (design 7.1).
            engine ??= LlamaEngine.Load(request.ModelPath, request.CtxTokens, request.Backend,
                phase => Emit(new AssistantProgress(phase, 0, 0)));

            var (prompt, maxTokens) = LlamaEngine.ReadPayload(request.PayloadJson);
            Emit(new AssistantProgress("prefill", 0, 0));
            int outputTokens = 0;
            await foreach (string piece in engine.InferAsync(prompt, maxTokens))
            {
                Emit(new AssistantChunk(piece));
                outputTokens++;
            }
            Emit(new AssistantDone(engine.Backend, engine.LastPromptTokens, outputTokens));
        }
        catch (Exception ex)
        {
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
    engine?.Dispose();
}
