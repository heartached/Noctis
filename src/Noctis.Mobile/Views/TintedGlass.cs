using Avalonia.Controls;
using Avalonia.Media;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// The floating chrome's Liquid Glass in a page's cover colour (owner 10-20, off Apple Music's
/// artist page: on a red page the back button, the share/⋯ capsule, the mini player and the tab
/// bar are red glass, not the theme's milky white or grey). The one place that look is built, for
/// the shell's chrome (ShellView, over an album or artist page) and the hero pages' floating
/// buttons (HeroChrome): the glass tint is the page colour itself, lifted a touch in its own hue
/// on a deep page and paled on a light one, as translucent as the theme's chrome; the rim, the
/// selected tab's capsule and the backdrop's luminance range are the ones for the text the page
/// colour takes (white on a deep page, as Apple's), so the labels read as they do on the theme's
/// glass. Set as resources over the theme's (MobileTheme.BuildPalette, ShellView's GlassBar*), and
/// removed again for the theme's own.
/// </summary>
internal static class TintedGlass
{
    /// <summary>The glass's alpha over a deep page (white text) and a light one: the theme chrome's
    /// own (#B3 on dark, #99 on Light).</summary>
    public const byte DeepAlpha = 0xB3, LightAlpha = 0x99;

    /// <summary>What a tint sets: the glass, its Reduce Glass fill, its rim, the tab bar's selected
    /// capsule, and the backdrop's luminance range. Text colours are the caller's (a theme scope, or
    /// the page's).</summary>
    public static readonly string[] Keys =
    {
        "PhoneChromeBrush", "PhoneChromePlainBrush", "PhoneChromeOutlineBrush", "PhoneChromeSelectedBrush", "GlassBarMinLuminance", "GlassBarMaxLuminance",
    };

    /// <summary>The glass tint for <paramref name="page"/>, translucent.</summary>
    public static Color Tint(Color page)
    {
        var light = PageTint.PrefersDarkText(page);
        var c = light ? PageTint.Mix(page, Colors.White, 0.35) : PageTint.PlusLighter(page, 0.06);
        return Color.FromArgb(light ? LightAlpha : DeepAlpha, c.R, c.G, c.B);
    }

    /// <summary>The glass's colour over the page itself: what its labels are read against.</summary>
    public static Color Face(Color page)
    {
        var t = Tint(page);
        return PageTint.Mix(page, Color.FromRgb(t.R, t.G, t.B), t.A / 255.0);
    }

    public static void Apply(IResourceDictionary into, Color page)
    {
        var light = PageTint.PrefersDarkText(page);
        into["PhoneChromeBrush"] = new SolidColorBrush(Tint(page));
        // Reduce Glass: the glass's face over the page, opaque (what ApplyText reads the labels against).
        into["PhoneChromePlainBrush"] = new SolidColorBrush(Face(page));
        into["PhoneChromeOutlineBrush"] = new SolidColorBrush(Color.Parse(light ? "#21000000" : "#30FFFFFF"));
        into["PhoneChromeSelectedBrush"] = new SolidColorBrush(Selected(page));
        into["GlassBarMinLuminance"] = light ? 0.25 : 0.0;
        into["GlassBarMaxLuminance"] = light ? 1.0 : 0.45;
    }

    /// <summary>The tab bar's selected capsule on this glass: a shade over the face (translucent black).</summary>
    public static Color Selected(Color page) => Color.Parse(PageTint.PrefersDarkText(page) ? "#14000000" : "#40000000");

    /// <summary>The selected capsule's colour over the glass's face: what the selected tab's accent
    /// glyph and label are read against.</summary>
    public static Color SelectedFace(Color page) => PageTint.Mix(Face(page), Colors.Black, Selected(page).A / 255.0);

    /// <summary>The labels on the tinted glass: the text colour the page takes, the page's
    /// secondary text, and <paramref name="accent"/> stepped toward the text until it reads on the
    /// glass and on the selected tab's capsule (MobileTheme.Readable's 3:1), for a theme scope's
    /// text keys. The capsule counts: the selected tab wears this accent on it, and over a mid-tone
    /// cover colour (dark text) the capsule's shade left it dark on dark, 1.9:1 (tester 11-17).</summary>
    public static void ApplyText(IResourceDictionary into, Color page, Color accent)
    {
        var face = Face(page);
        var text = PageTint.PrefersDarkText(face) ? Colors.Black : Colors.White;
        into["PrimaryTextBrush"] = new SolidColorBrush(text);
        into["PhoneSecondaryTextBrush"] = new SolidColorBrush(PageTint.SubtleTextOn(face, text));
        into["AccentTextExactBrush"] = new SolidColorBrush(MobileTheme.Readable(accent, new[] { face, SelectedFace(page) }, text));
    }

    public static readonly string[] TextKeys = { "PrimaryTextBrush", "PhoneSecondaryTextBrush", "AccentTextExactBrush" };

    public static void Clear(IResourceDictionary from)
    {
        foreach (var key in Keys) from.Remove(key);
        foreach (var key in TextKeys) from.Remove(key);
    }
}
