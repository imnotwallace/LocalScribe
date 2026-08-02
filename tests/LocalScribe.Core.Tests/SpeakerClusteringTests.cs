using LocalScribe.Core.Diarisation;

public class SpeakerClusteringTests
{
    private static float[] V(params float[] xs) => xs;

    // Two voices: direction (1,0) and direction (0,1). Long segments are reliable.
    private static List<TimedEmbedding> TwoVoices() =>
    [
        new(0, 3000, V(1f, 0.05f)),        // A reliable
        new(3000, 3500, V(0.2f, 0.9f)),    // bridge (500ms), actually voice B
        new(3500, 8000, V(0.03f, 1f)),     // B reliable
        new(8000, 12000, V(1f, -0.04f)),   // A reliable
        new(12000, 12400, V(0.9f, 0.1f)),  // bridge (400ms), voice A
        new(12400, 20000, V(0.01f, 0.98f)),// B reliable
    ];

    [Fact]
    public void Forced_two_recovers_both_voices_and_numbers_by_first_appearance()
    {
        var r = SpeakerClustering.Cluster(TwoVoices(), 2);
        Assert.Equal(2, r.ClusterCount);
        // First temporal segment is voice A -> cluster 0; voice B -> cluster 1.
        Assert.Equal(0, r.ClusterBySegment[0]);
        Assert.Equal(1, r.ClusterBySegment[2]);
        Assert.Equal(0, r.ClusterBySegment[3]);
        Assert.Equal(1, r.ClusterBySegment[5]);
        // Bridges attach to the right voice.
        Assert.Equal(1, r.ClusterBySegment[1]);
        Assert.Equal(0, r.ClusterBySegment[4]);
    }

    [Fact]
    public void Forced_result_is_deterministic()
    {
        var a = SpeakerClustering.Cluster(TwoVoices(), 2);
        var b = SpeakerClustering.Cluster(TwoVoices(), 2);
        Assert.Equal(a.ClusterBySegment, b.ClusterBySegment);
        Assert.Equal(a.ClusterCount, b.ClusterCount);
    }

    [Fact]
    public void Zero_norm_bridge_takes_temporally_nearest_reliable_cluster()
    {
        var segs = new List<TimedEmbedding>
        {
            new(0, 3000, V(1f, 0f)),
            new(3000, 3200, V(0f, 0f)),      // zero-norm, nearest reliable is seg 0 (A)
            new(9000, 14000, V(0f, 1f)),
        };
        var r = SpeakerClustering.Cluster(segs, 2);
        Assert.Equal(r.ClusterBySegment[0], r.ClusterBySegment[1]);
    }

    [Fact]
    public void Forced_k_clamps_to_available_segments()
    {
        var segs = new List<TimedEmbedding>
        {
            new(0, 2000, V(1f, 0f)),
            new(2000, 4000, V(0f, 1f)),
        };
        var r = SpeakerClustering.Cluster(segs, 5);
        Assert.Equal(2, r.ClusterCount);
    }

    [Fact]
    public void Bar_drops_when_too_few_reliable_segments()
    {
        // All segments are sub-second bridges; forced 2 must still produce 2 clusters.
        var segs = new List<TimedEmbedding>
        {
            new(0, 500, V(1f, 0.02f)),
            new(500, 900, V(0.9f, 0f)),
            new(900, 1400, V(0.02f, 1f)),
            new(1400, 1800, V(0f, 0.9f)),
        };
        var r = SpeakerClustering.Cluster(segs, 2);
        Assert.Equal(2, r.ClusterCount);
        Assert.Equal(r.ClusterBySegment[0], r.ClusterBySegment[1]);
        Assert.Equal(r.ClusterBySegment[2], r.ClusterBySegment[3]);
        Assert.NotEqual(r.ClusterBySegment[0], r.ClusterBySegment[2]);
    }

    [Fact]
    public void All_zero_norm_embeddings_collapse_to_single_cluster()
    {
        var segs = new List<TimedEmbedding>
        {
            new(0, 2000, V(0f, 0f)),
            new(2000, 4000, V(0f, 0f)),
        };
        var r = SpeakerClustering.Cluster(segs, 2);
        Assert.Equal(1, r.ClusterCount);
        Assert.Equal(new[] { 0, 0 }, r.ClusterBySegment);
    }

    [Fact]
    public void Empty_input_yields_empty_outcome()
    {
        var r = SpeakerClustering.Cluster([], 2);
        Assert.Empty(r.ClusterBySegment);
        Assert.Equal(0, r.ClusterCount);
    }

    [Fact]
    public void Forced_one_puts_everything_in_cluster_zero()
    {
        var r = SpeakerClustering.Cluster(TwoVoices(), 1);
        Assert.Equal(1, r.ClusterCount);
        Assert.All(r.ClusterBySegment, c => Assert.Equal(0, c));
    }
}
