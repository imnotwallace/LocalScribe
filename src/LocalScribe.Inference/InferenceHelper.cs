using System.Text.Json.Nodes;

namespace LocalScribe.Inference;

/// <summary>The helper's request loop, apart from Console so tests can drive it end to end. Same
/// shape as LocalScribe.Assistant's Program: one AssistantWire request per stdin line (HelperWire),
/// JSON-lines events out, keepAlive keeps the process for the next request (the transcription
/// engine sends one request per segment). Every request must carry its "remote" block - this process has no
/// settings and no configuration of its own, and nothing to fall back to.
/// Exit 0 = clean; 1 = an error event was emitted before exiting.</summary>
internal sealed class InferenceHelper(HttpClient http, Action<HelperEvent> emit, Func<int, TimeSpan>? retryBackoff = null)
{
    public async Task<int> RunAsync(TextReader input, CancellationToken ct)
    {
        string? line;
        while ((line = await input.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var request = HelperWire.ParseRequest(line);
            if (request?.Remote is not { } remote)
            {
                emit(new Failure("BAD_REQUEST: stdin line is not a valid remote inference request"));
                return 1;
            }
            try
            {
                switch (request.Op)
                {
                    case "summarize" or "answer":
                        await ChatAsync(request, remote, ct);
                        break;
                    case HelperWire.TranscribeOp:
                        await TranscribeAsync(request, remote, ct);
                        break;
                    default:
                        emit(new Failure($"BAD_REQUEST: the remote helper does not run '{request.Op}' jobs"));
                        return 1;
                }
            }
            catch (RemoteInferenceException ex)
            {
                emit(new Failure("REMOTE_FAILED: " + ex.Message));
                return 1;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Full chain to STDERR (stdout is the wire), the Assistant helper's convention.
                Console.Error.WriteLine(ex.ToString());
                emit(new Failure("JOB_FAILED: " + ex.Message));
                return 1;
            }
            if (!request.KeepAlive) return 0;
        }
        return 0;
    }

    private async Task ChatAsync(HelperRequest request, RemoteEndpoint remote, CancellationToken ct)
    {
        JsonNode? payload = JsonNode.Parse(request.PayloadJson);
        string prompt = HelperWire.Str(payload?["prompt"])
            ?? throw new InvalidDataException("payload has no prompt");
        int maxTokens = HelperWire.Int(payload?["maxTokens"]) ?? 1024;

        emit(new Progress("remote", 0, 0));
        var client = new ChatCompletionClient(http, remote);
        int pieces = 0;
        await foreach (string piece in client.StreamAsync(prompt, maxTokens, ct))
        {
            emit(new Chunk(piece));
            pieces++;
        }
        emit(new Done(client.PromptTokens ?? 0, client.CompletionTokens ?? pieces, client.ServedModel ?? remote.Model));
    }

    private async Task TranscribeAsync(HelperRequest request, RemoteEndpoint remote, CancellationToken ct)
    {
        JsonNode? payload = JsonNode.Parse(request.PayloadJson);
        byte[] wav = Convert.FromBase64String(HelperWire.Str(payload?["audioWav"])
            ?? throw new InvalidDataException("payload has no audio"));
        var client = new TranscriptionClient(http, remote, retryBackoff);
        var result = await client.TranscribeAsync(wav, HelperWire.Str(payload?["language"]),
            HelperWire.Str(payload?["prompt"]),
            attempt => emit(new Progress("remote-retry", attempt, TranscriptionClient.MaxAttempts)), ct);
        emit(result);
        emit(new Done(0, 0, remote.Model));
    }
}
