namespace Noctis.Plugins.AlbumRoulette.Roulette;

/// <summary>
/// The Language chip (GitHub #202): which language a song is in, told offline from the script
/// its title, album and artist (and, when those are in Latin letters, its lyrics) are written
/// in. Music tags almost never carry a language and Noctis does not read one, so the script is
/// the one signal every library has: Malayalam, Tamil, Hangul or Kana letters say which
/// language a song is in; Latin letters cannot tell English from Spanish, so they are one
/// group. A script shared by several languages is named after the script ("Cyrillic").
/// Nothing is looked up online.
/// </summary>
public static class ScriptLanguage
{
    /// <summary>Titles in Latin letters only (English, Spanish, romanized titles, ...).</summary>
    public const string Latin = "Latin script";

    public const string Japanese = "Japanese";
    public const string Chinese = "Chinese";
    public const string Korean = "Korean";

    // Unicode blocks → language. Latin, Han, Kana and Hangul are handled on their own in Score.
    private static readonly (int First, int Last, string Name)[] Blocks =
    {
        (0x0370, 0x03FF, "Greek"),
        (0x1F00, 0x1FFF, "Greek"),
        (0x0400, 0x052F, "Cyrillic"),
        (0x0530, 0x058F, "Armenian"),
        (0x0590, 0x05FF, "Hebrew"),
        (0x0600, 0x06FF, "Arabic script"),
        (0x0750, 0x077F, "Arabic script"),
        (0x08A0, 0x08FF, "Arabic script"),
        (0xFB50, 0xFDFF, "Arabic script"),
        (0xFE70, 0xFEFF, "Arabic script"),
        (0x0900, 0x097F, "Hindi / Marathi"),
        (0x0980, 0x09FF, "Bengali"),
        (0x0A00, 0x0A7F, "Punjabi"),
        (0x0A80, 0x0AFF, "Gujarati"),
        (0x0B00, 0x0B7F, "Odia"),
        (0x0B80, 0x0BFF, "Tamil"),
        (0x0C00, 0x0C7F, "Telugu"),
        (0x0C80, 0x0CFF, "Kannada"),
        (0x0D00, 0x0D7F, "Malayalam"),
        (0x0D80, 0x0DFF, "Sinhala"),
        (0x0E00, 0x0E7F, "Thai"),
        (0x0E80, 0x0EFF, "Lao"),
        (0x0F00, 0x0FFF, "Tibetan"),
        (0x1000, 0x109F, "Burmese"),
        (0x10A0, 0x10FF, "Georgian"),
        (0x1200, 0x139F, "Amharic"),
        (0x1780, 0x17FF, "Khmer"),
    };

    /// <summary>
    /// The language of <paramref name="texts"/> taken together (a song's title, album and
    /// artist): the non-Latin script with the most letters (two at least; one Han, Kana or Hangul
    /// character is a word already), else <see cref="Latin"/> when there are Latin letters, else
    /// null (no letters: "1999", ""). Kana anywhere makes Han characters Japanese; Han without
    /// Kana is Chinese.
    /// </summary>
    public static string? Detect(params string?[] texts)
    {
        var (best, _, latin) = Score(texts);
        return best ?? (latin > 0 ? Latin : null);
    }

    /// <summary>
    /// The language of a song's lyrics, or null when they do not name one: no lyrics, Latin
    /// letters only, or a few words in another script inside a song in Latin letters (that
    /// script must hold a fifth of the letters, a Han, Kana or Hangul character counting as two).
    /// </summary>
    public static string? DetectLyrics(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return null;
        var (best, count, latin) = Score(lines.Take(120));
        if (best is null) return null;
        var weight = best is Japanese or Chinese or Korean ? count * 2 : count;
        return weight * 5 >= weight + latin ? best : null;
    }

    private static (string? Best, int Count, int Latin) Score(IEnumerable<string?> texts)
    {
        Dictionary<string, int>? counts = null;
        int latin = 0, han = 0, kana = 0, hangul = 0;
        foreach (var text in texts)
        {
            if (string.IsNullOrEmpty(text)) continue;
            foreach (var c in text)
            {
                if (c < 0x0080)
                {
                    if (char.IsAsciiLetter(c)) latin++;
                    continue;
                }
                if (c <= 0x024F || c is >= (char)0x1E00 and <= (char)0x1EFF)
                {
                    if (char.IsLetter(c)) latin++;
                    continue;
                }
                if (c is >= (char)0x3040 and <= (char)0x30FF or >= (char)0x31F0 and <= (char)0x31FF or >= (char)0xFF66 and <= (char)0xFF9D) { kana++; continue; }
                if (c is >= (char)0x4E00 and <= (char)0x9FFF or >= (char)0x3400 and <= (char)0x4DBF or >= (char)0xF900 and <= (char)0xFAFF) { han++; continue; }
                if (c is >= (char)0xAC00 and <= (char)0xD7AF or >= (char)0x1100 and <= (char)0x11FF or >= (char)0x3130 and <= (char)0x318F) { hangul++; continue; }
                if (BlockOf(c) is { } name)
                {
                    counts ??= new Dictionary<string, int>(StringComparer.Ordinal);
                    counts[name] = counts.GetValueOrDefault(name) + 1;
                }
            }
        }

        string? best = null;
        var bestCount = 0;
        void Consider(string name, int count, int needed)
        {
            if (count >= needed && count > bestCount) { best = name; bestCount = count; }
        }
        if (kana > 0) Consider(Japanese, kana + han, 1);
        else Consider(Chinese, han, 1);
        Consider(Korean, hangul, 1);
        if (counts != null)
            foreach (var (name, count) in counts)
                Consider(name, count, 2);
        return (best, bestCount, latin);
    }

    private static string? BlockOf(char c)
    {
        foreach (var (first, last, name) in Blocks)
            if (c >= first && c <= last) return name;
        return null;
    }

    /// <summary>True for a language the script names (not <see cref="Latin"/>, not unknown):
    /// one a song's lyrics may add to a Latin-lettered title.</summary>
    public static bool IsSpecific(string? language) => language is not null && language != Latin;
}
