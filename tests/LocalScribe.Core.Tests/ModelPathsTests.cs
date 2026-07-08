using LocalScribe.Core.Transcription;

public class ModelPathsTests
{
    [Fact]
    public void Env_override_wins()
    {
        string prev = Environment.GetEnvironmentVariable("LOCALSCRIBE_MODELS") ?? "";
        try
        {
            Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS", @"C:\mlmodels");
            Assert.Equal(@"C:\mlmodels\silero_vad.onnx", ModelPaths.Resolve("silero_vad.onnx"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS",
                prev.Length == 0 ? null : prev);
        }
    }

    [Fact]
    public void Default_root_ends_with_models_and_is_absolute()
    {
        string prev = Environment.GetEnvironmentVariable("LOCALSCRIBE_MODELS") ?? "";
        try
        {
            Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS", null);
            string p = ModelPaths.Resolve("ggml-tiny.en.bin");
            Assert.True(Path.IsPathFullyQualified(p));
            Assert.Equal("models", Path.GetFileName(Path.GetDirectoryName(p)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS",
                prev.Length == 0 ? null : prev);
        }
    }

    [Fact]
    public void AvailableModels_ListsPresentGgmlBasenames()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ls-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "ggml-base.en.bin"), "x");
            File.WriteAllText(Path.Combine(dir, "ggml-small.en.bin"), "x");
            File.WriteAllText(Path.Combine(dir, "silero_vad.onnx"), "x");   // not a ggml model

            Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS", dir);
            var models = ModelPaths.AvailableModels();

            Assert.Contains("base.en", models);
            Assert.Contains("small.en", models);
            Assert.DoesNotContain("silero_vad", models);
            Assert.Equal(2, models.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS", null);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void AvailableModels_EmptyWhenDirMissing()
    {
        Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS",
            Path.Combine(Path.GetTempPath(), "ls-nope-" + Guid.NewGuid().ToString("N")));
        try { Assert.Empty(ModelPaths.AvailableModels()); }
        finally { Environment.SetEnvironmentVariable("LOCALSCRIBE_MODELS", null); }
    }
}
