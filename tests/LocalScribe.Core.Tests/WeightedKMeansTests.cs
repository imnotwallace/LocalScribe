using LocalScribe.Core.Diarisation;

public class WeightedKMeansTests
{
    private static float[] V(params float[] xs) => WeightedKMeans.NormalizeOrNull(xs)!;

    [Fact]
    public void Normalize_returns_unit_vector_and_null_on_zero_norm()
    {
        var v = WeightedKMeans.NormalizeOrNull([3f, 4f])!;
        Assert.Equal(0.6f, v[0], 5);
        Assert.Equal(0.8f, v[1], 5);
        Assert.Null(WeightedKMeans.NormalizeOrNull([0f, 0f]));
        Assert.Null(WeightedKMeans.NormalizeOrNull([]));
    }

    [Fact]
    public void Separates_two_obvious_blobs()
    {
        // Two orthogonal directions with small perturbations.
        var vectors = new List<float[]>
        {
            V(1f, 0.05f), V(1f, -0.05f), V(1f, 0.02f),
            V(0.05f, 1f), V(-0.05f, 1f), V(0.02f, 1f),
        };
        var weights = new List<double> { 1, 1, 1, 1, 1, 1 };
        var r = WeightedKMeans.Fit(vectors, weights, 2);
        Assert.Equal(r.Assignments[0], r.Assignments[1]);
        Assert.Equal(r.Assignments[1], r.Assignments[2]);
        Assert.Equal(r.Assignments[3], r.Assignments[4]);
        Assert.Equal(r.Assignments[4], r.Assignments[5]);
        Assert.NotEqual(r.Assignments[0], r.Assignments[3]);
    }

    [Fact]
    public void Is_deterministic_across_runs()
    {
        var vectors = new List<float[]>
        {
            V(1f, 0.3f), V(0.9f, 0.1f), V(0.2f, 1f), V(0.1f, 0.8f), V(0.5f, 0.5f),
        };
        var weights = new List<double> { 2.0, 1.0, 3.0, 1.5, 0.4 };
        var a = WeightedKMeans.Fit(vectors, weights, 2);
        var b = WeightedKMeans.Fit(vectors, weights, 2);
        Assert.Equal(a.Assignments, b.Assignments);
        for (int c = 0; c < 2; c++) Assert.Equal(a.Centroids[c], b.Centroids[c]);
    }

    [Fact]
    public void Heavy_weight_anchors_the_centroid()
    {
        // One heavy on-axis vector + one light off-axis vector in the same cluster:
        // the centroid must sit near the heavy vector.
        var vectors = new List<float[]> { V(1f, 0f), V(0.7f, 0.7f), V(0f, 1f) };
        var weights = new List<double> { 100.0, 1.0, 100.0 };
        var r = WeightedKMeans.Fit(vectors, weights, 2);
        int clusterOfHeavy = r.Assignments[0];
        double dHeavy = 1 - VoiceprintMath.Cosine(r.Centroids[clusterOfHeavy], vectors[0]);
        Assert.True(dHeavy < 0.05, $"centroid drifted from heavy anchor: {dHeavy}");
    }

    [Fact]
    public void K_equals_one_puts_everything_in_cluster_zero()
    {
        var vectors = new List<float[]> { V(1f, 0f), V(0f, 1f) };
        var r = WeightedKMeans.Fit(vectors, [1.0, 1.0], 1);
        Assert.Equal(new[] { 0, 0 }, r.Assignments);
        Assert.Single(r.Centroids);
    }

    [Fact]
    public void K_equals_count_gives_every_vector_its_own_cluster()
    {
        var vectors = new List<float[]> { V(1f, 0f), V(0f, 1f), V(0.7f, -0.7f) };
        var r = WeightedKMeans.Fit(vectors, [1.0, 1.0, 1.0], 3);
        Assert.Equal(3, r.Assignments.Distinct().Count());
    }

    [Fact]
    public void Invalid_arguments_throw()
    {
        var one = new List<float[]> { V(1f, 0f) };
        Assert.Throws<ArgumentException>(() => WeightedKMeans.Fit([], [], 1));
        Assert.Throws<ArgumentException>(() => WeightedKMeans.Fit(one, [1.0], 0));
        Assert.Throws<ArgumentException>(() => WeightedKMeans.Fit(one, [1.0], 2));
        Assert.Throws<ArgumentException>(() => WeightedKMeans.Fit(one, [1.0, 1.0], 1));
        Assert.Throws<ArgumentException>(() => WeightedKMeans.Fit(one, [0.0], 1));
    }
}
