namespace LocalScribe.Core.Vocabulary;

public interface IVocabularyProvider
{
    /// <summary>Bounded ~N-token initial-prompt bias shortlist for whisper.cpp (spec section 3/section 10).</summary>
    string BuildInitialPrompt(IReadOnlyList<string> matterIds);

    /// <summary>Deterministic heard->correct post-pass (projection-only; spec section 6.1 step 2).</summary>
    string ApplyCorrections(string text, IReadOnlyList<string> matterIds);
}
