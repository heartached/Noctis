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

    /// <summary>What a tint sets: the glass, its rim, the tab bar's selected capsule, and the
    /// backdrop's luminance range. Text colours are the caller's (a theme scope, or the page's).</summary>
    public static readonly string[] Keys =
    {
        "PhoneChromeBrush", "PhoneChromeOutlineBrush", "PhoneChromeSelectedBrush", "GlassBarMinLuminance", "GlassBarMaxLuminance",
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
        into["PhoneChromeOutlineBrush"] = new SolidColorBrush(Color.Parse(light ? "#21000000" : "#30FFFFFF"));
        into["PhoneChromeSelectedBrush"] = new SolidColorBrush(Color.Parse(light ? "#14000000" : "#40000000"));
        into["GlassBarMinLuminance"] = light ? 0.25 : 0.0;
        into["GlassBarMaxLuminance"] = light ? 1.0 : 0.45;
    }

    /// <summary>The labels on the tinted glass: the text colour the page takes, the page's
    /// secondary text, and <paramref name="accent"/> stepped toward the text until it reads on the
    /// glass (MobileTheme.Readable's 3:1), for a theme scope's text keys.</summary>
    public static void ApplyText(IResourceDictionary into, Color page, Color accent)
    {
        var face = Face(page);
        var text = PageTint.PrefersDarkText(face) ? Colors.Black : Colors.White;
        into["PrimaryTextBrush"] = new SolidColorBrush(text);
        into["PhoneSecondaryTextBrush"] = new SolidColorBrush(PageTint.SubtleTextOn(face, text));
        into["AccentTextExactBrush"] = new SolidColorBrush(MobileTheme.Readable(accent, new[] { face }, text));
    }

    public static readonly string[] TextKeys = { "PrimaryTextBrush", "PhoneSecondaryTextBrush", "AccentTextExactBrush" };

    public static void Clear(IResourceDictionary from)
    {
        foreach (var key in Keys) from.Remove(key);
        foreach (var key in TextKeys) from.Remove(key);
    }
}
