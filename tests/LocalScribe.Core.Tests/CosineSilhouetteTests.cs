using LocalScribe.Core.Diarisation;

public class CosineSilhouetteTests
{
    private static float[] V(params float[] xs) => WeightedKMeans.NormalizeOrNull(xs)!;

    [Fact]
    public void Well_separated_blobs_score_near_one()
    {
        var vectors = new List<float[]>
        {
            V(1f, 0.02f), V(1f, -0.02f), V(0.02f, 1f), V(-0.02f, 1f),
        };
        double s = CosineSilhouette.Weighted(vectors, [1.0, 1.0, 1.0, 1.0], [0, 0, 1, 1], 2);
        Assert.True(s > 0.8, $"expected near 1, got {s}");
    }

    [Fact]
    public void Random_split_of_one_blob_scores_near_zero_or_negative()
    {
        var vectors = new List<float[]>
        {
            V(1f, 0.01f), V(1f, -0.01f), V(1f, 0.02f), V(1f, -0.02f),
        };
        double s = CosineSilhouette.Weighted(vectors, [1.0, 1.0, 1.0, 1.0], [0, 1, 0, 1], 2);
        Assert.True(s < 0.2, $"expected low, got {s}");
    }

    [Fact]
    public void Singleton_cluster_scores_zero_for_that_point()
    {
        var vectors = new List<float[]> { V(1f, 0f), V(0f, 1f), V(0.01f, 1f) };
        // Point 0 is a singleton: s(0)=0. Points 1,2 are a tight pair far from cluster 0.
        double s = CosineSilhouette.Weighted(vectors, [1.0, 1.0, 1.0], [0, 1, 1], 2);
        Assert.InRange(s, 0.5, 1.0); // (0 + ~1 + ~1) / 3
    }

    [Fact]
    public void Weights_shift_the_mean()
    {
        var vectors = new List<float[]> { V(1f, 0f), V(0f, 1f), V(0.01f, 1f) };
        double light = CosineSilhouette.Weighted(vectors, [1.0, 1.0, 1.0], [0, 1, 1], 2);
        double heavy = CosineSilhouette.Weighted(vectors, [100.0, 1.0, 1.0], [0, 1, 1], 2);
        Assert.True(heavy < light, $"singleton weight 100 should drag the mean: {heavy} !< {light}");
    }

    [Fact]
    public void Single_cluster_returns_zero()
    {
        var vectors = new List<float[]> { V(1f, 0f), V(0f, 1f) };
        Assert.Equal(0.0, CosineSilhouette.Weighted(vectors, [1.0, 1.0], [0, 0], 1));
    }
}
