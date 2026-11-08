using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Xunit;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace Noctis.Tests;

/// <summary>Phone Settings › Appearance: the seventeen accents and Match Artwork, an accent saved
/// outside them, the swatch layout, and the contrast every theme × accent pair keeps.</summary>
public class MobileAppearanceTests
{
    private sealed class FakeThemeHost : IThemeHost
    {
        public (string Appearance, string DarkTheme, string Accent)? Last { get; private set; }
        public void ApplyTheme(string appearance, string darkTheme, string accentHex) => Last = (appearance, darkTheme, accentHex);
    }

    private static readonly string[] Themes = { "Light", "Ink", "Smoke", "Dark", "Midnight" };

    [Fact]
    public void Accents_AreSeventeenDesktopPresets_CrimsonFirst_AndDistinct()
    {
        var accents = SettingsPageViewModel.Accents;
        Assert.Equal(17, accents.Count);
        Assert.Equal(("Crimson", MobileTheme.DefaultAccent), (accents[0].Name, accents[0].Hex));
        // The desktop's own names and colours, exactly.
        Assert.All(accents, a => Assert.Contains(Noctis.App.AccentPresets, p => p.Name == a.Name && p.Hex == a.Hex));
        Assert.Equal(17, accents.Select(a => a.Hex).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // The first nine kept their order (a saved pick reads the same), the eight added after.
        Assert.Equal(new[] { "Crimson", "Tangerine", "Gold", "Emerald", "Teal", "Cerulean", "Indigo", "Orchid", "Rose" },
            accents.Select(a => a.Name).Where(n => new[] { "Crimson", "Tangerine", "Gold", "Emerald", "Teal", "Cerulean", "Indigo", "Orchid", "Rose" }.Contains(n)));
        // Light and dark neutrals among them (beta feedback 11-07).
        Assert.Contains(accents, a => a.Name == "Silver");
        Assert.Contains(accents, a => a.Name == "Slate");
        // Clearly apart: CIE76 ΔE of at least 25 between any two (the nearest pair, Lime and
        // Emerald, is 25.4; Indigo and Violet, left out, were 17, Crimson and Coral 15).
        foreach (var a in accents)
        foreach (var b in accents.Where(b => b != a))
        {
            var d = DeltaE(Color.Parse(a.Hex), Color.Parse(b.Hex));
            Assert.True(d >= 25, $"{a.Name} and {b.Name} are ΔE {d:0} apart");
        }
    }

    private static double DeltaE(Color a, Color b)
    {
        static (double L, double A, double B) Lab(Color c)
        {
            static double Lin(byte v) { var x = v / 255d; return x > 0.04045 ? Math.Pow((x + 0.055) / 1.055, 2.4) : x / 12.92; }
            double r = Lin(c.R), g = Lin(c.G), bl = Lin(c.B);
            var x = (r * 0.4124 + g * 0.3576 + bl * 0.1805) / 0.95047;
            var y = r * 0.2126 + g * 0.7152 + bl * 0.0722;
            var z = (r * 0.0193 + g * 0.1192 + bl * 0.9505) / 1.08883;
            static double F(double t) => t > 0.008856 ? Math.Cbrt(t) : 7.787 * t + 16.0 / 116;
            return (116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z)));
        }
        var (l1, a1, b1) = Lab(a);
        var (l2, a2, b2) = Lab(b);
        return Math.Sqrt(Math.Pow(l1 - l2, 2) + Math.Pow(a1 - a2, 2) + Math.Pow(b1 - b2, 2));
    }

    /// <summary>An accent saved outside the nine (Scarlet, from an earlier build or the desktop)
    /// stays applied, keeps its name on the label and in settings.json, and checks no swatch.</summary>
    [AvaloniaFact]
    public async Task AccentOutsideTheNine_StaysApplied_KeepsItsName_AndChecksNoSwatch()
    {
        var host = new FakeThemeHost();
        using var rig = MobileFixtures.MakeRig(theme: host, seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.Theme = "Ink";
            s.AccentColorHex = "#FF3B30";
            s.AccentPresetName = "Scarlet";
            await p.SaveSettingsAsync(s);
        });
        await WithAppearancePane(rig, (page, vm, window) =>
        {
            Assert.Null(vm.SelectedAccent);
            Assert.Equal("Scarlet", vm.AccentName);
            Assert.Equal("Accent, Scarlet", vm.AccentRowName);
            var list = MobileFixtures.Named<ListBox>(page, "AccentList");
            Assert.Null(list.SelectedItem);
            Assert.All(list.GetVisualDescendants().OfType<Ellipse>().Where(e => e.Classes.Contains("swatch-ring")), r => Assert.Equal(0, r.Opacity));

            // Another change re-applies the theme with the saved accent, not Crimson.
            vm.DarkTheme = "Smoke";
            Assert.Equal(("System", "Smoke", "#FF3B30"), host.Last);
            Assert.Equal("Accent, Scarlet", vm.AccentRowName);
        });
        var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await vm.PendingSave;
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("#FF3B30", saved.AccentColorHex);
        Assert.Equal("Scarlet", saved.AccentPresetName);   // was rewritten to "Custom"

        // Picking one of the nine takes over.
        vm.SelectedAccent = SettingsPageViewModel.Accents.Single(a => a.Name == "Teal");
        Assert.Equal("Accent, Teal", vm.AccentRowName);
        await vm.PendingSave;
        Assert.Equal("Teal", (await rig.Persistence.LoadSettingsAsync()).AccentPresetName);
    }

    /// <summary>Match Theme on a colour theme wears the theme's own accent, so the grid ticks no
    /// swatch (Crimson stayed ticked on Forest); tapping the saved swatch picks it and turns Match
    /// Theme off, and the tick comes back. Match Artwork's swatch hides its tick the same way.</summary>
    [AvaloniaFact]
    public async Task MatchTheme_TicksNoSwatch_AndTappingTheSavedOneTurnsItOff()
    {
        var crimson = SettingsPageViewModel.Accents.Single(a => a.Name == "Crimson");
        using var rig = MobileFixtures.MakeRig(theme: new FakeThemeHost(), seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileAppearance = "Dark";
            s.MobileThemeName = "forest";
            s.MobileAccentFollowsTheme = true;
            s.AccentColorHex = crimson.Hex;
            s.AccentPresetName = "Crimson";
            await p.SaveSettingsAsync(s);
        });
        await WithAppearancePane(rig, (page, vm, window) =>
        {
            var list = MobileFixtures.Named<ListBox>(page, "AccentList");
            Assert.True(vm.UsesThemeAccent);
            Assert.Equal(crimson, vm.SelectedAccent);            // still the saved colour
            Assert.Null(vm.ShownAccent);
            Assert.Null(list.SelectedItem);
            Assert.Equal(Loc.T("Mobile.Accent.MatchTheme"), vm.AccentName);   // the footnote's {0}

            list.SelectedItem = crimson;                          // a tap on the saved swatch
            Assert.False(vm.AccentFollowsTheme);
            Assert.Equal(crimson, list.SelectedItem);
            Assert.Equal(Loc.T("Mobile.Accent.Crimson"), vm.AccentName);

            vm.AccentFollowsTheme = true;                         // the switch back on: the tick goes
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Null(list.SelectedItem);
            Assert.Equal(crimson, vm.SelectedAccent);

            list.SelectedItem = AccentSwatch.Artwork;             // Match Artwork, same rules
            Assert.False(vm.AccentFollowsTheme);
            Assert.True(vm.UsesArtworkAccent);
            Assert.Same(AccentSwatch.Artwork, list.SelectedItem);
            vm.AccentFollowsTheme = true;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Null(list.SelectedItem);
            Assert.False(vm.UsesArtworkAccent);
            Assert.Equal(Loc.T("Mobile.Accent.MatchTheme"), vm.AccentName);
        });
    }

    /// <summary>A colour picked on the desktop's custom picker reads "Custom"; a stale saved name
    /// that is one of the nine is never shown for a colour that is not that swatch.</summary>
    [Theory]
    [InlineData("#123456", "Custom", "Custom")]
    [InlineData("#123456", "Crimson", "Custom")]
    [InlineData("#123456", "", "Custom")]
    [InlineData("#B8BCC4", "Silver", "Silver")]
    [InlineData("#12c76f", "Custom", "Emerald")]
    public async Task AccentName_ForSavedColours(string hex, string savedName, string expected)
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.AccentColorHex = hex;
            s.AccentPresetName = savedName;
            await p.SaveSettingsAsync(s);
        });
        rig.Shell.OpenSettingsCommand.Execute(null);
        var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await vm.Loaded;
        Assert.Equal(expected, vm.AccentName);
    }

    /// <summary>Seventeen swatches and Match Artwork: three full rows of six, each centred, every
    /// cell at least a 44 dp touch target on a 412 dp phone.</summary>
    [AvaloniaFact]
    public async Task Swatches_ThreeRowsOfSix_WithFullTouchTargets()
    {
        using var rig = MobileFixtures.MakeRig();
        await WithAppearancePane(rig, (page, vm, window) =>
        {
            var list = MobileFixtures.Named<ListBox>(page, "AccentList");
            var cells = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.Equal(18, cells.Count);
            Assert.Same(AccentSwatch.Artwork, cells[^1].DataContext);
            var boxes = cells.Select(c => new Rect(c.TranslatePoint(new Point(0, 0), list)!.Value, c.Bounds.Size)).ToList();
            Assert.All(boxes, b => Assert.True(b.Width >= 44 && b.Height >= 44, b.ToString()));
            var rows = new[] { boxes.Take(6).ToList(), boxes.Skip(6).Take(6).ToList(), boxes.Skip(12).ToList() };
            foreach (var row in rows)
            {
                Assert.Equal(6, row.Count);
                Assert.Single(row.Select(b => b.Y).Distinct());
                // Centred on the list: equal space left and right.
                Assert.InRange(row[0].X - (list.Bounds.Width - row[^1].Right), -1.5, 1.5);
            }
            Assert.True(rows[1][0].Y > rows[0][0].Y && rows[2][0].Y > rows[1][0].Y);
        });
    }

    /// <summary>
    /// Every theme × every accent: text, accent labels and glyphs, and glyphs on accent fills
    /// keep their contrast on the surfaces they sit on (WCAG ratios: 4.5 for body text, 3 for
    /// accent text, grey notes and glyphs). Before the guards Emerald / Tangerine labels read
    /// 2.2-2.4:1 on Light's white cards, white on a Tangerine Play pill 2.4:1, and an Indigo
    /// check or selected outline 1.5:1 on Smoke's cards.
    /// </summary>
    [AvaloniaFact]
    public void EveryThemeAndAccent_KeepsItsContrast()
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        var styles = new StyleInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") };
        var addedFont = !app.Resources.ContainsKey("InterSemiBold");
        if (addedFont) app.Resources["InterSemiBold"] = FontFamily.Default;   // Styles.axaml asks for it
        app.Styles.Add(styles);
        var theme = new MobileTheme();
        var failures = new System.Collections.Generic.List<string>();
        try
        {
            // Light, the dark styles and every colour theme (a chosen accent shows on a colour
            // theme too, with Match Theme off).
            foreach (var name in PhoneThemes.All.Select(t => t.Id))
            {
                var variant = name == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
                foreach (var accent in SettingsPageViewModel.Accents)
                {
                    theme.Apply(app, name, accent.Hex);
                    Color Get(string key)
                    {
                        Assert.True(app.TryGetResource(key, variant, out var value), $"{name}: no {key}");
                        return value is Color c ? c : ((ISolidColorBrush)value!).Color;
                    }
                    var surfaces = new[] { "PhoneBackgroundBrush", "PhoneGroupedBackgroundBrush", "PhoneCardBrush" }.Select(k => (k, Get(k))).ToList();
                    void Check(string fg, Color text, Color under, string underName, double min)
                    {
                        var ratio = MobileTheme.ContrastRatio(Over(text, under), under);
                        if (ratio < min) failures.Add($"{name}/{accent.Name}: {fg} on {underName} {ratio:0.00} < {min}");
                    }
                    foreach (var (k, s) in surfaces)
                    {
                        Check("PrimaryTextBrush", Get("PrimaryTextBrush"), s, k, 4.5);
                        Check("SecondaryTextBrush", Get("SecondaryTextBrush"), s, k, 3);
                        Check("PhoneSecondaryTextBrush", Get("PhoneSecondaryTextBrush"), s, k, 3);
                        Check("AccentTextBrush", Get("AccentTextBrush"), s, k, 3);
                        Check("AccentTextExactBrush", Get("AccentTextExactBrush"), s, k, 3);
                    }
                    Check("AccentForegroundBrush", Get("AccentForegroundBrush"), Get("AccentColorBrush"), "AccentColorBrush", 3);
                    Check("ToggleButtonForegroundChecked", Get("ToggleButtonForegroundChecked"), Get("AccentColorBrush"), "AccentColorBrush", 3);
                }
            }
        }
        finally
        {
            theme.Remove(app);
            app.Styles.Remove(styles);
            if (addedFont) app.Resources.Remove("InterSemiBold");
            app.RequestedThemeVariant = before;
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>Crimson, the default, already clears every floor: the guards leave it exactly
    /// as the desktop builds it, on Light and on Ink.</summary>
    [AvaloniaFact]
    public void Guards_LeaveCrimsonUntouched()
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        var theme = new MobileTheme();
        try
        {
            foreach (var (name, variant) in new[] { ("Light", ThemeVariant.Light), ("Ink", ThemeVariant.Dark) })
            {
                theme.Apply(app, name, MobileTheme.DefaultAccent);
                var desktop = Noctis.Helpers.AccentPalette.Build(Color.Parse(MobileTheme.DefaultAccent), name == "Light");
                foreach (var key in new[] { "AccentTextBrush", "AccentTextExactBrush", "AccentForegroundBrush" })
                {
                    Assert.True(app.Resources.TryGetResource(key, variant, out var value));
                    Assert.Equal(((ISolidColorBrush)desktop[key]!).Color, ((ISolidColorBrush)value!).Color);
                }
            }
        }
        finally
        {
            theme.Remove(app);
            app.RequestedThemeVariant = before;
        }
    }

    /// <summary>Now Playing is always dark, and its bars are Apple's white capsules (owner 10-13),
    /// not the accent: whatever the theme and accent (Indigo read 2:1 on the scrim as a fill), the
    /// played part is light translucent white, with no thumb, and stands out from the track.</summary>
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Smoke")]
    [InlineData("Midnight")]
    public void NowPlaying_SeekBar_ReadsOnTheDarkScrim_EvenForIndigo(string themeName)
    {
        var app = Application.Current!;
        var before = app.RequestedThemeVariant;
        var theme = new MobileTheme();
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            theme.Apply(app, themeName, "#4338CA");
            rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
            rig.Shell.OpenNowPlayingCommand.Execute(null);
            window.UpdateLayout();
            var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
            Assert.IsType<CapsuleSlider>(MobileFixtures.Named<Control>(page, "SeekBar"));
            Assert.IsType<CapsuleSlider>(MobileFixtures.Named<Control>(page, "VolumeSlider"));
            Assert.Empty(page.GetVisualDescendants().OfType<Thumb>());
            var scrim = Color.Parse("#121214");
            var fill = Over(Color.FromArgb((byte)Math.Round(CapsuleSlider.FillAlpha * 255), 255, 255, 255), scrim);
            var track = Over(Color.FromArgb((byte)Math.Round(CapsuleSlider.TrackAlpha * 255), 255, 255, 255), scrim);
            var ratio = MobileTheme.ContrastRatio(fill, track);
            Assert.True(ratio >= MobileTheme.MinAccentContrast, $"{themeName}: played part {ratio:0.00}:1 on the track");
        }
        finally
        {
            window.Close();
            theme.Remove(app);
            app.RequestedThemeVariant = before;
        }
    }

    /// <summary>Glyphs, labels and outlines drawn on a page or card take the guarded accent
    /// (AccentTextExactBrush), never the raw AccentColorBrush, which is for fills: an Indigo
    /// check, row icon or selected Look outline read 1.5-1.9:1 on the dark cards. Sliders keep
    /// it (Fluent paints the slider's fill from its Foreground).</summary>
    [Fact]
    public void PhoneViews_DrawNoGlyphOrOutlineInTheRawAccent()
    {
        var views = new DirectoryInfo(Path.Combine(RepoRoot(), "src", "Noctis.Mobile", "Views"));
        var offenders = new System.Collections.Generic.List<string>();
        const string raw = "\\{DynamicResource AccentColorBrush\\}";
        foreach (var file in views.GetFiles("*.axaml"))
        {
            var text = File.ReadAllText(file.FullName);
            foreach (Match style in Regex.Matches(text, "<Style Selector=\"([^\"]*)\">(.*?)</Style>", RegexOptions.Singleline))
                if (!style.Groups[1].Value.Contains("Slider")
                    && Regex.IsMatch(style.Groups[2].Value, $"Property=\"(Foreground|BorderBrush|Stroke)\" Value=\"{raw}\""))
                    offenders.Add($"{file.Name}: {style.Groups[1].Value}");
            foreach (Match attr in Regex.Matches(text, $"\\s(Foreground|BorderBrush|Stroke)=\"{raw}\""))
                offenders.Add($"{file.Name}: {attr.Value.Trim()}");
        }
        Assert.Empty(offenders);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Noctis.Mobile")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }

    /// <summary>A translucent brush as it lands on an opaque surface.</summary>
    private static Color Over(Color top, Color under)
    {
        var a = top.A / 255.0;
        byte Mix(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(Mix(top.R, under.R), Mix(top.G, under.G), Mix(top.B, under.B));
    }

    private static async Task WithAppearancePane(MobileFixtures.Rig rig, Action<SettingsPage, SettingsPageViewModel, Window> check)
    {
        var app = Application.Current!;
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
            await vm.Loaded;
            vm.OpenPaneCommand.Execute(SettingsPane.Accent);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 600)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                System.Threading.Thread.Sleep(5);
            }
            MobileFixtures.Land(window);   // the sheet's rise can outlast the wait
            check(MobileFixtures.Find<SettingsPage>(view), vm, window);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(icons);
        }
    }
}
