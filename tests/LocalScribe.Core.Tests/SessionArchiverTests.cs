// tests/LocalScribe.Core.Tests/SessionArchiverTests.cs
using System.IO.Compression;
using LocalScribe.Core.Storage;

public sealed class SessionArchiverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_{Guid.NewGuid():N}");
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private string Seed(params (string name, byte[] bytes)[] files)
    {
        string dir = Path.Combine(_root, "sessions", "s1");
        Directory.CreateDirectory(dir);
        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(dir, name), bytes);
        return dir;
    }

    [Fact]
    public async Task Archives_present_files_only_and_stores_audio_uncompressed()
    {
        // A .wav of all-zero bytes is highly compressible; NoCompression keeps CompressedLength==Length.
        string dir = Seed(
            ("session.json", "{}"u8.ToArray()),
            ("transcript.md", new byte[2048]),          // compressible text-side => Optimal shrinks it
            ("local.wav", new byte[4096]));             // audio => NoCompression (stored)

        string dest = Path.Combine(_root, "s1.zip");
        using (var fs = new FileStream(dest, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            await SessionArchiver.AddSessionFolderAsync(zip, dir, "", CancellationToken.None);

        using var read = ZipFile.OpenRead(dest);
        Assert.Equal(new[] { "local.wav", "session.json", "transcript.md" },
            read.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        var wav = read.Entries.Single(e => e.Name == "local.wav");
        Assert.Equal(wav.Length, wav.CompressedLength);                 // stored, not deflated
        var md = read.Entries.Single(e => e.Name == "transcript.md");
        Assert.True(md.CompressedLength < md.Length);                   // Optimal shrank it
    }

    [Fact]
    public async Task Missing_folder_adds_nothing()
    {
        Directory.CreateDirectory(_root);
        string dest = Path.Combine(_root, "empty.zip");
        using (var fs = new FileStream(dest, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            await SessionArchiver.AddSessionFolderAsync(zip, Path.Combine(_root, "nope"), "", CancellationToken.None);
        using var read = ZipFile.OpenRead(dest);
        Assert.Empty(read.Entries);
    }

    [Fact]
    public async Task Entry_prefix_nests_the_folder()
    {
        string dir = Seed(("meta.json", "{}"u8.ToArray()));
        string dest = Path.Combine(_root, "pref.zip");
        using (var fs = new FileStream(dest, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            await SessionArchiver.AddSessionFolderAsync(zip, dir, "s1/", CancellationToken.None);
        using var read = ZipFile.OpenRead(dest);
        Assert.Equal("s1/meta.json", read.Entries.Single().FullName);
    }
}
