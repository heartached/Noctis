using Avalonia.Media;

namespace Noctis.Helpers;

/// <summary>
/// Fonts for the scripts the app's own face lacks. Every TextBlock draws in the embedded Inter
/// SemiBold, which has no Arabic letters, so a Persian or Arabic title or lyric fell to whatever
/// the platform's fallback picked for the requested weight (GitHub #269: Persian lyrics looked
/// out of place next to the rest of the app). Arabic-script text now draws in the bundled
/// Vazirmatn UI (SIL OFL 1.1, Assets/Fonts/Vazirmatn/OFL.txt) on every platform.
/// </summary>
public static class ScriptFonts
{
    /// <summary>
    /// Vazirmatn's UI cut (tighter line height) without its Latin letters, which Inter draws.
    /// Only SemiBold and Bold are bundled: the app's text is SemiBold whatever weight a control
    /// asks for, so a lighter request matches SemiBold and the bold lyrics get the real Bold.
    /// </summary>
    public const string ArabicFamily = "avares://Noctis.UI/Assets/Fonts/Vazirmatn#Vazirmatn UI NL";

    /// <summary>Arabic, Arabic Supplement and the Arabic presentation forms (Persian, Urdu,
    /// Kurdish, Pashto letters and digits included).</summary>
    public static readonly UnicodeRange ArabicScript =
        UnicodeRange.Parse("U+0600-06FF, U+0750-077F, U+FB50-FDFF, U+FE70-FEFF");

    /// <summary>
    /// The fallback chain for <see cref="FontManagerOptions.FontFallbacks"/>: Vazirmatn for
    /// Arabic-script text first, then <paramref name="platformFamilies"/> (the CJK and emoji
    /// faces a platform needs named) for every other missing glyph.
    /// </summary>
    public static FontManagerOptions Options(params string[] platformFamilies)
    {
        var fallbacks = new FontFallback[platformFamilies.Length + 1];
        fallbacks[0] = new FontFallback { FontFamily = new FontFamily(ArabicFamily), UnicodeRange = ArabicScript };
        for (var i = 0; i < platformFamilies.Length; i++)
            fallbacks[i + 1] = new FontFallback { FontFamily = new FontFamily(platformFamilies[i]) };
        return new FontManagerOptions { FontFallbacks = fallbacks };
    }
}
