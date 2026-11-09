using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Live 11-06 (Dark theme, Album Page Colour on, Liquid Glass off and on): on a light-cover
/// album page the translucent player island turned light grey while its title stayed
/// near-white (233,240,240 on ~#C8C6C2) and its icons white. The island now follows a
/// contrast-checked family for the surface it actually has, and keeps the theme's own
/// colours on dark pages.
/// </summary>
public class IslandLightTintContrastTests
{
    private static readonly Color LavenderHaze = Color.FromRgb(0xE8, 0xE6, 0xE1);
    private static readonly Color LoverPink = Color.FromRgb(0xF6, 0xD5, 0xE0);
    private static readonly Color TakeCareBrown = Color.FromRgb(0x2A, 0x1B, 0x14);
    private static readonly Color ThemePage = Color.FromRgb(0x25, 0x25, 0x25);

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("SpeakerHighIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private sealed record Rig(Window Window, PlaybackBarView Bar, PlayerViewModel Vm)
    {
        public GlassPanel Glass => Bar.FindControl<GlassPanel>("IslandGlass")!;
        public TextBlock Title => Bar.FindControl<TextBlock>("TrackTitleTextBlock")!;

        public void Backdrop(Color? color)
        {
            Bar.BackdropColor = color;
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>The theme's (window-level) value for an island key, above the bar's overrides.</summary>
        public IBrush Theme(string key) =>
            Window.TryFindResource(key, Window.ActualThemeVariant, out var v) ? (IBrush)v! : throw new KeyNotFoundException(key);

        /// <summary>The icons the island paints in IslandIconFill (the transport glyphs and the
        /// lyrics / queue / volume buttons), accent-coloured states left out.</summary>
        public List<IBrush> IconBrushes()
        {
            var accent = Theme("AccentColorBrush");
            var island = Bar.FindControl<Border>("IslandBorder")!;
            var brushes = new List<IBrush>();
            foreach (var v in island.GetVisualDescendants())
            {
                var brush = v switch
                {
                    TransportGlyph g => g.Foreground,
                    LineIcon l => l.Foreground,
                    _ => null,
                };
                if (brush != null && !ReferenceEquals(brush, accent)) brushes.Add(brush);
            }
            return brushes;
        }
    }

    private static Rig Build()
    {
        EnsureAppStyles();
        var vm = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var bar = new PlaybackBarView { DataContext = vm };
        var window = new Window { Width = 900, Height = 300, Content = bar, RequestedThemeVariant = ThemeVariant.Dark };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return new Rig(window, bar, vm);
    }

    private static Color Shown(IBrush brush, Color surface)
    {
        var c = ((ISolidColorBrush)brush).Color;
        return AlbumTintPalette.Flatten(Color.FromArgb((byte)Math.Round(c.A * ((ISolidColorBrush)brush).Opacity), c.R, c.G, c.B), surface);
    }

    private static double Contrast(IBrush brush, Color surface) => AlbumTintPalette.Contrast(Shown(brush, surface), surface);

    private static void AssertReadable(Rig rig, Color surface, string label)
    {
        Assert.True(Contrast(rig.Title.Foreground!, surface) >= 4.5,
            $"{label}: title {rig.Title.Foreground} on {surface} = {Contrast(rig.Title.Foreground!, surface):F2}");
        // The title sits on the track box (IslandTrackBoxColor at Track Box Opacity) too.
        var boxColor = rig.Window.TryFindResource("IslandTrackBoxColor", rig.Window.ActualThemeVariant, out var v) && v is Color c
            ? c : Colors.White;
        var box = IslandContrast.Surface(surface, boxColor, rig.Vm.IslandTrackBoxOpacity);
        Assert.True(Contrast(rig.Title.Foreground!, box) >= 4.5, $"{label}: title on track box {box}");

        var icons = rig.IconBrushes();
        Assert.True(icons.Count >= 5, $"{label}: found {icons.Count} icons");
        foreach (var icon in icons)
            Assert.True(Contrast(icon, surface) >= 3, $"{label}: icon {icon} on {surface} = {Contrast(icon, surface):F2}");

        foreach (var key in new[] { "IslandForegroundSecondary", "IslandSliderFilled" })
        {
            var brush = (IBrush)rig.Glass.FindResource(key)!;
            Assert.True(Contrast(brush, surface) >= 3, $"{label}: {key} {brush} on {surface} = {Contrast(brush, surface):F2}");
        }
    }

    [AvaloniaFact]
    public void LightAlbumPage_GlassOff_TitleAndIconsGoDark()
    {
        var rig = Build();
        foreach (var page in new[] { LavenderHaze, LoverPink })
        {
            rig.Backdrop(page);
            var surface = IslandContrast.SurfaceOf(rig.Glass, page);
            Assert.True(DominantLuminance(surface) > 0.2, $"surface {surface} should be light over {page}");
            Assert.True(rig.Bar.IsOnLightSurface);
            AssertReadable(rig, surface, $"glass off, page {page}");
        }
    }

    [AvaloniaFact]
    public void LightAlbumPage_GlassOn_TitleAndIconsGoDark()
    {
        var rig = Build();
        AppGlass.Set(true, AppGlass.SurfaceTint, AppGlass.SidebarTint);
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(rig.Glass.EffectiveGlassActive);
            rig.Backdrop(LavenderHaze);
            AssertReadable(rig, IslandContrast.SurfaceOf(rig.Glass, LavenderHaze), "glass on");
        }
        finally
        {
            AppGlass.Clear();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>The lead's measured pill colour round the title, whatever opacity made it.</summary>
    [Fact]
    public void MeasuredLiveSurface_GetsDarkFamily()
    {
        var surface = Color.FromRgb(0xC8, 0xC6, 0xC2);
        var overrides = IslandContrast.OverridesFor(surface,
            key => key == "IslandForeground" ? new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)) : null);
        Assert.NotNull(overrides);
        Assert.True(Contrast(overrides!["IslandForeground"], surface) >= 4.5);
        Assert.True(Contrast(overrides["IslandIconFill"], surface) >= 3);
        Assert.True(Contrast(overrides["IslandForegroundSecondary"], surface) >= 4.5);
    }

    [AvaloniaFact]
    public void DarkPages_KeepTheThemeColours()
    {
        var rig = Build();
        var titleBefore = rig.Title.Foreground;
        var iconsBefore = rig.IconBrushes();
        Assert.Same(rig.Theme("IslandForeground"), titleBefore);

        foreach (Color? page in new Color?[] { TakeCareBrown, ThemePage, null })
        {
            rig.Backdrop(page);
            Assert.False(rig.Bar.IsOnLightSurface);
            Assert.Same(titleBefore, rig.Title.Foreground);
            Assert.Equal(iconsBefore, rig.IconBrushes());
            foreach (var key in IslandContrast.Keys)
                Assert.False(rig.Glass.Resources.ContainsKey(key), $"{key} overridden on {page}");
        }

        // Back from a light page to a dark one: the overrides come off again.
        rig.Backdrop(LavenderHaze);
        Assert.NotSame(titleBefore, rig.Title.Foreground);
        rig.Backdrop(TakeCareBrown);
        Assert.Same(titleBefore, rig.Title.Foreground);
        Assert.Equal(iconsBefore, rig.IconBrushes());
    }

    /// <summary>The volume pop-up's own fill is near-opaque (IslandBackground) with Liquid Glass
    /// off, so it keeps the theme's light icons even while the island over a light page goes dark.</summary>
    [AvaloniaFact]
    public void VolumePopup_KeepsReadableColoursOnItsOwnFill()
    {
        var rig = Build();
        rig.Backdrop(LavenderHaze);
        var content = rig.Bar.FindControl<GlassPanel>("VolumeFlyoutContent")!;
        var surface = IslandContrast.SurfaceOf(content, LavenderHaze);
        var icon = (IBrush)content.FindResource("IslandIconFill")!;
        Assert.True(Contrast(icon, surface) >= 3, $"volume icon {icon} on {surface}");
        Assert.Equal(((ISolidColorBrush)rig.Theme("IslandIconFill")).Color, ((ISolidColorBrush)icon).Color);
    }

    [AvaloniaFact]
    public void Backdrop_IsTheAlbumTint_OnlyWhileItRunsUnderTheIsland()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var album = new Album { Id = Guid.NewGuid(), Name = "A", Artist = "B", Tracks = new List<Track>() };
        var page = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), null!);

        Assert.Null(MainWindowViewModel.IslandBackdropFor(null, page));
        page.ApplyTint(LavenderHaze);
        Assert.Equal(LavenderHaze, MainWindowViewModel.IslandBackdropFor(null, page));

        // Tint whole page off, scrolled to the related sections: the theme page is under the island.
        page.TintUnderPlayerBar = false;
        Assert.Null(MainWindowViewModel.IslandBackdropFor(null, page));
        // ...unless the whole window is tinted.
        Assert.Equal(LavenderHaze, MainWindowViewModel.IslandBackdropFor(new SolidColorBrush(LavenderHaze), page));
    }

    private static double DominantLuminance(Color c) => Noctis.Services.DominantColorExtractor.GetRelativeLuminance(c);
}
