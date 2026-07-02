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
}
