using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Theme (beta tester feedback 11-04: "the cards remain the same color no matter what"):
/// Light, the four dark styles and a dark theme per Colour background, each tinting the page, the
/// cards and grouped rows, hairlines, secondary text and the chrome glass. The picker saves, old
/// settings migrate to the closest theme, every theme keeps its text readable (WCAG AA for body
/// text on the cards), and the tab bar, mini player and cards really wear the theme's brushes.
/// Under NOCTIS_TEST_SKIA=1 with NOCTIS_THEME_PICKER_SHOTS=&lt;dir&gt; Home, Settings and the
/// Theme page are saved per theme as themes-&lt;theme&gt;-&lt;screen&gt;.png.
/// </summary>
public class MobileThemePickerTests
{
    private readonly ITestOutputHelper _o;
    public MobileThemePickerTests(ITestOutputHelper o) => _o = o;

    private const double BodyAA = 4.5;

    // ---- The catalog -----------------------------------------------------------------------

    [Fact]
    public void TheCatalog_IsLight_TheFourDarkStyles_AndOneThemePerColourBackground()
    {
        Assert.Equal(new[] { "Light", "Ink", "Smoke", "Dark", "Midnight" }, PhoneThemes.Standards.Select(t => t.Id));
        Assert.Equal(BackgroundCatalog.Colours.Select(c => c.Id).OrderBy(i => i), PhoneThemes.Colours.Select(t => t.Id).OrderBy(i => i));
        Assert.All(PhoneThemes.Colours, t =>
        {
            Assert.Equal(t.Id, t.Pair!.Id);
            Assert.NotNull(t.Accent);                                         // a colour theme defines its accent
            Assert.Equal(t.Pair.Floor, t.Page);                               // its page is the background's deep floor
        });
        Assert.All(PhoneThemes.Standards, t => Assert.Null(t.Accent));    // Light and the dark styles keep the user's
        Assert.Equal(PhoneThemes.All.Count(), PhoneThemes.All.Select(t => t.Id.ToLowerInvariant()).Distinct().Count());
    }

    /// <summary>The point of the feature: on a colour theme the cards, rows and glass are no longer
    /// Ink's neutral greys but carry the background's hue - subtly (an Apple tint, not a paint job).</summary>
    [Fact]
    public void AColourTheme_TintsItsSurfaces_InItsBackgroundsHue_Subtly()
    {
        var ink = PhoneThemes.Find("Ink")!;
        foreach (var t in PhoneThemes.Colours)
        {
            foreach (var (name, surface, neutral) in new[] { ("card", t.Card, ink.Card), ("grouped", t.Grouped, ink.Grouped),
                         ("chrome", t.Chrome, ink.Chrome), ("separator", t.Separator, ink.Separator) })
            {
                Assert.NotEqual(neutral, surface);
                Hsv(surface, out var h, out var s, out var v);
                Hsv(t.Pair!.Top, out var hue, out _, out _);
                Assert.True(HueDistance(h, hue) < 25, $"{t.Id} {name} {surface} hue {h:0} vs {hue:0}");
                Assert.True(s is > 0.08 and < 0.6, $"{t.Id} {name} {surface} saturation {s:0.00} (tinted, not garish)");
                Assert.True(v < 0.45, $"{t.Id} {name} {surface} stays a dark surface ({v:0.00})");
            }
        }
    }

    /// <summary>Every theme, through the real resource stack (overlay, palette, accent guard): body
    /// text at WCAG AA (4.5:1) on the page, the grouped rows and the cards, secondary text at AA too,
    /// both at least 4.5:1 on the chrome glass over the page, and the accent's text and glyphs at
    /// 3:1 on the cards - with the user's default accent and with the theme's own.</summary>
    [AvaloniaFact]
    public void EveryTheme_KeepsBodyAndSecondaryText_AtAA_OnItsCardsRowsAndGlass()
    {
        var app = Application.Current!;
        using var stack = new MobileThemeSweepTests.PhoneStack();
        var failures = new List<string>();
        foreach (var theme in PhoneThemes.All)
        foreach (var follows in new[] { false, true })
        {
            stack.Apply(theme.Id, PhoneThemes.EffectiveAccent(theme.Id, MobileTheme.DefaultAccent, follows));
            var variant = theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
            Color C(string key) => app.TryGetResource(key, variant, out var v) && v is ISolidColorBrush b ? b.Color : throw new Exception(key);
            var page = C("PhoneBackgroundBrush");
            var surfaces = new (string, Color)[]
            {
                ("page", page), ("grouped", C("PhoneGroupedBackgroundBrush")), ("card", C("PhoneCardBrush")),
                ("chrome", PhoneThemes.Over(C("PhoneChromeBrush"), page)),
            };
            foreach (var (where, under) in surfaces)
            {
                Check($"{theme.Id} body on {where}", C("PrimaryTextBrush"), under, BodyAA);
                Check($"{theme.Id} secondary on {where}", C("PhoneSecondaryTextBrush"), under, BodyAA);
                if (where != "chrome") Check($"{theme.Id} accent (follows {follows}) on {where}", C("AccentTextExactBrush"), under, MobileTheme.MinAccentContrast);
            }
        }
        foreach (var f in failures) _o.WriteLine(f);
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        void Check(string what, Color text, Color under, double floor)
        {
            var ratio = MobileTheme.ContrastRatio(PhoneThemes.Over(text, under), under);
            if (ratio < floor) failures.Add($"{what}: {text} on {under} = {ratio:0.00}:1 (< {floor})");
        }
    }

    /// <summary>A colour theme over its own background mesh (Settings › Background, same name): the
    /// cards are the theme's tinted glass, and white body text and the secondary text stay at AA on
    /// them over every wash of the mesh at full strength.</summary>
    [Fact]
    public void AColourTheme_OnItsBackgroundMesh_KeepsAA_OnItsTintedGlass()
    {
        var bg = new PhoneBackground();
        var failures = new List<string>();
        foreach (var theme in PhoneThemes.Darks)
        {
            bg.Reassert(theme.Id);
            var card = bg.DarkGlass(card: true);
            var grouped = bg.DarkGlass(card: false);
            Assert.Equal(theme.Card, Color.FromRgb(card.R, card.G, card.B));      // the theme's card, as glass
            var mesh = BackgroundCatalog.For(theme.Pair ?? BackgroundCatalog.Neutrals[0], light: false);
            foreach (var wash in new[] { mesh.Floor, mesh.A, mesh.B, mesh.C })
            foreach (var glass in new[] { card, grouped })
            {
                var under = PhoneThemes.Over(glass, wash);
                var body = MobileTheme.ContrastRatio(Color.Parse("#F2F2F2"), under);
                var secondary = MobileTheme.ContrastRatio(PhoneThemes.Over(theme.Secondary, under), under);
                if (body < BodyAA || secondary < BodyAA)
                    failures.Add($"{theme.Id} glass {glass} over {wash}: body {body:0.00}, secondary {secondary:0.00}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    // ---- Migration -------------------------------------------------------------------------

    /// <summary>Settings from before themes map to the closest theme: a Colour background becomes
    /// its own theme (so its cards stop being grey), else the saved dark style (Ink for a
    /// desktop-only one); Light and System stay in MobileAppearance as they were. A saved theme wins.</summary>
    [Theory]
    [InlineData("Midnight", "Off", "", "Midnight")]
    [InlineData("Smoke", "slate", "", "Smoke")]              // a Neutral background keeps the dark style
    [InlineData("Gray", "Off", "", "Ink")]                   // the desktop's Gray: the phone always showed Ink
    [InlineData("Ink", "forest", "", "forest")]              // a Colour background: its theme
    [InlineData("Midnight", "Cobalt", "", "cobalt")]         // ids are case-insensitive
    [InlineData("Ink", "Artwork", "", "Ink")]                // Now Playing's mesh: no theme of its own
    [InlineData("Ink", "forest", "rose", "rose")]            // already chosen: kept
    [InlineData("Smoke", "Off", "Light", "Smoke")]           // "Light" is no dark theme: migrated as if empty
    public void OldSettings_MapToTheClosestTheme(string theme, string background, string saved, string expected)
    {
        var settings = new AppSettings { Theme = theme, MobileBackground = background, MobileThemeName = saved };
        Assert.Equal(expected, PhoneThemes.DarkThemeFor(settings));
    }

    [Fact]
    public async Task Migration_WritesOnce_KeepsAChosenAccent_AndALaterBackgroundCannotMoveIt()
    {
        using var rig = MobileFixtures.MakeRig(seed: p => p.UpdateSettingsAsync(s =>
        {
            s.Theme = "Midnight";
            s.MobileBackground = "ember";
            s.MobileAppearance = "Dark";
            s.AccentColorHex = "#12C76F";
        }));
        var settings = await rig.Persistence.LoadSettingsAsync();
        Assert.True(await PhoneThemes.MigrateAsync(rig.Persistence, settings));
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("ember", saved.MobileThemeName);
        Assert.False(saved.MobileAccentFollowsTheme);                       // Emerald was chosen: it stays
        Assert.Equal("Midnight", saved.Theme);                              // the desktop's field is untouched
        Assert.Equal("Dark", saved.MobileAppearance);
        Assert.False(await PhoneThemes.MigrateAsync(rig.Persistence, saved)); // once

        await rig.Persistence.UpdateSettingsAsync(s => s.MobileBackground = "Off");
        Assert.Equal("ember", PhoneThemes.DarkThemeFor(await rig.Persistence.LoadSettingsAsync()));
    }

    [Fact]
    public async Task Migration_OfTheDefaultAccent_MatchesTheTheme()
    {
        using var rig = MobileFixtures.MakeRig(seed: p => p.UpdateSettingsAsync(s => s.MobileBackground = "forest"));
        var settings = await rig.Persistence.LoadSettingsAsync();
        await PhoneThemes.MigrateAsync(rig.Persistence, settings);
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("forest", saved.MobileThemeName);
        Assert.True(saved.MobileAccentFollowsTheme);
        Assert.Equal(PhoneThemes.Find("forest")!.Accent, Color.Parse(PhoneThemes.EffectiveAccent("forest", saved.AccentColorHex, true)));
        Assert.Equal(saved.AccentColorHex, PhoneThemes.EffectiveAccent("Light", saved.AccentColorHex, true));   // Light has none
        Assert.Equal(saved.AccentColorHex, PhoneThemes.EffectiveAccent("forest", saved.AccentColorHex, false));
    }

    // ---- The picker ------------------------------------------------------------------------

    private sealed class FakeThemeHost : IThemeHost
    {
        public (string Appearance, string DarkTheme, string Accent, bool Follows)? Last { get; private set; }
        public void ApplyTheme(string appearance, string darkTheme, string accentHex) => throw new InvalidOperationException("the 4-argument one is called");
        public void ApplyTheme(string appearance, string darkTheme, string accentHex, bool accentFollowsTheme) =>
            Last = (appearance, darkTheme, accentHex, accentFollowsTheme);
    }

    [Fact]
    public async Task Picker_SavesTheTheme_AndMatchTheme_AndASwatchTurnsMatchThemeOff()
    {
        var host = new FakeThemeHost();
        using var rig = MobileFixtures.MakeRig(theme: host, seed: p => p.UpdateSettingsAsync(s => s.Theme = "Gray"));
        var page = new SettingsPageViewModel(rig.Shell);
        await page.LoadAsync();

        page.ChooseThemeCommand.Execute(page.ColourThemes.Single(t => t.Id == "harbour"));
        Assert.Equal(("System", "harbour", MobileTheme.DefaultAccent, true), host.Last);
        Assert.True(page.UsesThemeAccent);
        await page.PendingSave;
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("harbour", saved.MobileThemeName);
        Assert.Equal("Gray", saved.Theme);                                   // a colour theme is the phone's alone

        page.SelectedAccent = SettingsPageViewModel.Accents.Single(a => a.Name == "Gold");
        Assert.False(page.AccentFollowsTheme);                               // the pick is what shows
        Assert.Equal(("System", "harbour", "#F4D24B", false), host.Last);
        Assert.Equal("Gold", page.AccentName);
        page.ChooseThemeCommand.Execute(page.StandardThemes.Single(t => t.Id == "Midnight"));
        Assert.False(page.ShowsMatchTheme);                                  // the dark styles have no accent of their own
        await page.PendingSave;

        saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("Midnight", saved.MobileThemeName);
        Assert.Equal("Midnight", saved.Theme);                               // the desktop's own styles still go to Theme
        Assert.False(saved.MobileAccentFollowsTheme);
        Assert.Equal("#F4D24B", saved.AccentColorHex);

        var again = new SettingsPageViewModel(rig.Shell);
        await again.LoadAsync();
        Assert.Equal("Midnight", again.DarkTheme);
        Assert.False(again.AccentFollowsTheme);
        Assert.True(again.StandardThemes.Single(t => t.Id == "Midnight").IsSelected);
    }

    [Fact]
    public async Task Picker_OneTap_FromLight_ToAColourTheme_AppliesAndSavesOnce()
    {
        var host = new FakeThemeHost();
        using var rig = MobileFixtures.MakeRig(theme: host, seed: p => p.UpdateSettingsAsync(s => s.MobileAppearance = "Light"));
        var page = new SettingsPageViewModel(rig.Shell);
        await page.LoadAsync();
        Assert.True(page.StandardThemes[0].IsSelected);

        var saves = page.SaveCount;
        page.ChooseThemeCommand.Execute(page.ColourThemes.Single(t => t.Id == "orchid"));
        await page.PendingSave;
        Assert.Equal(("Dark", "orchid", MobileTheme.DefaultAccent, true), host.Last);
        Assert.Equal(saves + 1, page.SaveCount);
        Assert.False(page.StandardThemes[0].IsSelected);
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal(("Dark", "orchid"), (saved.MobileAppearance, saved.MobileThemeName));
    }

    // ---- The app wears it ------------------------------------------------------------------

    /// <summary>Mounted with the real stack: the tab bar's and the mini player's glass, the selected
    /// tab's capsule, Settings' cards and the page take the theme's brushes, the selected tab its
    /// accent, and they follow a switch to another theme at once.</summary>
    [AvaloniaFact]
    public void TheTabBar_MiniPlayer_AndCards_WearTheThemesBrushes()
    {
        var song = MobileFixtures.Song("Midnight City", artist: "M83");
        using var stack = new MobileThemeSweepTests.PhoneStack();
        using var rig = MobileFixtures.MakeRig(new[] { song }, new[] { MobileFixtures.MakeAlbum("Hurry Up", "M83", song) });
        stack.Apply("forest", PhoneThemes.EffectiveAccent("forest", MobileTheme.DefaultAccent, true));
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.Player.PlayTracks(new List<Track> { song }, 0);
            rig.Shell.OpenSettingsCommand.Execute(null);
            MobileFixtures.Land(window);
            foreach (var id in new[] { "forest", "cobalt", "Ink" })
            {
                stack.Apply(id, PhoneThemes.EffectiveAccent(id, MobileTheme.DefaultAccent, true));
                MobileFixtures.Land(window);
                var t = PhoneThemes.Find(id)!;
                Assert.Equal(t.Chrome, Solid(view.FindControl<GlassPanel>("TabCapsule")!.Background));
                Assert.Equal(t.Chrome, Solid(view.FindControl<GlassPanel>("MiniBar")!.Background));
                Assert.Equal(Color.Parse("#99000000"), Solid(view.FindControl<GlassDroplet>("SelectionPill")!.Background));
                var settings = MobileFixtures.Find<SettingsPage>(view);
                Assert.Equal(t.Card, Solid(MobileFixtures.Named<Border>(settings, "AppearanceCard").Background));
                Assert.Equal(t.Grouped, Solid(((ISolidColorBrush)view.FindResource(view.ActualThemeVariant, "PhoneSheetBackgroundBrush")!)));
                var selectedTab = view.FindControl<Button>("HomeTab")!.GetVisualDescendants().OfType<PathIcon>().First();
                var accent = Solid(selectedTab.Foreground);
                if (t.Accent is { } own)
                    Assert.True(HueDistance(Hue(accent), Hue(own)) < 12, $"{id}: the selected tab {accent} wears the theme's accent {own}");
                else
                    Assert.Equal(MobileTheme.DefaultAccent, $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>With a Background on, the mesh layer's cards are the theme's glass (they used to be
    /// the same grey on every mesh), and they follow a theme switch.</summary>
    [AvaloniaFact]
    public async Task WithABackground_TheMeshCards_AreTheThemesGlass()
    {
        using var rig = MobileFixtures.MakeRig();
        var background = rig.Shell.Background;
        await background.LoadAsync();
        try
        {
            background.Reassert("forest");
            background.Choice = "forest";
            var layer = Application.Current!.Resources.MergedDictionaries.OfType<ResourceDictionary>().Last();
            var dark = (ResourceDictionary)layer.ThemeDictionaries[ThemeVariant.Dark];
            var forest = PhoneThemes.Find("forest")!;
            Assert.Equal(Color.FromArgb(PhoneBackground.CardGlassAlpha, forest.Card.R, forest.Card.G, forest.Card.B), Solid(dark["PhoneCardBrush"]));
            background.Reassert("Ink");
            Assert.Equal(Color.Parse("#E02C2C2E"), Solid(dark["PhoneCardBrush"]));   // Ink's glass is the one it always was
            Assert.Equal(Color.Parse("#B31C1C1E"), Solid(dark["PhoneGroupedBackgroundBrush"]));
        }
        finally
        {
            background.Choice = "Off";
        }
    }

    // ---- Pictures --------------------------------------------------------------------------

    /// <summary>Home, Settings and the Theme page in every theme (and Forest over its own mesh),
    /// saved when NOCTIS_THEME_PICKER_SHOTS names a folder under NOCTIS_TEST_SKIA=1.</summary>
    [AvaloniaFact]
    public void Shots_HomeAndSettings_InEveryTheme()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_THEME_PICKER_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var a = MobileFixtures.Song("Midnight City", artist: "M83", favourite: true, plays: 9);
        var b = MobileFixtures.Song("Wait", artist: "M83", plays: 4);
        var c = MobileFixtures.Song("Teardrop", artist: "Massive Attack", plays: 7);
        var albums = new[] { MobileFixtures.MakeAlbum("Hurry Up, We're Dreaming", "M83", a, b), MobileFixtures.MakeAlbum("Mezzanine", "Massive Attack", c) };
        using var stack = new MobileThemeSweepTests.PhoneStack();
        var shots = PhoneThemes.All.Select(t => (t.Id, Mesh: (string?)null)).Append(("forest", "forest")).ToList();
        foreach (var (id, mesh) in shots)
        {
            stack.Apply(id, PhoneThemes.EffectiveAccent(id, MobileTheme.DefaultAccent, true));
            using var rig = MobileFixtures.MakeRig(new[] { a, b, c }, albums);
            var label = mesh == null ? id : $"{id}-on-{mesh}";
            var window = MobileFixtures.Mount(rig.Shell, out _);
            try
            {
                if (mesh != null) { rig.Shell.Background.Reassert(id); rig.Shell.Background.Choice = mesh; }
                rig.Shell.Player.PlayTracks(new List<Track> { a, b, c }, 0);
                rig.Shell.SelectTabCommand.Execute(MobileTab.Home);
                Settle(window);
                window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"themes-{label}-home.png"));
                rig.Shell.OpenSettingsCommand.Execute(null);
                Settle(window);
                window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"themes-{label}-settings.png"));
                ((SettingsPageViewModel)rig.Shell.CurrentPage!).Pane = SettingsPane.Theme;
                Settle(window);
                window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"themes-{label}-picker.png"));
            }
            finally
            {
                rig.Shell.Background.Choice = "Off";
                window.Close();
            }
        }
        _o.WriteLine($"{shots.Count * 3} pictures in {dir}");
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 40; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        MobileFixtures.Land(window);
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static Color Solid(object? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static double Hue(Color c) { Hsv(c, out var h, out _, out _); return h; }

    private static double HueDistance(double a, double b) { var d = Math.Abs(a - b) % 360; return d > 180 ? 360 - d : d; }

    private static void Hsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, bl = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, bl));
        var d = max - Math.Min(r, Math.Min(g, bl));
        v = max;
        s = max == 0 ? 0 : d / max;
        h = d == 0 ? 0 : max == r ? 60 * (((g - bl) / d + 6) % 6) : max == g ? 60 * ((bl - r) / d + 2) : 60 * ((r - g) / d + 4);
    }
}
