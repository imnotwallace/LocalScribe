using LocalScribe.Core.Model;
using LocalScribe.Core.People;

public class VoiceprintMatcherTests
{
    private const string M = "campplus-zh-en";

    private static Person P(string id, string name, params float[][] vecs) => new()
    {
        Id = id, Name = name,
        Voiceprint = vecs.Select((v, i) => new VoiceprintEnrollment
        { Id = $"{id}-e{i}", Embedding = v, Method = M }).ToList(),
    };

    [Fact]
    public void Clear_match_is_suggested()
    {
        var suggestions = VoiceprintMatcher.Suggest(
            new Dictionary<string, float[]> { ["Remote:0"] = [1f, 0f] }, M,
            [P("p1", "Sarah", [1f, 0.1f]), P("p2", "Bob", [0f, 1f])]);
        Assert.Equal("p1", suggestions["Remote:0"].PersonId);
        Assert.Equal("Sarah", suggestions["Remote:0"].PersonName);
        Assert.True(suggestions["Remote:0"].Score > 0.9);
    }

    [Fact]
    public void Below_threshold_is_not_suggested()
    {
        var s = VoiceprintMatcher.Suggest(
            new Dictionary<string, float[]> { ["Remote:0"] = [1f, 0f] }, M,
            [P("p1", "A", [0.5f, 0.9f])]);   // cosine ~0.486
        Assert.Empty(s);
    }

    [Fact]
    public void Confusable_runners_up_suppress_the_suggestion()
    {
        // Two people nearly identical to the probe: margin < 0.05 -> no suggestion.
        var s = VoiceprintMatcher.Suggest(
            new Dictionary<string, float[]> { ["Remote:0"] = [1f, 0f] }, M,
            [P("p1", "A", [1f, 0.01f]), P("p2", "B", [1f, 0.02f])]);
        Assert.Empty(s);
    }

    [Fact]
    public void Wrong_method_enrollments_are_skipped()
    {
        var stale = P("p1", "A", [1f, 0f]) with
        {
            Voiceprint = [new VoiceprintEnrollment { Id = "e", Embedding = [1f, 0f], Method = "other" }],
        };
        Assert.Empty(VoiceprintMatcher.Suggest(
            new Dictionary<string, float[]> { ["Remote:0"] = [1f, 0f] }, M, [stale]));
    }

    [Fact]
    public void Person_score_is_max_over_enrollments()
    {
        var s = VoiceprintMatcher.Suggest(
            new Dictionary<string, float[]> { ["Remote:0"] = [1f, 0f] }, M,
            [P("p1", "A", [0f, 1f], [1f, 0f])]);   // second enrollment is the match
        Assert.Equal("p1", s["Remote:0"].PersonId);
    }

    [Fact]
    public void Empty_pool_or_no_embeddings_yields_empty()
    {
        Assert.Empty(VoiceprintMatcher.Suggest(new Dictionary<string, float[]>(), M, [P("p1", "A", [1f])]));
        Assert.Empty(VoiceprintMatcher.Suggest(new Dictionary<string, float[]> { ["Remote:0"] = [1f] }, M, []));
    }

    [Fact]
    public void No_qualifying_cluster_is_omitted_not_padded_with_null()
    {
        // Two clusters: one clearly matches, one has no candidate above threshold.
        // The result must contain ONLY the matching key - no null/placeholder entry for the other.
        var s = VoiceprintMatcher.Suggest(
            new Dictionary<string, float[]>
            {
                ["Remote:0"] = [1f, 0f],
                ["Remote:1"] = [0.5f, 0.9f],   // ~0.486 cosine vs p1 - below threshold
            }, M,
            [P("p1", "Sarah", [1f, 0f])]);

        Assert.Single(s);
        Assert.True(s.ContainsKey("Remote:0"));
        Assert.False(s.ContainsKey("Remote:1"));
    }

    [Fact]
    public void Candidate_order_does_not_affect_the_result_clear_winner()
    {
        // Three candidates, clear winner + a clear (non-confusable) runner-up.
        // The identity of best/runner-up must not depend on the order candidates are supplied in.
        var probe = new Dictionary<string, float[]> { ["Remote:0"] = [1f, 0f] };
        var a = P("a", "A", [1f, 0.01f]);   // best: cosine ~0.99995
        var b = P("b", "B", [1f, 0.5f]);    // runner-up: cosine ~0.894
        var c = P("c", "C", [0f, 1f]);      // clearly not a match: cosine 0

        var forward = VoiceprintMatcher.Suggest(probe, M, [a, b, c]);
        var reversed = VoiceprintMatcher.Suggest(probe, M, [c, b, a]);
        var shuffled = VoiceprintMatcher.Suggest(probe, M, [b, c, a]);

        foreach (var s in new[] { forward, reversed, shuffled })
        {
            Assert.Equal("a", s["Remote:0"].PersonId);
            Assert.Equal("A", s["Remote:0"].PersonName);
        }
        Assert.Equal(forward["Remote:0"].Score, reversed["Remote:0"].Score, 10);
        Assert.Equal(forward["Remote:0"].Score, shuffled["Remote:0"].Score, 10);
    }

    [Fact]
    public void Candidate_order_does_not_affect_suppression_when_top_two_are_confusable()
    {
        // Confusable pair where the dethroned "old best" must still count toward the runner-up
        // regardless of when in the sequence it was overtaken.
        var probe = new Dictionary<string, float[]> { ["Remote:0"] = [1f, 0f] };
        var a = P("a", "A", [1f, 0.02f]);   // slightly lower of the confusable pair
        var b = P("b", "B", [1f, 0.01f]);   // slightly higher of the confusable pair
        var low = P("low", "Low", [0f, 1f]); // irrelevant low scorer

        Assert.Empty(VoiceprintMatcher.Suggest(probe, M, [a, b, low]));
        Assert.Empty(VoiceprintMatcher.Suggest(probe, M, [low, a, b]));
        Assert.Empty(VoiceprintMatcher.Suggest(probe, M, [b, low, a]));
        Assert.Empty(VoiceprintMatcher.Suggest(probe, M, [a, low, b]));
    }
}
