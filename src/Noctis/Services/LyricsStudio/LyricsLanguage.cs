using System.Globalization;

namespace Noctis.Services.LyricsStudio;

/// <summary>
/// The language a song's known lyrics are written in, as a Whisper language code — the strongest
/// evidence there is for which language the model should listen for. A script other than Latin
/// names its language outright (Hangul → ko, kana → ja, …); Latin text is told apart by its most
/// frequent function words. <see cref="Guess.Confident"/> is false when the text leaves a choice
/// (too few telltale words, or two languages close, as in a bilingual song): then
/// <see cref="Guess.Candidates"/> are the languages the audio should decide between.
/// Pure; no lyric text is kept or logged.
/// </summary>
public static class LyricsLanguage
{
    public sealed record Guess(string? Code, bool Confident, IReadOnlyList<string> Candidates);

    /// <summary>A non-Latin script on at least this share of the words names the language (K-pop with English lines is Korean).</summary>
    private const double ScriptShare = 0.25;
    /// <summary>Function-word hits the best Latin language needs, and how far it must lead the next one.</summary>
    private const double MinHits = 6;
    private const double MinLead = 1.6;

    /// <summary>Whisper languages written in the Latin script (the ones lyrics are commonly in first).</summary>
    public static readonly IReadOnlyList<string> LatinLanguages = new[]
    {
        "en", "es", "pt", "fr", "de", "it", "nl", "id", "tr", "pl", "tl", "sv", "ro", "ca", "da", "no", "fi", "cs", "sk",
        "hr", "hu", "ms", "vi", "sw", "lt", "lv", "sl", "et", "is", "af", "cy", "ga", "eu", "gl", "sq", "bs", "mt", "az", "uz",
        "haw", "mi", "la", "lb", "yo", "ha", "so", "sn", "ln",
    };

    private static readonly Dictionary<string, string[]> FunctionWords = new()
    {
        ["en"] = new[] { "the", "and", "you", "i", "to", "it", "me", "my", "in", "that", "is", "of", "on", "your", "we", "be", "all", "just", "dont", "know", "im", "what", "like", "cant", "when", "with", "for", "this", "but", "got", "wanna", "gonna", "baby", "yeah", "never", "love", "want", "need", "cause", "youre", "aint", "were", "they", "she", "he" },
        ["es"] = new[] { "que", "de", "el", "la", "y", "en", "yo", "tu", "me", "te", "mi", "lo", "no", "se", "un", "una", "por", "con", "para", "pero", "como", "mas", "esta", "estoy", "quiero", "todo", "cuando", "bebe", "eres", "soy", "ya", "si", "del", "las", "los", "le", "nos", "porque", "tengo", "contigo", "nada", "aqui", "hoy", "tambien", "donde", "vamo", "pa" },
        ["pt"] = new[] { "que", "de", "eu", "voce", "nao", "e", "o", "a", "meu", "minha", "do", "da", "em", "um", "uma", "com", "pra", "mais", "quando", "tudo", "isso", "esse", "essa", "ela", "ele", "tem", "vai", "sou", "estou", "te", "me", "se", "no", "na", "sem", "agora", "gente", "voces", "entao", "muito" },
        ["fr"] = new[] { "je", "tu", "le", "la", "les", "de", "des", "et", "est", "pas", "que", "qui", "un", "une", "mon", "ma", "moi", "toi", "dans", "pour", "avec", "ce", "cest", "sur", "nous", "vous", "il", "elle", "au", "du", "ne", "plus", "jai", "suis", "mais", "comme", "tout", "quand", "jsuis" },
        ["de"] = new[] { "ich", "du", "und", "die", "der", "das", "nicht", "ist", "ein", "eine", "mich", "mir", "dich", "dir", "zu", "mit", "wir", "es", "sie", "auf", "nur", "noch", "wenn", "was", "wie", "bin", "hab", "auch", "den", "dem", "im", "kein", "immer", "mein", "dein" },
        ["it"] = new[] { "che", "di", "e", "il", "la", "non", "un", "una", "mi", "ti", "io", "tu", "per", "con", "sono", "sei", "ma", "come", "piu", "cosa", "questo", "quando", "anche", "nel", "della", "del", "mio", "mia", "ho", "se", "sempre", "ancora", "tutto" },
        ["nl"] = new[] { "ik", "je", "de", "het", "een", "en", "niet", "dat", "van", "is", "jij", "mij", "wat", "met", "op", "voor", "maar", "zo", "ben", "heb", "als", "er", "ze", "we", "naar", "nog", "dit", "mijn", "jouw" },
        ["id"] = new[] { "aku", "kamu", "yang", "dan", "di", "ini", "itu", "tak", "tidak", "ku", "mu", "dia", "kau", "akan", "dengan", "untuk", "ada", "cinta", "hati", "dalam", "bisa", "jangan", "sudah", "saja" },
        ["tr"] = new[] { "bir", "ve", "bu", "ben", "sen", "ne", "da", "de", "cok", "gibi", "icin", "ama", "beni", "seni", "var", "yok", "mi", "degil", "kadar", "her", "o", "bana", "sana" },
        ["pl"] = new[] { "nie", "i", "sie", "w", "to", "na", "ze", "jest", "z", "mnie", "ja", "ty", "co", "jak", "tak", "mi", "do", "a", "juz", "tylko", "bo", "mam" },
        ["tl"] = new[] { "ang", "ng", "sa", "ko", "mo", "na", "ako", "ikaw", "ka", "hindi", "ay", "lang", "kita", "mga", "at", "pag", "kung", "siya" },
        ["sv"] = new[] { "jag", "du", "och", "att", "det", "som", "en", "inte", "ar", "pa", "med", "mig", "dig", "vi", "har", "om", "for", "sa", "kan" },
        ["ro"] = new[] { "si", "eu", "tu", "nu", "ca", "de", "la", "pe", "cu", "un", "o", "sa", "ma", "te", "mi", "esti", "sunt" },
    };

    private static readonly Dictionary<string, List<string>> LanguagesOfWord = BuildIndex();

    private static Dictionary<string, List<string>> BuildIndex()
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (lang, words) in FunctionWords)
            foreach (var w in words)
            {
                if (!index.TryGetValue(w, out var list)) index[w] = list = new List<string>();
                if (!list.Contains(lang)) list.Add(lang);
            }
        return index;
    }

    public static Guess FromLines(IEnumerable<string>? lines)
    {
        if (lines is null) return new Guess(null, false, Array.Empty<string>());
        var scripts = new Dictionary<string, int>(StringComparer.Ordinal);
        var marked = new Dictionary<string, int>(StringComparer.Ordinal); // uk / fa / ur letters seen
        var totalWords = 0;
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            foreach (var raw in (line ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var script = ScriptOf(raw, out var marker);
                if (script is null) continue;
                if (marker is not null) marked[marker] = marked.GetValueOrDefault(marker) + 1;
                totalWords++;
                scripts[script] = scripts.GetValueOrDefault(script) + 1;
                if (script != "latin") continue;
                var norm = LyricsAligner.Normalize(raw);
                if (!LanguagesOfWord.TryGetValue(norm, out var langs)) continue;
                foreach (var l in langs) scores[l] = scores.GetValueOrDefault(l) + 1.0 / langs.Count;
            }
        }
        if (totalWords == 0) return new Guess(null, false, Array.Empty<string>());

        // A non-Latin script: the largest one, when it covers enough of the words.
        var other = scripts.Where(kv => kv.Key != "latin").OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (other.Key is not null && other.Value >= ScriptShare * totalWords)
        {
            // Cyrillic / Arabic script: letters only Ukrainian / Urdu / Persian use, on a tenth of its words, name those.
            var code = other.Key;
            if (code == "ru" && marked.GetValueOrDefault("uk") * 10 >= other.Value) code = "uk";
            if (code == "ar" && marked.GetValueOrDefault("ur") * 10 >= other.Value) code = "ur";
            else if (code == "ar" && marked.GetValueOrDefault("fa") * 10 >= other.Value) code = "fa";
            return new Guess(code, true, new[] { code });
        }
        if (scripts.GetValueOrDefault("latin") == 0) return new Guess(null, false, Array.Empty<string>());

        var ranked = scores.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        if (ranked.Count == 0) return new Guess(null, false, LatinLanguages);
        var top = ranked[0];
        var second = ranked.Count > 1 ? ranked[1].Value : 0;
        if (top.Value >= MinHits && top.Value >= MinLead * second)
            return new Guess(top.Key, true, new[] { top.Key });
        // Close call (a bilingual song, or little text): the audio decides between the close ones.
        var close = ranked.Where(kv => kv.Value >= top.Value / MinLead).Select(kv => kv.Key).ToList();
        return new Guess(top.Key, false, close.Count >= 2 ? close : LatinLanguages);
    }

    /// <summary>
    /// The Whisper language a word's letters name ("latin" for Latin letters; "ru" for any Cyrillic,
    /// "ar" for any Arabic script), or null for no letters. <paramref name="marker"/>: "uk", "fa"
    /// or "ur" when the word has a letter only that language uses.
    /// </summary>
    internal static string? ScriptOf(string word, out string? marker)
    {
        marker = null;
        int latin = 0, total = 0;
        string? best = null;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var ukrainian = false;
        var persian = false;
        var urdu = false;
        foreach (var ch in word.Normalize(System.Text.NormalizationForm.FormC))
        {
            if (!char.IsLetter(ch)) continue;
            total++;
            var s = ScriptOfChar(ch);
            if (s == "latin") { latin++; continue; }
            if (s is null) continue;
            if (ch is 'і' or 'ї' or 'є' or 'ґ' or 'І' or 'Ї' or 'Є' or 'Ґ') ukrainian = true;
            if (ch is 'پ' or 'چ' or 'ژ' or 'گ' or 'ی') persian = true;
            if (ch is 'ٹ' or 'ڈ' or 'ڑ' or 'ں' or 'ے') urdu = true;
            counts[s] = counts.GetValueOrDefault(s) + 1;
        }
        if (total == 0) return null;
        var other = counts.OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (other.Key is not null && other.Value > latin)
        {
            best = other.Key;
            if (best == "ru" && ukrainian) marker = "uk";
            if (best == "ar" && urdu) marker = "ur";
            else if (best == "ar" && persian) marker = "fa";
            // Han characters next to kana are Japanese.
            if (best == "zh" && counts.ContainsKey("ja")) best = "ja";
            return best;
        }
        return latin > 0 ? "latin" : null;
    }

    private static string? ScriptOfChar(char ch)
    {
        var c = (int)ch;
        if (c < 0x250) return char.IsLetter(ch) ? "latin" : null;
        if (c is >= 0x1E00 and <= 0x1EFF) return "latin";
        if (c is >= 0x0370 and <= 0x03FF) return "el";
        if (c is >= 0x0400 and <= 0x052F) return "ru";
        if (c is >= 0x0530 and <= 0x058F) return "hy";
        if (c is >= 0x0590 and <= 0x05FF) return "he";
        if (c is >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F) return "ar";
        if (c is >= 0x0900 and <= 0x097F) return "hi";
        if (c is >= 0x0980 and <= 0x09FF) return "bn";
        if (c is >= 0x0A00 and <= 0x0A7F) return "pa";
        if (c is >= 0x0A80 and <= 0x0AFF) return "gu";
        if (c is >= 0x0B80 and <= 0x0BFF) return "ta";
        if (c is >= 0x0C00 and <= 0x0C7F) return "te";
        if (c is >= 0x0C80 and <= 0x0CFF) return "kn";
        if (c is >= 0x0D00 and <= 0x0D7F) return "ml";
        if (c is >= 0x0D80 and <= 0x0DFF) return "si";
        if (c is >= 0x0E00 and <= 0x0E7F) return "th";
        if (c is >= 0x0E80 and <= 0x0EFF) return "lo";
        if (c is >= 0x1000 and <= 0x109F) return "my";
        if (c is >= 0x10A0 and <= 0x10FF) return "ka";
        if (c is >= 0x1780 and <= 0x17FF) return "km";
        if (c is >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xAC00 and <= 0xD7AF) return "ko";
        if (c is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F) return "ja";
        if (c is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF) return "zh";
        return null;
    }
}
