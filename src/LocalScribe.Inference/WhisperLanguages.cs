namespace LocalScribe.Inference;

/// <summary>Remote servers disagree on how they name the detected language: OpenAI's verbose_json
/// says "english", most self-hosted servers say "en". The worker's language lock compares
/// whisper.cpp's ISO 639-1 codes, so everything is normalized to those. An unrecognized value
/// becomes null - "no signal" - rather than a junk code that could win the lock vote.</summary>
internal static class WhisperLanguages
{
    // Whisper's own table (tokenizer.LANGUAGES): code -> name.
    private static readonly Dictionary<string, string> CodeToName = new(StringComparer.Ordinal)
    {
        ["en"] = "english", ["zh"] = "chinese", ["de"] = "german", ["es"] = "spanish", ["ru"] = "russian",
        ["ko"] = "korean", ["fr"] = "french", ["ja"] = "japanese", ["pt"] = "portuguese", ["tr"] = "turkish",
        ["pl"] = "polish", ["ca"] = "catalan", ["nl"] = "dutch", ["ar"] = "arabic", ["sv"] = "swedish",
        ["it"] = "italian", ["id"] = "indonesian", ["hi"] = "hindi", ["fi"] = "finnish", ["vi"] = "vietnamese",
        ["he"] = "hebrew", ["uk"] = "ukrainian", ["el"] = "greek", ["ms"] = "malay", ["cs"] = "czech",
        ["ro"] = "romanian", ["da"] = "danish", ["hu"] = "hungarian", ["ta"] = "tamil", ["no"] = "norwegian",
        ["th"] = "thai", ["ur"] = "urdu", ["hr"] = "croatian", ["bg"] = "bulgarian", ["lt"] = "lithuanian",
        ["la"] = "latin", ["mi"] = "maori", ["ml"] = "malayalam", ["cy"] = "welsh", ["sk"] = "slovak",
        ["te"] = "telugu", ["fa"] = "persian", ["lv"] = "latvian", ["bn"] = "bengali", ["sr"] = "serbian",
        ["az"] = "azerbaijani", ["sl"] = "slovenian", ["kn"] = "kannada", ["et"] = "estonian",
        ["mk"] = "macedonian", ["br"] = "breton", ["eu"] = "basque", ["is"] = "icelandic", ["hy"] = "armenian",
        ["ne"] = "nepali", ["mn"] = "mongolian", ["bs"] = "bosnian", ["kk"] = "kazakh", ["sq"] = "albanian",
        ["sw"] = "swahili", ["gl"] = "galician", ["mr"] = "marathi", ["pa"] = "punjabi", ["si"] = "sinhala",
        ["km"] = "khmer", ["sn"] = "shona", ["yo"] = "yoruba", ["so"] = "somali", ["af"] = "afrikaans",
        ["oc"] = "occitan", ["ka"] = "georgian", ["be"] = "belarusian", ["tg"] = "tajik", ["sd"] = "sindhi",
        ["gu"] = "gujarati", ["am"] = "amharic", ["yi"] = "yiddish", ["lo"] = "lao", ["uz"] = "uzbek",
        ["fo"] = "faroese", ["ht"] = "haitian creole", ["ps"] = "pashto", ["tk"] = "turkmen", ["nn"] = "nynorsk",
        ["mt"] = "maltese", ["sa"] = "sanskrit", ["lb"] = "luxembourgish", ["my"] = "myanmar", ["bo"] = "tibetan",
        ["tl"] = "tagalog", ["mg"] = "malagasy", ["as"] = "assamese", ["tt"] = "tatar", ["haw"] = "hawaiian",
        ["ln"] = "lingala", ["ha"] = "hausa", ["ba"] = "bashkir", ["jw"] = "javanese", ["su"] = "sundanese",
        ["yue"] = "cantonese",
    };

    private static readonly Dictionary<string, string> NameToCode =
        CodeToName.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    public static string? ToCode(string? reported)
    {
        if (string.IsNullOrWhiteSpace(reported)) return null;
        string key = reported.Trim().ToLowerInvariant();
        if (CodeToName.ContainsKey(key)) return key;
        return NameToCode.TryGetValue(key, out string? code) ? code : null;
    }
}
