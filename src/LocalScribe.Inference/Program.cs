// LocalScribe.Inference.exe - the opt-in remote inference helper (design 2026-10-18). The App
// starts it ONLY for a job the user routed to a remote endpoint they configured (an assistant job
// with Settings > Remote inference on, or a re-transcription where they picked Remote), and never
// otherwise. It speaks the App's AssistantWire contract on stdio (HelperWire), makes the HTTP
// calls, and writes no files. With remote inference off it never runs, so no running LocalScribe
// process holds network code - the property the App/Core grep (NoNetworkInAppOrCoreTests) protects.
using System.Text;
using LocalScribe.Inference;

// UTF-8 (no BOM) on both pipes, as LocalScribe.Assistant does: the App's ProcessAssistantHelper pins
// the same encoding on its side, and a BOM would corrupt the first request line.
Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var stdout = Console.Out;
void Emit(HelperEvent evt)
{
    stdout.WriteLine(HelperWire.Serialize(evt));
    stdout.Flush();   // the App reads line by line
}

using var http = RemoteHttp.CreateClient();
try
{
    // Cancellation is a process kill from the App (the AssistantJobRunner contract).
    return await new InferenceHelper(http, Emit).RunAsync(Console.In, CancellationToken.None);
}
catch (Exception ex)
{
    Emit(new Failure("HELPER_CRASH: " + ex.Message));
    return 1;
}
