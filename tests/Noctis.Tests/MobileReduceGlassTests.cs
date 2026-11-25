using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>Settings › Appearance › Reduce Glass (beta testers 11-22: "in some menus it just feels
/// laggy"): the menus' card, the floating bars, the header buttons and the tab pill turn into a plain
/// fill that reads nothing beneath it, live, saved with the other phone switches.</summary>
public class MobileReduceGlassTests
{
    private static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);
        }
    }

    // ── The setting ──

    [Fact]
    public async Task ReduceGlass_IsOffByDefault_SavedAndReadBack()
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            Assert.False(s.MobileReduceGlass);                 // the default
            s.MobileReduceGlass = true;
            await p.SaveSettingsAsync(s);
        });
        Assert.False(rig.Shell.ReduceGlass);
        await rig.Shell.InitializeAsync();
        Assert.True(rig.Shell.ReduceGlass);                    // the file was read

        rig.Shell.ReduceGlass = false;
        await rig.Shell.PendingPlayerBarSave;
        Assert.False((await rig.Persistence.LoadSettingsAsync()).MobileReduceGlass);
        rig.Shell.ReduceGlass = true;
        await rig.Shell.PendingPlayerBarSave;
        Assert.True((await rig.Persistence.LoadSettingsAsync()).MobileReduceGlass);
    }

    // ── The surfaces ──

    /// <summary>Every glass surface follows the switch live: off, each frosts (and holds a backdrop
    /// copy once drawn); on, none is glass, each drops its copy and draws again without one, its
    /// fill opaque; off again, the glass is back.</summary>
    [AvaloniaFact]
    public void TheSwitch_TurnsEveryGlassSurfaceIntoAPlainFill_Live()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone", artist: "Band") });
        rig.Shell.GlassTabPill = true;                          // ignored under Reduce Glass
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
            MobileFixtures.Land(window);
            var home = MobileFixtures.Find<HomePage>(view);
            var surfaces = new (string Name, GlassPanel Panel)[]
            {
                ("tab capsule", view.FindControl<GlassPanel>("TabCapsule")!),
                ("mini player", view.FindControl<GlassPanel>("MiniBar")!),
                ("search bubble", view.FindControl<GlassPanel>("SearchBubble")!),
                ("selection bar", view.FindControl<GlassPanel>("SelectionBar")!),
                ("tab pill", view.FindControl<GlassDroplet>("SelectionPill")!),
                ("header profile", MobileFixtures.Named<ProfileButton>(home, "HomeProfile")),
                ("notice", view.FindControl<MenuGlass>("NoticeBanner")!),
            };
            var tabCapsule = surfaces[0].Panel;
            var miniBar = surfaces[1].Panel;
            Pump(3);
            foreach (var (name, panel) in surfaces)
            {
                Assert.False(panel.ReduceGlass, name);
                Assert.True(panel.EffectiveGlassActive, $"{name} frosts with the switch off");
            }
            Assert.NotNull(tabCapsule.Backdrop);               // drawn as glass: it holds what lies beneath
            Assert.NotNull(miniBar.Backdrop);

            rig.Shell.ReduceGlass = true;
            Pump(3);
            foreach (var (name, panel) in surfaces)
            {
                Assert.True(panel.ReduceGlass, name);           // inherited from ShellView
                Assert.False(panel.EffectiveGlassActive, $"{name} is still glass");
                Assert.Null(panel.Backdrop);                    // released, and no new copy was made
            }
            foreach (var (name, panel) in surfaces.Where(s => s.Panel is not GlassDroplet))
                Assert.True(panel.PlainBackground is ISolidColorBrush { Color.A: 255 } b && b.Opacity >= 1, $"{name}'s plain fill is see-through");

            // A menu popping open (its lite frost while it moves, then the full glass) reads nothing either.
            rig.Shell.OpenNowPlayingCommand.Execute(null);
            MobileFixtures.Land(window);
            rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
            Pump(25);
            var card = MobileFixtures.Named<MenuGlass>(view.FindControl<PopMenu>("Menu")!, "Glass");
            Assert.True(card.IsEffectivelyVisible);
            Assert.True(card.ReduceGlass);
            Assert.False(card.EffectiveGlassActive);
            Assert.Null(card.Backdrop);
            Assert.True(card.PlainBackground is ISolidColorBrush { Color.A: 255 }, "the menu card's plain fill is see-through");
            rig.Shell.CloseSheet();
            Pump(15);
            rig.Shell.CloseNowPlayingCommand.Execute(null);
            MobileFixtures.Land(window);

            rig.Shell.ReduceGlass = false;
            Pump(3);
            foreach (var (name, panel) in surfaces.Append(("menu card", card)))
                Assert.True(panel.EffectiveGlassActive, $"{name} is glass again");
            Assert.NotNull(tabCapsule.Backdrop);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Reduced, a panel draws its shadow, its plain fill and its edge, and nothing it would
    /// read beneath: over a red backdrop the fill comes out exactly, where the glass lets red through.
    /// Real Skia only (NOCTIS_TEST_SKIA=1).</summary>
    [AvaloniaFact]
    public async Task AReducedPanel_DrawsItsPlainFill_NotWhatLiesBeneath()
    {
        if (!HeadlessTestApp.RealRendering) Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        var panel = new GlassPanel
        {
            UseAppGlass = false, IsGlassActive = true, BlurRadius = 4, CornerRadius = new CornerRadius(0),
            Background = new SolidColorBrush(Color.Parse("#33FFFFFF")), PlainBackground = new SolidColorBrush(Color.Parse("#2C2C2E")),
            EdgeThickness = 0, Width = 80, Height = 80,
        };
        var host = new Border
        {
            Background = Brushes.Red, Child = panel, Padding = new Thickness(20), Width = 120, Height = 120,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        var window = new Window { Width = 400, Height = 300, Content = new Panel { Children = { host } } };
        window.Show();
        try
        {
            async Task<Color> Middle()
            {
                for (var i = 0; i < 6; i++)
                {
                    Dispatcher.UIThread.RunJobs();
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    await Task.Delay(20);
                }
                var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                using var fb = frame!.Lock();
                var px = new byte[4];
                System.Runtime.InteropServices.Marshal.Copy(fb.Address + 60 * fb.RowBytes + 60 * 4, px, 0, 4);
                return fb.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? Color.FromRgb(px[0], px[1], px[2]) : Color.FromRgb(px[2], px[1], px[0]);
            }
            var glass = await Middle();
            Assert.True(glass.R > glass.G + 40, $"the glass lets the red through ({glass})");
            GlassPanel.SetReduceGlass(host, true);              // inherited, as from ShellView
            Assert.True(panel.ReduceGlass);
            Assert.Equal(Color.Parse("#2C2C2E"), await Middle());
            Assert.Null(panel.Backdrop);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The bars' plain fill is the glass's face over the page (what their labels are checked
    /// against, MobileThemePickerTests), and over an album or artist page the tinted glass's face.</summary>
    [AvaloniaFact]
    public void ThePlainChrome_IsTheGlassFaceOverThePage_InTheThemeAndOnTintedPages()
    {
        foreach (var theme in PhoneThemes.All)
        {
            var palette = MobileTheme.BuildPalette(theme.IsLight ? null : theme.Id);
            var set = (ResourceDictionary)palette.ThemeDictionaries[theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark];
            Color C(string key) => ((ISolidColorBrush)set[key]!).Color;
            Assert.Equal(PhoneThemes.Over(C("PhoneChromeBrush"), C("PhoneBackgroundBrush")), C("PhoneChromePlainBrush"));
        }
        foreach (var page in new[] { Color.Parse("#8E1B1B"), Color.Parse("#F1D9A8"), Color.Parse("#1B2B4A") })
        {
            var into = new ResourceDictionary();
            TintedGlass.Apply(into, page);
            Assert.Equal(TintedGlass.Face(page), ((ISolidColorBrush)into["PhoneChromePlainBrush"]!).Color);
            TintedGlass.Clear(into);
            Assert.False(into.ContainsKey("PhoneChromePlainBrush"));
        }
    }

    /// <summary>The menus' flat card keeps every label readable: the text and the destructive red at
    /// 4.5:1, the second lines at 4.5:1 too, in both themes (the card is opaque, nothing shows through).</summary>
    [AvaloniaFact]
    public void TheFlatMenuCard_KeepsItsTextReadable()
    {
        var glass = new MenuGlass();
        static Color Composite(Color text, Color under)
        {
            var a = text.A / 255.0;
            return Color.FromRgb((byte)Math.Round(text.R * a + under.R * (1 - a)), (byte)Math.Round(text.G * a + under.G * (1 - a)),
                (byte)Math.Round(text.B * a + under.B * (1 - a)));
        }
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var d = (ResourceDictionary)glass.Resources.ThemeDictionaries[variant];
            Color Brush(string key) => ((SolidColorBrush)d[key]!).Color;
            var card = Brush("MenuGlassPlainTint");
            Assert.Equal(255, card.A);
            foreach (var key in new[] { "MenuGlassText", "MenuGlassSecondaryText", "MenuGlassDestructive" })
                Assert.True(PageTint.ContrastRatio(Composite(Brush(key), card), card) >= 4.5, $"{variant} {key} on the flat card");
        }
    }

    // ── Settings ──

    /// <summary>The switch sits in Appearance by Liquid Glass Tab Pill, drives the setting, and
    /// disables the tab pill's switch while on (the pill has no glass to keep then); the pill's own
    /// value is kept for when Reduce Glass goes off.</summary>
    [AvaloniaFact]
    public async Task TheSettingsSwitch_DrivesIt_AndDisablesTheTabPillSwitch()
    {
        using var rig = MobileFixtures.MakeRig();
        await rig.Shell.InitializeAsync();
        rig.Shell.GlassTabPill = true;
        var app = Application.Current!;
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
            await vm.Loaded;
            MobileFixtures.Land(window);
            var page = MobileFixtures.Find<SettingsPage>(view);
            var reduce = MobileFixtures.Named<ToggleButton>(page, "ReduceGlassSwitch");
            var pill = MobileFixtures.Named<ToggleButton>(page, "GlassTabPillSwitch");
            Assert.Equal("Reduce Glass", Avalonia.Automation.AutomationProperties.GetName(reduce));
            Assert.Same(reduce.Parent, pill.Parent);           // the same Appearance card
            Assert.False(reduce.IsChecked);
            Assert.True(pill.IsEnabled);

            reduce.IsChecked = true;
            Assert.True(rig.Shell.ReduceGlass);
            Assert.False(pill.IsEnabled);
            Assert.True(pill.IsChecked);                        // kept
            Assert.False(view.FindControl<GlassDroplet>("SelectionPill")!.EffectiveGlassActive);
            await rig.Shell.PendingPlayerBarSave;
            Assert.True((await rig.Persistence.LoadSettingsAsync()).MobileReduceGlass);

            reduce.IsChecked = false;
            Assert.False(rig.Shell.ReduceGlass);
            Assert.True(pill.IsEnabled);
            Assert.True(rig.Shell.GlassTabPill);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(icons);
        }
    }
}
