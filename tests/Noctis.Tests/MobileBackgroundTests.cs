using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>Settings › Background: the saved choice, Slow Motion and Battery Saver, the mesh on
/// the app, the pane's view model, and the contrast of text over every mesh.</summary>
public class MobileBackgroundTests
{
    private sealed class FakeSaver : IBatterySaver
    {
        public bool IsOn { get; set; }
    }

    private static async Task Settled(PhoneBackground background)
    {
        // The Strength save waits for the slider to rest, then queues behind the others.
        await background.PendingSave;
        await background.PendingSave;
    }

    private static int LayerCount() => Application.Current!.Resources.MergedDictionaries
        .OfType<ResourceDictionary>()
        .Count(d => d.ThemeDictionaries.Values.OfType<ResourceDictionary>().Any(t => t["PhoneBackgroundBrush"] is DrawingBrush));

    [AvaloniaFact]
    public async Task ExistingSettings_ReadAsOff_AndNothingIsLayeredOverTheTheme()
    {
        using var rig = MobileFixtures.MakeRig();
        var background = rig.Shell.Background;
        await background.LoadAsync();

        Assert.Equal("Off", background.Choice);
        Assert.Equal("Off", background.Summary);
        Assert.False(background.Motion);
        Assert.Equal(PhoneBackground.DefaultStrength, background.Strength);
        Assert.False(background.IsActive);
        Assert.Equal(0, LayerCount());
        Assert.Equal("Off", new AppSettings().MobileBackground);      // a settings file without the field
    }

    [AvaloniaFact]
    public async Task UnknownSavedChoice_FallsBackToOff()
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileBackground = "Banana";
            s.MobileBackgroundStrength = 7.0;
            await p.SaveSettingsAsync(s);
        });
        await rig.Shell.Background.LoadAsync();

        Assert.Equal("Off", rig.Shell.Background.Choice);
        Assert.Equal(1, rig.Shell.Background.Strength);   // clamped
    }

    [AvaloniaFact]
    public async Task Choice_Motion_AndStrength_AreSaved_AndLoadedByANewRun_WithoutTouchingOtherSettings()
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.Theme = "Smoke";
            s.MobileAppearance = "Dark";
            await p.SaveSettingsAsync(s);
        });
        var first = new PhoneBackground(rig.Persistence);
        await first.LoadAsync();
        first.Choice = "ash";
        first.Motion = true;
        first.Strength = 0.8;
        await Settled(first);

        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal("ash", saved.MobileBackground);
        Assert.True(saved.MobileBackgroundMotion);
        Assert.Equal(0.8, saved.MobileBackgroundStrength, 3);
        Assert.Equal("Smoke", saved.Theme);                       // only our own fields
        Assert.Equal("Dark", saved.MobileAppearance);

        var second = new PhoneBackground(rig.Persistence);
        await second.LoadAsync();
        Assert.Equal("ash", second.Choice);
        Assert.Equal("Ash", second.Summary);
        Assert.True(second.Motion);
        Assert.Equal(0.8, second.Strength, 3);
        first.Choice = "Off";
        second.Choice = "Off";
    }

    [AvaloniaFact]
    public async Task Summary_NamesTheChoice()
    {
        using var rig = MobileFixtures.MakeRig();
        var background = rig.Shell.Background;
        await background.LoadAsync();
        var seen = new System.Collections.Generic.List<string>();
        background.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PhoneBackground.Summary)) seen.Add(background.Summary); };

        background.Choice = PhoneBackground.Artwork;
        background.Choice = "plum-ink";
        background.Choice = "Off";

        Assert.Equal(new[] { "Now Playing", "Plum Ink", "Off" }, seen);
    }

    [AvaloniaFact]
    public async Task ChoosingABackground_LayersTheMeshOverTheTheme_AndOffTakesItAway()
    {
        using var rig = MobileFixtures.MakeRig();
        var background = rig.Shell.Background;
        await background.LoadAsync();

        background.Choice = "ember";
        Assert.Equal(1, LayerCount());
        background.Choice = "harbour";
        Assert.Equal(1, LayerCount());                            // never stacked
        var layer = Application.Current!.Resources.MergedDictionaries.OfType<ResourceDictionary>().Last();
        Assert.IsType<DrawingBrush>(((ResourceDictionary)layer.ThemeDictionaries[ThemeVariant.Dark])["PhoneBackgroundBrush"]);
        Assert.IsType<DrawingBrush>(((ResourceDictionary)layer.ThemeDictionaries[ThemeVariant.Light])["PhoneBackgroundBrush"]);

        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary());   // a theme re-applied over it
        background.Reassert();
        Assert.Same(layer, Application.Current.Resources.MergedDictionaries.OfType<ResourceDictionary>()
            .Last(d => d.ThemeDictionaries.Count > 0));

        background.Choice = "Off";
        Assert.Equal(0, LayerCount());
    }

    [AvaloniaFact]
    public async Task NowPlaying_FollowsTheCover()
    {
        using var rig = MobileFixtures.MakeRig();
        var background = rig.Shell.Background;
        await background.LoadAsync();
        background.Choice = PhoneBackground.Artwork;
        background.Cover = Color.Parse("#C0392B");
        var red = ((ISolidColorBrush)Floor(background.MeshFor(false)!)).Color;
        background.Cover = Color.Parse("#2B6CC0");
        var blue = ((ISolidColorBrush)Floor(background.MeshFor(false)!)).Color;

        Assert.True(red.R > red.B);
        Assert.True(blue.B > blue.R);
        background.Choice = "Off";
    }

    private static IBrush Floor(MeshBrush mesh) =>
        ((GeometryDrawing)((DrawingGroup)mesh.Brush.Drawing!).Children[0]).Brush!;

    [AvaloniaFact]
    public async Task SlowMotion_IsOffByDefault_AndStandsStillWhileBatterySaverIsOn()
    {
        using var rig = MobileFixtures.MakeRig();
        var saver = new FakeSaver();
        var background = new PhoneBackground(rig.Persistence, saver);
        await background.LoadAsync();

        background.Choice = "ash";
        Assert.False(background.Motion);                          // off by default
        Assert.False(background.MotionRuns);
        Assert.False(background.MotionTimerRunning);

        background.Motion = true;
        Assert.True(background.MotionRuns);
        Assert.True(background.MotionTimerRunning);

        // Battery Saver comes on while it drifts: the next check stops it.
        saver.IsOn = true;
        Assert.False(background.MotionRuns);
        for (var i = 0; i < 80 && background.MotionTimerRunning; i++) background.Tick();
        Assert.False(background.MotionTimerRunning);

        // Already on when Slow Motion is switched on: it never starts.
        background.Motion = false;
        background.Motion = true;
        Assert.False(background.MotionRuns);
        Assert.False(background.MotionTimerRunning);

        saver.IsOn = false;
        background.Suspended = true;                              // the app in the background: still
        background.Motion = false;
        background.Motion = true;
        Assert.False(background.MotionTimerRunning);
        background.Suspended = false;
        Assert.True(background.MotionTimerRunning);

        background.Choice = "Off";                                 // nothing showing: nothing runs
        Assert.False(background.MotionTimerRunning);
    }

    [AvaloniaFact]
    public async Task Pane_ListsEightNeutralsAndTwelveColours_AndPicksOne()
    {
        using var rig = MobileFixtures.MakeRig();
        await rig.Shell.Background.LoadAsync();
        var vm = new BackgroundSettingsViewModel(rig.Shell);

        Assert.Equal(8, vm.Neutrals.Count);
        Assert.Equal(12, vm.Colours.Count);
        Assert.Equal("Off", vm.Summary);
        Assert.True(vm.IsOff);

        vm.PickCommand.Execute(vm.Neutrals.First(s => s.Name == "Ash"));
        Assert.Equal("Ash", vm.Summary);
        Assert.Equal("Preview · Ash", vm.PreviewTag);
        Assert.Equal(new[] { "Ash" }, vm.Neutrals.Concat(vm.Colours).Where(s => s.IsSelected).Select(s => s.Name));
        Assert.False(vm.IsOff);

        vm.PickArtworkCommand.Execute(null);
        Assert.Equal("Now Playing", vm.Summary);
        Assert.Empty(vm.Neutrals.Concat(vm.Colours).Where(s => s.IsSelected));

        vm.Motion = true;
        vm.Strength = 0.1;                                        // clamped to the floor
        Assert.True(rig.Shell.Background.Motion);
        Assert.Equal(PhoneBackground.MinStrength, vm.Strength);
        vm.PickOffCommand.Execute(null);
        Assert.Equal("Off", vm.Summary);
    }

    [AvaloniaFact]
    public async Task Pane_Renders_WithItsSwatches()
    {
        using var rig = MobileFixtures.MakeRig();
        await rig.Shell.Background.LoadAsync();
        var vm = new BackgroundSettingsViewModel(rig.Shell);
        var pane = new BackgroundPane { DataContext = vm };
        var window = new Window { Width = 412, Height = 1400, Content = pane };
        window.Show();
        window.UpdateLayout();

        Assert.Equal(20, pane.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("swatch")));
        Assert.NotNull(pane.FindControl<Border>("PreviewCard"));
        window.Close();
    }

    [Fact]
    public void EveryChoice_KeepsTextReadable_InLightAndDark_AtFullStrength()
    {
        // The surfaces over the mesh (Settings cards are translucent): the same text colours as
        // the plain theme, on the worst spot of the mesh. Never worse than the plain theme's own.
        var darkCard = Color.Parse("#E02C2C2E");
        var lightCard = Color.Parse("#CCFFFFFF");
        var darkSecondary = Color.Parse("#8D8D93");
        var darkPlain = MobileTheme.ContrastRatio(darkSecondary, Color.Parse("#2C2C2E"));
        var lightSecondary = Color.Parse("#993C3C43");
        var lightPlain = MobileTheme.ContrastRatio(Over(lightSecondary, Colors.White), Colors.White);

        var covers = new Color?[] { null, Colors.Yellow, Colors.White, Colors.Black, Colors.Red, Color.Parse("#00FF88"), Color.Parse("#7A5A1E") };
        var palettes = BackgroundCatalog.All.SelectMany(c => new[] { BackgroundCatalog.For(c, false), BackgroundCatalog.For(c, true) })
            .Concat(covers.SelectMany(c => new[] { BackgroundCatalog.ForCover(c, false), BackgroundCatalog.ForCover(c, true) }))
            .ToList();
        Assert.Equal((BackgroundCatalog.All.Count() + covers.Length) * 2, palettes.Count);

        for (var i = 0; i < palettes.Count; i++)
        {
            var light = i % 2 == 1;
            var p = palettes[i];
            foreach (var wash in new[] { p.Floor, p.A, p.B, p.C })
            {
                var page = wash;
                var card = Over(light ? lightCard : darkCard, page);
                if (light)
                {
                    Assert.True(MobileTheme.ContrastRatio(Colors.Black, page) >= 12, $"black on page {wash}");
                    Assert.True(MobileTheme.ContrastRatio(Over(lightSecondary, card), card) >= lightPlain * 0.9, $"secondary on light card {wash}");
                }
                else
                {
                    Assert.True(MobileTheme.ContrastRatio(Colors.White, page) >= 8, $"white on page {wash}");
                    Assert.True(MobileTheme.ContrastRatio(darkSecondary, card) >= darkPlain * 0.93, $"secondary on dark card {wash}");
                }
            }
        }
    }

    /// <summary><paramref name="top"/> laid over the opaque <paramref name="under"/>.</summary>
    private static Color Over(Color top, Color under)
    {
        var a = top.A / 255.0;
        byte L(byte t, byte u) => (byte)Math.Round(t * a + u * (1 - a));
        return Color.FromRgb(L(top.R, under.R), L(top.G, under.G), L(top.B, under.B));
    }

    /// <summary>The mesh is one shared brush: every surface showing it must repaint when the
    /// cover, Strength or the drift change its parts in place. A DrawingBrush does not notice its
    /// drawing's gradients changing, so <see cref="MeshBrush"/> flips the brush's own opacity by a
    /// hair on every change, which raises the brush's invalidation (only checkable on a device).</summary>
    [AvaloniaFact]
    public void TheMeshChangesInPlace_AndEveryChangeNudgesTheBrush()
    {
        var mesh = new MeshBrush();
        var palette = BackgroundCatalog.For(BackgroundCatalog.Find("ember")!, false);
        mesh.Set(palette, 1);

        var opacity = mesh.Brush.Opacity;
        mesh.Drift(40);
        Assert.NotEqual(opacity, mesh.Brush.Opacity);

        opacity = mesh.Brush.Opacity;
        mesh.Set(palette, 0.2);
        Assert.NotEqual(opacity, mesh.Brush.Opacity);

        opacity = mesh.Brush.Opacity;
        mesh.Rest();
        Assert.NotEqual(opacity, mesh.Brush.Opacity);
    }
}
