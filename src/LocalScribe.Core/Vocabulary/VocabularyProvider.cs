using System.Text.RegularExpressions;
using LocalScribe.Core.Model;
namespace LocalScribe.Core.Vocabulary;

/// <summary>Effective vocabulary = global (settings) UNION matters(session), consumed two ways:
/// initial-prompt bias + a projection-time heard->correct pass (spec section 1.7/section 10).</summary>
public sealed class VocabularyProvider : IVocabularyProvider
{
    private readonly Model.Vocabulary _global;
    private readonly IReadOnlyDictionary<string, Matter> _mattersById;
    private readonly int _maxPromptTokens;

    public VocabularyProvider(Model.Vocabulary global, IReadOnlyDictionary<string, Matter> mattersById,
        int maxPromptTokens = 200)
        => (_global, _mattersById, _maxPromptTokens) = (global, mattersById, maxPromptTokens);

    public string BuildInitialPrompt(IReadOnlyList<string> matterIds)
    {
        var chosen = new List<string>();
        int tokens = 0;
        foreach (string term in EffectiveTerms(matterIds))
        {
            int t = term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (tokens + t > _maxPromptTokens) break;
            chosen.Add(term);
            tokens += t;
        }
        return string.Join(", ", chosen);
    }

    public string ApplyCorrections(string text, IReadOnlyList<string> matterIds)
    {
        // Sequential, longest-key-first over the running text: one rule's output can match a
        // later rule's key (deterministic, documented). Lookarounds instead of \b so keys with
        // non-word edges ("c#", ".net") still match whole-word.
        foreach (var kv in EffectiveCorrections(matterIds).OrderByDescending(k => k.Key.Length))
        {
            string replacement = kv.Value.Replace("$", "$$");   // escape $ in the regex replacement
            text = Regex.Replace(text, $@"(?<!\w){Regex.Escape(kv.Key)}(?!\w)", replacement, RegexOptions.IgnoreCase);
        }
        return text;
    }

    private List<string> EffectiveTerms(IReadOnlyList<string> matterIds)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        void Add(IEnumerable<string> terms)
        {
            foreach (string t in terms)
                if (t.Length > 0 && seen.Add(t)) result.Add(t);
        }
        Add(_global.Terms);
        foreach (string id in matterIds)
            if (_mattersById.TryGetValue(id, out Matter? m)) Add(m.Vocabulary.Terms);
        return result;
    }

    private Dictionary<string, string> EffectiveCorrections(IReadOnlyList<string> matterIds)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _global.Corrections) map[kv.Key] = kv.Value;
        foreach (string id in matterIds)
            if (_mattersById.TryGetValue(id, out Matter? m))
                foreach (var kv in m.Vocabulary.Corrections) map[kv.Key] = kv.Value;   // matter overrides global
        return map;
    }
}
