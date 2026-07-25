using LocalScribe.Core.Search.Semantic;

public sealed class EmbeddingMathTests
{
    [Fact]
    public void Truncates_then_renormalizes_to_unit_length()
    {
        float[] v = [3f, 4f, 100f, 100f];
        float[] r = EmbeddingMath.TruncateAndNormalize(v, 2);
        Assert.Equal(2, r.Length);
        Assert.Equal(0.6f, r[0], 3);
        Assert.Equal(0.8f, r[1], 3);
    }

    [Fact]
    public void Dim_zero_or_larger_than_vector_keeps_full_width()
    {
        Assert.Equal(3, EmbeddingMath.TruncateAndNormalize([1f, 2f, 2f], 0).Length);
        Assert.Equal(3, EmbeddingMath.TruncateAndNormalize([1f, 2f, 2f], 99).Length);
    }

    [Fact]
    public void Zero_vector_stays_zero_never_nan()
    {
        float[] r = EmbeddingMath.TruncateAndNormalize([0f, 0f], 2);
        Assert.All(r, f => Assert.Equal(0f, f));
    }

    [Fact]
    public void Method_string_is_filename_lowercase_at_dim()
    {
        Assert.Equal("embeddinggemma-300m-q8_0@256",
            EmbeddingMethod.For(@"C:\models\embeddingGemma-300M-Q8_0.gguf", 256));
    }

    [Fact]
    public void Input_array_is_never_mutated()
    {
        float[] v = [3f, 4f];
        EmbeddingMath.TruncateAndNormalize(v, 0);
        Assert.Equal(new[] { 3f, 4f }, v);
        EmbeddingMath.TruncateAndNormalize(v, 1);
        Assert.Equal(new[] { 3f, 4f }, v);
    }
}
