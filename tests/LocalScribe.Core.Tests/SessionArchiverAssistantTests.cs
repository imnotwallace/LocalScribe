using System.IO.Compression;
using LocalScribe.Core.Storage;

namespace LocalScribe.Core.Tests;

public class SessionArchiverAssistantTests
{
    [Fact]
    public async Task Zip_includes_the_assistant_folder_automatically()
    {
        // Design 2026-07-18 section 7.3: zip archives include assistant\ as-is (clearly
        // separated). SessionArchiver walks AllDirectories, so this holds with NO archiver
        // change - this test pins that so a future rewrite cannot silently drop the folder.
        string dir = Directory.CreateTempSubdirectory("ls-arch-assist-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "transcript.jsonl"), "");
            Directory.CreateDirectory(Path.Combine(dir, "assistant"));
            File.WriteAllText(Path.Combine(dir, "assistant", "summaries.json"),
                "{\"schemaVersion\":1,\"versions\":[]}");

            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                await SessionArchiver.AddSessionFolderAsync(zip, dir, "s1/", CancellationToken.None);

            ms.Position = 0;
            using var read = new ZipArchive(ms, ZipArchiveMode.Read);
            Assert.NotNull(read.GetEntry("s1/assistant/summaries.json"));
            Assert.NotNull(read.GetEntry("s1/transcript.jsonl"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
