using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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
/// Owner 10-20, off Apple Music's artist page: "make sure pop-ups and things are rounded pill, and
/// also red glass, so it just matches the entire thing". On a page in its cover's colour the
/// floating chrome (tab bar, mini player, folded bubbles, the page's back and ⋯ buttons) is glass in
/// that colour with the text it takes, not the theme's milky white (device 10-20: a white tab bar and
/// mini player over a deep red album page in Light); the player's banner is the pop-ups' glass in a pill.
/// </summary>
public class MobileTintedGlassTests : IDisposable
{
    private readonly string _cover = Path.Combine(Path.GetTempPath(), $"noctis-tinted-glass-{Guid.NewGuid():N}.jpg");

    public MobileTintedGlassTests() => File.WriteAllBytes(_cover, new byte[] { 1, 2, 3 });

    public void Dispose()
    {
        try { File.Delete(_cover); } catch { }
    }

    private static readonly Color DeepRed = Color.FromRgb(0x7A, 0x12, 0x14);

    private (MobileFixtures.Rig Rig, Window Window, ShellView View, Album Album) OpenTinted(Color tint)
    {
        var tracks = new[] { MobileFixtures.Song("One", artist: "Cole"), MobileFixtures.Song("Two", artist: "Cole") };
        var album = MobileFixtures.MakeAlbum("Born Sinner", "Cole", tracks);
        album.ArtworkPath = _cover;
        var rig = MobileFixtures.MakeRig(tracks, new[] { album }, tint: () => new PageTint(_ => tint, work => Task.FromResult(work())));
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        MobileFixtures.Land(window);
        return (rig, window, view, album);
    }

    private static Color BrushColor(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    [AvaloniaFact]
    public void OnACoverTintedPage_TheChromeIsGlassInThePageColour_AndTheThemesGlassAgainOffIt()
    {
        var (rig, window, view, _) = OpenTinted(DeepRed);
        using var _ = rig;
        var page = ((AlbumPageViewModel)rig.Shell.CurrentPage!).Tint.TintColor!.Value;
        var scope = view.FindControl<ThemeVariantScope>("ChromeScope")!;
        foreach (var name in new[] { "TabCapsule", "MiniBar", "SearchBubble" })
            Assert.Equal(TintedGlass.Tint(page), BrushColor(view.FindControl<GlassPanel>(name)!.Background));
        // A deep page: the dark glass's labels (white), and its luminance range.
        Assert.Equal(ThemeVariant.Dark, scope.ActualThemeVariant);
        Assert.Equal(0.45, view.FindControl<GlassPanel>("TabCapsule")!.BackdropMaxLuminance, 3);
        var face = TintedGlass.Face(page);
        var label = view.FindControl<Button>("FavoritesTab")!.GetVisualDescendants().OfType<TextBlock>().First();
        Assert.True(MobileTheme.ContrastRatio(BrushColor(label.Foreground), face) >= 4.5, "tab label on the red glass");
        // The selected tab (Home, where the album was opened from) in the accent, stepped until it reads.
        Assert.True(view.FindControl<Button>("HomeTab")!.Classes.Contains("selected"));
        foreach (var part in view.FindControl<Button>("HomeTab")!.GetVisualDescendants().Where(v => v is TextBlock or PathIcon))
        {
            var fg = part is TextBlock t ? t.Foreground : ((PathIcon)part).Foreground;
            Assert.True(MobileTheme.ContrastRatio(BrushColor(fg), face) >= MobileTheme.MinAccentContrast, "selected tab on the red glass");
        }

        // Back to the tab: the theme's own glass.
        rig.Shell.NavigateBackCommand.Execute(null);
        MobileFixtures.Land(window);
        var theme = BrushColor(view.FindResource(view.ActualThemeVariant, "PhoneChromeBrush") as IBrush);
        Assert.Equal(theme, BrushColor(view.FindControl<GlassPanel>("TabCapsule")!.Background));
        Assert.Equal(view.ActualThemeVariant, scope.ActualThemeVariant);
        window.Close();
    }

    /// <summary>The Queue sheet follows the app theme, and the bar stands over it: theme glass there.</summary>
    [AvaloniaFact]
    public void OverTheQueue_TheChromeIsTheThemesGlass()
    {
        var (rig, window, view, _) = OpenTinted(DeepRed);
        using var _ = rig;
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.ToggleQueueCommand.Execute(null);
        MobileFixtures.Land(window);
        var theme = BrushColor(view.FindResource(view.ActualThemeVariant, "PhoneChromeBrush") as IBrush);
        Assert.Equal(theme, BrushColor(view.FindControl<GlassPanel>("TabCapsule")!.Background));
        window.Close();
    }

    /// <summary>The album page's floating back and ⋯ buttons: the page colour's glass, their glyphs
    /// in the page's text colour (they were black on a milky circle in Light over a red page).</summary>
    [AvaloniaFact]
    public void TheHeroButtons_AreGlassInThePageColour_WithThePagesText()
    {
        var (rig, window, view, _) = OpenTinted(DeepRed);
        using var _ = rig;
        var albumPage = MobileFixtures.Find<AlbumPage>(view);
        var page = ((AlbumPageViewModel)rig.Shell.CurrentPage!).Tint.TintColor!.Value;
        var text = BrushColor(albumPage.FindResource("AlbumText") as IBrush);
        foreach (var name in new[] { "BackButton", "MoreButton" })
        {
            var button = MobileFixtures.Named<Button>(albumPage, name);
            var glass = button.GetVisualAncestors().OfType<GlassPanel>().First();
            Assert.Equal(TintedGlass.Tint(page), BrushColor(glass.Background));
            Assert.Equal(text, BrushColor(button.Foreground));
            Assert.True(MobileTheme.ContrastRatio(text, TintedGlass.Face(page)) >= 4.5, $"{name}'s glyph on its glass");
        }
        window.Close();
    }

    /// <summary>Over every kind of page colour - deep, mid and pale, warm and cool - the tinted glass
    /// keeps its labels readable: the text 4.5:1, the accent (stepped as needed) 3:1.</summary>
    [Theory]
    [InlineData(0x7A, 0x12, 0x14)]
    [InlineData(0x4C, 0x20, 0x00)]
    [InlineData(0x14, 0x18, 0x30)]
    [InlineData(0x6E, 0x6E, 0x72)]
    [InlineData(0x2E, 0x8B, 0x57)]
    [InlineData(0xE8, 0xB4, 0xC0)]
    [InlineData(0xF4, 0xD2, 0x4B)]
    [InlineData(0xFF, 0xFF, 0xFF)]
    [InlineData(0x00, 0x00, 0x00)]
    public void TheTintedGlass_KeepsItsLabelsReadable(byte r, byte g, byte b)
    {
        var page = Color.FromRgb(r, g, b);
        var labels = new ResourceDictionary();
        foreach (var accent in SettingsPageViewModel.Accents)
        {
            TintedGlass.ApplyText(labels, page, Color.Parse(accent.Hex));
            var face = TintedGlass.Face(page);
            Assert.True(MobileTheme.ContrastRatio(BrushColor(labels["PrimaryTextBrush"] as IBrush), face) >= 4.5);
            Assert.True(MobileTheme.ContrastRatio(BrushColor(labels["PhoneSecondaryTextBrush"] as IBrush), face) >= 4.5);
            Assert.True(MobileTheme.ContrastRatio(BrushColor(labels["AccentTextExactBrush"] as IBrush), face) >= MobileTheme.MinAccentContrast,
                $"{accent.Name} on {page}");
        }
    }

    /// <summary>The player's banner ("Can't reach your computer"): the pop-ups' glass, every setting
    /// of it, in a pill, its text the glass's - it was a hard-coded #F22C2C2E box with white text.</summary>
    [AvaloniaFact]
    public void TheNoticeBanner_IsThePopUpsGlass_InAPill()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.Notice = NowPlayingViewModel.ComputerUnreachableStopped;
        window.UpdateLayout();
        var banner = Assert.IsType<MenuGlass>(view.FindControl<Control>("NoticeBanner"));
        Assert.True(banner.IsVisible && banner.Bounds.Height > 0);
        var reference = new MenuGlass();
        foreach (var p in new AvaloniaProperty[]
                 {
                     GlassPanel.UseAppGlassProperty, GlassPanel.IsGlassActiveProperty, GlassPanel.BlurRadiusProperty,
                     GlassPanel.SpecularStyleProperty, GlassPanel.BackdropLiftProperty, GlassPanel.EdgeThicknessProperty,
                 })
            Assert.True(Equals(reference.GetValue(p), banner.GetValue(p)), $"the banner overrides {p.Name}");
        // A pill on one line; a long message wraps into the same round-ended card.
        Assert.True(banner.CornerRadius.TopLeft >= Math.Min(22, banner.Bounds.Height / 2) - 0.5, $"radius {banner.CornerRadius} for {banner.Bounds.Height}");
        var text = view.FindControl<TextBlock>("NoticeText")!;
        Assert.Same(banner.FindResource(banner.ActualThemeVariant, "MenuGlassText"), text.Foreground);
        window.Close();
    }
}
