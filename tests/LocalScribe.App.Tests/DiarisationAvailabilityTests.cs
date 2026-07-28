using System.IO;
using LocalScribe.App.Services;
using LocalScribe.Core.Diarisation;
using Xunit;

namespace LocalScribe.App.Tests;

/// <summary>Pre-flight availability gate for speaker detection (design 2026-07-28 section 5).
/// LocalScribe.Diarizer.exe is deployed by NO build step (App.csproj:32-38 documents that a
/// same-folder copy would overwrite App's onnxruntime.dll 1.22 with sherpa's 1.24.4), and
/// ModelPaths.Resolve does no existence check (ModelPaths.cs:23), so the gate must probe for
/// itself. Without it, a missing helper surfaces as a raw Win32Exception from
/// ProcessDiarisationHelper.cs:33 AFTER transcription has already burned minutes.</summary>
public sealed class DiarisationAvailabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ls_avail_{Guid.NewGuid():N}");

    public DiarisationAvailabilityTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Models => Path.Combine(_root, "models");
    private string Resolve(string name) => Path.Combine(Models, name);
    private string Exe => Path.Combine(_root, "LocalScribe.Diarizer.exe");

    private void WriteModel(string name)
    {
        string p = Resolve(name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, [1, 2, 3]);
    }

    private void WriteExe() => File.WriteAllBytes(Exe, [1, 2, 3]);

    [Fact]
    public void Null_when_the_exe_and_both_models_are_present()
    {
        WriteExe();
        WriteModel(DiarisationModels.Segmentation);
        WriteModel(DiarisationModels.Embedding);

        Assert.Null(DiarisationAvailability.Probe(Resolve, Exe));
    }

    [Fact]
    public void Names_the_missing_helper_exe()
    {
        WriteModel(DiarisationModels.Segmentation);
        WriteModel(DiarisationModels.Embedding);

        string? reason = DiarisationAvailability.Probe(Resolve, Exe);
        Assert.NotNull(reason);
        Assert.Contains("LocalScribe.Diarizer.exe", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Names_a_missing_model()
    {
        WriteExe();
        WriteModel(DiarisationModels.Segmentation);   // embedding model deliberately absent

        string? reason = DiarisationAvailability.Probe(Resolve, Exe);
        Assert.NotNull(reason);
        Assert.Contains("model", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_zero_byte_file_does_not_count_as_present()
    {
        WriteExe();
        WriteModel(DiarisationModels.Segmentation);
        string p = Resolve(DiarisationModels.Embedding);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, []);

        Assert.NotNull(DiarisationAvailability.Probe(Resolve, Exe));
    }

    [Fact]
    public void The_segmentation_name_is_a_subpath_and_survives_Path_Combine()
    {
        // Deliberately a forward-slash subpath (sherpa ships the model inside a folder).
        Assert.Contains('/', DiarisationModels.Segmentation);
        Assert.EndsWith("model.onnx", DiarisationModels.Segmentation, StringComparison.Ordinal);
    }
}
