using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pins tools/verify-no-foreign-natives.ps1, the build.ps1 gate that refuses to package
/// native binaries for a platform the installer can never run on (2026-10-21).
///
/// WHY THIS EXISTS: the 0.9.2 installer carried ~570 MB that no Windows x64 machine can load -
/// LLamaSharp.Backend.Cuda12's linux sub-package (506 MB of .so), Whisper.net.Runtime.Vulkan's
/// linux-x64 set (56 MB) and Whisper.net.Runtime's win-x86 and win-arm64 CPU builds. All three
/// packages copy their natives through their own build props/targets rather than as NuGet runtime
/// assets, so `dotnet publish -r win-x64` never prunes them, and every existing publish guard only
/// checks that the REQUIRED files are present, never that foreign ones are absent.
///
/// These facts RUN the script against small fake publish trees instead of grepping its text: the
/// layouts mirror the two real ones (LLamaSharp's runtimes/&lt;rid&gt;/native/&lt;variant&gt;/ and
/// Whisper.net's runtimes/&lt;variant&gt;/&lt;rid&gt;/), with one-byte stand-ins for the binaries.</summary>
public sealed class ForeignNativesGuardTests : IDisposable
{
    private const string Rid = "win-x64";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ls-foreign-natives-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>A publish tree holding only what a win-x64 install really ships, in both vendor
    /// layouts, including the GPU variant folders (cuda, vulkan, cuda12) that sit beside or
    /// above the RID folder and must NOT be mistaken for a platform.</summary>
    private string WinX64Publish()
    {
        foreach (string rel in new[]
                 {
                     "LocalScribe.App.exe",
                     "runtimes/win-x64/whisper.dll",
                     "runtimes/cuda/win-x64/ggml-cuda-whisper.dll",
                     "runtimes/vulkan/win-x64/ggml-vulkan-whisper.dll",
                     "assistant/LocalScribe.Assistant.exe",
                     "assistant/runtimes/win-x64/native/avx2/llama.dll",
                     "assistant/runtimes/win-x64/native/cuda12/ggml-cuda.dll",
                     "models/ggml-base.en.bin",
                     "ffmpeg/ffmpeg.exe",
                 })
            Touch(rel);
        return _root;
    }

    private void Touch(string rel)
    {
        string path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    [Fact]
    public void A_publish_holding_only_the_target_platform_passes()
    {
        // Positive control. Without it a script that rejects every runtimes\ folder - or treats
        // "cuda"/"vulkan" as a platform name - would satisfy every failing fact below.
        (int exit, string output) = RunGuard(WinX64Publish());

        Assert.True(exit == 0, "expected a clean win-x64 publish to pass:\n" + output);
    }

    [Theory]
    // LLamaSharp layout: the RID is the first folder under runtimes\. The 506 MB one.
    [InlineData("assistant/runtimes/linux-x64/native/cuda12/libggml-cuda.so", "assistant/runtimes/linux-x64")]
    // Whisper.net layout: the RID sits UNDER a GPU variant folder.
    [InlineData("runtimes/vulkan/linux-x64/libggml-vulkan-whisper.so", "runtimes/vulkan/linux-x64")]
    // Other WINDOWS architectures. These are .dll files, so a check on file extension alone
    // cannot see them - only recognising the folder as another platform's RID can. An x64
    // process never loads an arm64 or x86 dll.
    [InlineData("runtimes/win-arm64/whisper.dll", "runtimes/win-arm64")]
    [InlineData("runtimes/win-x86/whisper.dll", "runtimes/win-x86")]
    [InlineData("assistant/runtimes/osx-arm64/native/libllama.dylib", "assistant/runtimes/osx-arm64")]
    public void A_runtimes_folder_for_another_platform_fails_and_is_named(string file, string reported)
    {
        WinX64Publish();
        Touch(file);

        (int exit, string output) = RunGuard(_root);

        Assert.True(exit != 0, "expected the guard to fail on " + file + ":\n" + output);
        Assert.Contains(reported, output);
    }

    [Fact]
    public void A_linux_library_outside_any_runtimes_folder_is_caught_too()
    {
        // A flattened layout (the regression verify-assistant-publish.ps1 was written for) would
        // put a .so straight beside the exe, under no RID folder at all. No Windows build can load
        // an ELF or Mach-O library wherever it sits.
        WinX64Publish();
        Touch("assistant/libggml-cuda.so");

        (int exit, string output) = RunGuard(_root);

        Assert.True(exit != 0, "expected the guard to fail on a loose .so:\n" + output);
        Assert.Contains("assistant/libggml-cuda.so", output);
    }

    [Fact]
    public void A_missing_publish_directory_fails_rather_than_passing_vacuously()
    {
        // A mistyped path in build.ps1 must not read as "no foreign binaries found".
        (int exit, string output) = RunGuard(Path.Combine(_root, "does-not-exist"));

        Assert.True(exit != 0, "expected a missing publish directory to fail:\n" + output);
    }

    /// <summary>Runs the guard the way build.ps1 does, in Windows PowerShell 5.1 - present on
    /// every Windows machine and on the windows-latest CI runner - so the script must stay
    /// 5.1-compatible.</summary>
    private static (int Exit, string Output) RunGuard(string publishDir)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[]
                 {
                     "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                     "-File", Path.Combine(RepoPaths.SolutionRoot(), "tools", "verify-no-foreign-natives.ps1"),
                     "-PublishDir", publishDir,
                     "-Rid", Rid,
                 })
            psi.ArgumentList.Add(arg);

        var sb = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (sb) { sb.AppendLine(e.Data); } } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (sb) { sb.AppendLine(e.Data); } } };

        Assert.True(proc.Start(), "could not start powershell.exe");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        try
        {
            // Same rule as BuildOutputGuardTests (F9): the Assert THROWS on timeout and `using`
            // disposes without killing, so kill explicitly or a wedged run outlives the test.
            Assert.True(proc.WaitForExit(60_000), "the guard did not exit within 60s");
            proc.WaitForExit(); // flushes the async readers
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        }

        lock (sb)
        {
            return (proc.ExitCode, sb.ToString());
        }
    }
}
