using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// The Artists grid's A–Z index and its placeholder circles. An artist files under the first
/// letter or digit of its name with accents dropped ("Ébano" under E); a digit, a symbol or a
/// non-Latin letter files under "#", which Apple lists after Z.
/// </summary>
public static class ArtistIndex
{
    /// <summary>The index strip, top to bottom.</summary>
    public static IReadOnlyList<string> Letters { get; } =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ#".Select(c => c.ToString()).ToArray();

    public const string Other = "#";

    public static string LetterOf(string? name)
    {
        foreach (var c in name ?? string.Empty)
        {
            if (!char.IsLetterOrDigit(c)) continue;
            // FormD splits "É" into "E" + a combining accent; the base letter comes first.
            var letter = char.ToUpperInvariant(c.ToString().Normalize(NormalizationForm.FormD)[0]);
            return letter is >= 'A' and <= 'Z' ? letter.ToString() : Other;
        }
        return Other;
    }

    /// <summary>A letter's place on the strip (A = 0 … # = 26); -1 for anything else.</summary>
    public static int OrderOf(string letter)
    {
        for (var i = 0; i < Letters.Count; i++)
            if (string.Equals(Letters[i], letter, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The name without leading punctuation ("'Til Tuesday" sorts as "Til Tuesday"),
    /// so the order agrees with <see cref="LetterOf"/>.</summary>
    public static string SortName(string name)
    {
        var start = 0;
        while (start < name.Length && !char.IsLetterOrDigit(name[start])) start++;
        return start < name.Length ? name[start..] : name;
    }

    /// <summary>Up to two initials for a circle without a picture: the first letter or digit
    /// of the first two words ("Bruno Mars" → "BM", "ABBA" → "A", "Anderson .Paak" → "AP").</summary>
    public static string Initials(string? name)
    {
        var initials = new StringBuilder(2);
        foreach (var word in (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var first = word.FirstOrDefault(char.IsLetterOrDigit);
            if (first == default) continue;
            initials.Append(char.ToUpperInvariant(first));
            if (initials.Length == 2) break;
        }
        return initials.ToString();
    }

    // Soft mid-tone gradients (light top, deeper bottom) that hold white initials on both the
    // light and the dark theme. Immutable: shared by every circle, on any thread.
    private static readonly IBrush[] Placeholders =
    {
        Gradient("#E8A0AE", "#C46A7E"),   // rose
        Gradient("#EDB48E", "#C9845A"),   // apricot
        Gradient("#E3C27A", "#B8964A"),   // amber
        Gradient("#A9C98E", "#76A05C"),   // sage
        Gradient("#86CBBE", "#4E9E90"),   // teal
        Gradient("#8FC1E6", "#5A92C2"),   // sky
        Gradient("#A3A8E8", "#6E74C4"),   // periwinkle
        Gradient("#C3A2E2", "#946BC0"),   // lavender
        Gradient("#D9A3CF", "#AE6FA3"),   // orchid
        Gradient("#A9B4C2", "#76859A"),   // slate
    };

    /// <summary>The placeholder circle's tint, picked by name so an artist keeps its colour
    /// from launch to launch (string.GetHashCode is randomised per process).</summary>
    public static IBrush PlaceholderBrush(string? name)
    {
        var hash = 17u;
        foreach (var c in name ?? string.Empty) hash = unchecked(hash * 31 + char.ToUpperInvariant(c));
        return Placeholders[hash % (uint)Placeholders.Length];
    }

    private static IBrush Gradient(string top, string bottom) => new ImmutableLinearGradientBrush(
        new[] { new ImmutableGradientStop(0, Color.Parse(top)), new ImmutableGradientStop(1, Color.Parse(bottom)) },
        startPoint: new RelativePoint(0.5, 0, RelativeUnit.Relative),
        endPoint: new RelativePoint(0.5, 1, RelativeUnit.Relative));
}
