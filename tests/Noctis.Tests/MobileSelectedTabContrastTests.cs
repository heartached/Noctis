using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Noctis.Mobile.Services;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Tester recording 11-17 (cover-coloured pages, a brown accent): the selected tab's glyph and
/// label, in the accent (MobileStyles Button.tab.selected: AccentTextExactBrush), read dark on dark
/// inside the selected capsule while the other tabs were clear. The accent was guarded against the
/// page, the grouped page and the card (the theme glass) or the glass's face (a cover-tinted glass),
/// never against the capsule it sits on: over a mid-tone cover the tinted glass's capsule left it at
/// 1.9-2.5:1. Now the capsule is one of the surfaces it must clear (3:1).
/// </summary>
public class MobileSelectedTabContrastTests
{
    /// <summary><paramref name="top"/> painted over the opaque <paramref name="under"/>.</summary>
    private static Color Over(Color top, Color under)
    {
        var a = top.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(top.R * a + under.R * (1 - a)),
            (byte)Math.Round(top.G * a + under.G * (1 - a)),
            (byte)Math.Round(top.B * a + under.B * (1 - a)));
    }

    private static List<Color> Sweep()
    {
        var colours = new List<Color>();
        for (var hue = 0; hue < 360; hue += 15)
            foreach (var (s, l) in new[] { (0.9, 0.12), (0.6, 0.2), (0.5, 0.3), (0.6, 0.5), (0.4, 0.75), (0.3, 0.9) })
                colours.Add(new HslColor(1, hue, s, l).ToRgb());
        return colours;
    }

    /// <summary>Over an album, artist or playlist page in its cover's colour (TintedGlass): every page
    /// colour and every accent, the selected tab's accent clears 3:1 on the selected capsule.</summary>
    [Fact]
    public void OnACoverTintedBar_TheSelectedTab_ReadsOnItsCapsule()
    {
        var failures = new List<string>();
        var colours = Sweep();
        foreach (var page in colours)
        foreach (var accent in colours)
        {
            var d = new Avalonia.Controls.ResourceDictionary();
            TintedGlass.Apply(d, page);
            TintedGlass.ApplyText(d, page, accent);
            var fg = ((ISolidColorBrush)d["AccentTextExactBrush"]!).Color;
            var capsule = Over(((ISolidColorBrush)d["PhoneChromeSelectedBrush"]!).Color, TintedGlass.Face(page));
            var r = MobileTheme.ContrastRatio(fg, capsule);
            // Where even the glass's text colour falls under 3:1 on the capsule (a few mid reds, 2.95),
            // the accent goes all the way to it: as readable as anything on that capsule can be.
            var text = ((ISolidColorBrush)d["PrimaryTextBrush"]!).Color;
            var floor = Math.Min(MobileTheme.MinAccentContrast, MobileTheme.ContrastRatio(text, capsule));
            if (r < floor - 1e-9) failures.Add($"page {page} accent {accent}: {fg} on {capsule} {r:0.00}");
            // Still on the glass itself (the accent's other uses on the bar).
            var onFace = MobileTheme.ContrastRatio(fg, TintedGlass.Face(page));
            if (onFace < MobileTheme.MinAccentContrast) failures.Add($"page {page} accent {accent}: on the face {onFace:0.00}");
        }
        Assert.True(failures.Count == 0, failures.Count + " failures:\n" + string.Join("\n", failures.Take(30)));
    }

    /// <summary>The 11-17 case itself: a mid-tone brown page, a brown accent (2.0:1 before).</summary>
    [Fact]
    public void ABrownPage_AndABrownAccent_KeepTheSelectedTabReadable()
    {
        var page = Color.Parse("#A0703C");
        var accent = Color.Parse("#734C26");
        var d = new Avalonia.Controls.ResourceDictionary();
        TintedGlass.Apply(d, page);
        TintedGlass.ApplyText(d, page, accent);
        var fg = ((ISolidColorBrush)d["AccentTextExactBrush"]!).Color;
        Assert.True(MobileTheme.ContrastRatio(fg, TintedGlass.SelectedFace(page)) >= MobileTheme.MinAccentContrast);
        // Guarded against the glass's face alone (before), it fell under the floor on the capsule.
        var text = Noctis.Mobile.ViewModels.PageTint.PrefersDarkText(TintedGlass.Face(page)) ? Colors.Black : Colors.White;
        var faceOnly = MobileTheme.Readable(accent, new[] { TintedGlass.Face(page) }, text);
        Assert.True(MobileTheme.ContrastRatio(faceOnly, TintedGlass.SelectedFace(page)) < MobileTheme.MinAccentContrast);
    }

    /// <summary>On the theme's own glass, every theme and every preset, theme or cover accent: the
    /// selected tab clears 3:1 on its capsule (the glass over the page, the capsule over that).</summary>
    [AvaloniaFact]
    public void OnTheThemeBar_TheSelectedTab_ReadsOnItsCapsule_OnEveryThemeAndAccent()
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        var theme = new MobileTheme();
        var failures = new List<string>();
        var accents = new List<string> { MobileTheme.DefaultAccent };
        foreach (var c in Sweep())
            if (PhoneThemes.ArtworkAccentHex(c) is { } hex) accents.Add(hex);
        accents.AddRange(PhoneThemes.All.Where(t => t.Accent != null).Select(t => $"#{t.Accent!.Value.R:X2}{t.Accent.Value.G:X2}{t.Accent.Value.B:X2}"));
        accents.AddRange(new[] { "#FFFF00", "#36A16B", "#CC669B" });   // the three under 3:1 on Light before
        try
        {
            foreach (var name in PhoneThemes.All.Select(t => t.Id))
            {
                var variant = name == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
                foreach (var hex in accents.Distinct())
                {
                    theme.Apply(app, name, hex);
                    Color Get(string key)
                    {
                        Assert.True(app.TryGetResource(key, variant, out var value), $"{name}: no {key}");
                        return value is Color c ? c : ((ISolidColorBrush)value!).Color;
                    }
                    var capsule = Over(Get("PhoneChromeSelectedBrush"), Over(Get("PhoneChromeBrush"), Get("PhoneBackgroundBrush")));
                    var ratio = MobileTheme.ContrastRatio(Get("AccentTextExactBrush"), capsule);
                    if (ratio < MobileTheme.MinAccentContrast) failures.Add($"{name}/{hex}: {Get("AccentTextExactBrush")} on {capsule} {ratio:0.00}");
                }
            }
        }
        finally
        {
            theme.Remove(app);
            app.RequestedThemeVariant = before;
        }
        Assert.True(failures.Count == 0, failures.Count + " failures:\n" + string.Join("\n", failures.Take(40)));
    }
}
