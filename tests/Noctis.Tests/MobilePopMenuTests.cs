using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Animation;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>Every ⋯ menu on the phone (owner 10-13): Apple's glass card popping out of the tapped
/// button with the desktop menu's motion, the item's actions in it, on screen wherever the button
/// is, and every way out of it.</summary>
public class MobilePopMenuTests(ITestOutputHelper output)
{
    private static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);   // the frame clock is the wall clock
        }
    }

    /// <summary>A real tap on <paramref name="target"/>'s centre: press and release.</summary>
    private static void Tap(Window window, Visual target)
    {
        window.UpdateLayout();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static Rect BoxIn(Visual v, Visual root) => new(v.TranslatePoint(default, root)!.Value, v.Bounds.Size);

    private static (MobileFixtures.Rig rig, Window window, ShellView view) OpenNowPlaying()
    {
        var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone", artist: "Band") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);   // the player takes touches once it has risen
        return (rig, window, view);
    }

    private static PopMenu MenuOf(ShellView view) => view.FindControl<PopMenu>("Menu")!;

    private static double ScaleOf(Visual v) => v.RenderTransform?.Value.M11 ?? 1;

    [AvaloniaFact]
    public void NowPlayingMore_PopsTheMenuOutOfTheButton_WithTheSongsActions_NotTheSheet()
    {
        var (rig, window, view) = OpenNowPlaying();
        using var _ = rig;
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        var more = MobileFixtures.Named<Button>(page, "NowPlayingMore");
        Tap(window, more);
        Pump(30);

        var menu = MenuOf(view);
        var card = menu.FindControl<Panel>("Card")!;
        Assert.True(menu.IsVisible);
        Assert.Same(more, menu.Anchor);
        Assert.False(view.FindControl<ContextSheet>("Sheet")!.IsVisible);   // not the bottom sheet
        Assert.Equal(1, ScaleOf(card), 3);
        Assert.Equal(1, MobileFixtures.Named<GlassPanel>(menu, "Glass").Fade, 3);

        // Low on the screen, it opens up: its bottom-right corner on the ⋯ button's.
        var cardBox = BoxIn(card, view);
        var moreBox = BoxIn(more, view);
        Assert.Equal(moreBox.Bottom, cardBox.Bottom, 1);
        Assert.Equal(moreBox.Right, cardBox.Right, 1);
        Assert.True(cardBox.Top > 0);

        foreach (var name in new[] { "MenuFavourite", "MenuAddToPlaylist", "MenuPlayNext", "MenuAddToQueue", "MenuPin",
                     "MenuGoToAlbum", "MenuGoToArtist", "MenuLyrics" })
            Assert.True(MobileFixtures.Named<Control>(menu, name).IsEffectivelyVisible, name);
        var texts = menu.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Band", texts);                                 // Go to Artist's second line
        window.Close();
    }

    /// <summary>The phone's pop (tester 11-04): the card starts at <see cref="PopMenu.PopScale"/>
    /// with its frost and the dim faded out, holds that pose until it has been drawn, then springs
    /// out of the ⋯ button (most of the way at once, a hair past, settled), the frost and the dim
    /// in together; the close is quicker and heads back into the button, inert at once, hiding
    /// the card after it.</summary>
    [AvaloniaFact]
    public void TheMenu_GrowsOutOfTheButton_AndShrinksBackIntoIt_Smoothly()
    {
        var (rig, window, view) = OpenNowPlaying();
        using var _ = rig;
        var menu = MenuOf(view);
        var card = menu.FindControl<Panel>("Card")!;
        var glass = menu.FindControl<GlassPanel>("Glass")!;
        var dim = menu.FindControl<Border>("Dim")!;
        var more = MobileFixtures.Named<Button>(view, "NowPlayingMore");

        // A tap's order (Click, then the command) without the input pass running the frames.
        more.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        more.Command!.Execute(null);
        Assert.Same(more, menu.Anchor);
        Assert.Equal(PopMenu.PopScale, ScaleOf(card), 3);                // the start, before any frame
        Assert.Equal(0, glass.Fade);
        Assert.Equal(0, dim.Opacity);
        // It grows out of the button: the origin is the ⋯ button's centre inside the card.
        var origin = card.RenderTransformOrigin.ToPixels(card.Bounds.Size);
        var centre = more.TranslatePoint(new Point(more.Bounds.Width / 2, more.Bounds.Height / 2), menu)!.Value
            - card.Bounds.Position;
        Assert.Equal(centre.X, origin.X, 1);
        Assert.Equal(centre.Y, origin.Y, 1);
        // The start pose is held until it has been drawn (two frames on): no run starts inside
        // the slow first frame. (Headless, the dispatcher runs those frames as jobs.)
        Assert.Null(card.Transitions);

        var scales = new List<double> { PopMenu.PopScale };
        var fades = new List<double> { 0 };
        var dims = new List<double> { 0 };
        var checkedTransitions = false;
        for (var i = 0; i < 40; i++)
        {
            Pump(1);
            scales.Add(ScaleOf(card));
            fades.Add(glass.Fade);
            dims.Add(dim.Opacity);
            if (!checkedTransitions && card.Transitions is { Count: > 0 })
            {
                checkedTransitions = true;
                var grow = Assert.IsType<TransformOperationsTransition>(Assert.Single(card.Transitions));
                Assert.Equal(TimeSpan.FromMilliseconds(PopMenu.PopOpenMs), grow.Duration);
                Assert.IsType<PopSpringEase>(grow.Easing);
                var frost = Assert.IsType<DoubleTransition>(Assert.Single(glass.Transitions!));
                var shade = Assert.IsType<DoubleTransition>(Assert.Single(dim.Transitions!));
                Assert.Equal(TimeSpan.FromMilliseconds(PopMenu.PopFadeMs), frost.Duration);
                Assert.Equal(frost.Duration, shade.Duration);                // the dim fades with the frost
                Assert.Same(frost.Easing, shade.Easing);
            }
        }
        Assert.True(checkedTransitions);
        output.WriteLine("open scale: " + string.Join(" ", scales.Select(v => v.ToString("0.0000"))));
        output.WriteLine("open fade:  " + string.Join(" ", fades.Select(v => v.ToString("0.000"))));
        output.WriteLine("open dim:   " + string.Join(" ", dims.Select(v => v.ToString("0.000"))));
        Assert.Contains(scales, s => s > PopMenu.PopScale + 0.02 && s < 0.99);   // frames in between: a glide, not a snap
        Assert.Equal(1, scales[^1], 3);
        Assert.Equal(1, fades[^1], 3);
        Assert.Equal(1, dims[^1], 3);
        Assert.All(scales, s => Assert.True(s <= 1.006, $"overshoot {s}: a hair past, not a bounce"));
        // Rising to its peak without a step back on the way up.
        var peak = scales.IndexOf(scales.Max());
        for (var i = 1; i <= peak; i++)
            Assert.True(scales[i] >= scales[i - 1] - 1e-6, $"scale fell back at frame {i}: {string.Join(", ", scales)}");

        rig.Shell.CloseSheet();
        Assert.False(menu.IsHitTestVisible);                             // inert while it fades
        Assert.True(menu.IsVisible);
        var shrink = Assert.IsType<TransformOperationsTransition>(Assert.Single(card.Transitions!));
        Assert.Equal(TimeSpan.FromMilliseconds(PopMenu.PopCloseMs), shrink.Duration);
        Assert.True(shrink.Duration < TimeSpan.FromMilliseconds(PopMenu.PopOpenMs), "the close is quicker than the open");
        Assert.Equal(origin, card.RenderTransformOrigin.ToPixels(card.Bounds.Size));   // back into the button
        var closing = new List<double>();
        var closingFade = new List<double>();
        for (var i = 0; i < (int)(PopMenu.PopCloseMs / 16) + 8 && menu.IsVisible; i++)
        {
            Pump(1);
            closing.Add(ScaleOf(card));
            closingFade.Add(glass.Fade);
        }
        output.WriteLine("close scale: " + string.Join(" ", closing.Select(v => v.ToString("0.000"))));
        output.WriteLine("close fade:  " + string.Join(" ", closingFade.Select(v => v.ToString("0.000"))));
        Assert.Contains(closing, s => s > PopMenu.PopCloseScale + 1e-3 && s < 1 - 1e-3);  // shrinks through the frames
        Assert.False(menu.IsVisible);
        window.Close();
    }

    /// <summary>The open curve: a spring that is most of the way there at once, runs a hair past
    /// and has settled (no visible snap) when the run ends; the close is shorter, its shrink
    /// leaving at once while its fade holds a moment.</summary>
    [Fact]
    public void ThePopCurves_SpringOutQuickly_AndSettle_TheCloseIsShorter()
    {
        var ease = new PopSpringEase();
        Assert.Equal(0, ease.Ease(0));
        Assert.Equal(1, ease.Ease(1));
        var samples = Enumerable.Range(0, 1001).Select(i => ease.Ease(i / 1000.0)).ToList();
        var first90 = samples.FindIndex(v => v >= 0.9) / 1000.0;
        output.WriteLine($"90 % at {first90:0.000} of the run ({first90 * PopMenu.PopOpenMs:0} ms), peak {samples.Max():0.0000}");
        Assert.InRange(first90, 0.1, 0.35);                              // quick out of the button
        Assert.InRange(samples.Max(), 1.01, 1.04);                       // a hair past
        Assert.InRange(samples[999], 0.997, 1.003);                      // settled: the last frame does not snap
        Assert.True(PopMenu.PopCloseMs < PopMenu.PopOpenMs);
        Assert.True(PopMenu.PopCloseEase.Ease(0.25) > 0.5);
        Assert.True(PopMenu.PopCloseFadeEase.Ease(0.25) < 0.15);
    }

    /// <summary>The origin follows the anchor (the ⋯ button, then the quality badge: two different
    /// points), and while the card moves nothing about it is laid out again: only its render
    /// transform, its frost and opacities change, so no frame of the run costs a layout pass.</summary>
    [AvaloniaFact]
    public void TheOrigin_FollowsTheAnchor_AndTheRunLaysNothingOut()
    {
        var song = MobileFixtures.Song("Tone", artist: "Band");
        song.Codec = "FLAC"; song.BitsPerSample = 24; song.SampleRate = 96000;
        using var rig = MobileFixtures.MakeRig(new[] { song });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);
        var menu = MenuOf(view);
        var card = menu.FindControl<Panel>("Card")!;
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;

        var origins = new List<Point>();
        foreach (var name in new[] { "NowPlayingMore", "QualityBadge" })
        {
            var anchor = MobileFixtures.Named<Button>(page, name);
            anchor.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            anchor.Command!.Execute(null);
            Assert.Same(anchor, menu.Anchor);
            var origin = card.RenderTransformOrigin.ToPixels(card.Bounds.Size) + card.Bounds.Position;
            var centre = anchor.TranslatePoint(new Point(anchor.Bounds.Width / 2, anchor.Bounds.Height / 2), menu)!.Value;
            Assert.Equal(centre.X, origin.X, 1);
            origins.Add(origin);

            // From the first moving frame on, the card is never measured or arranged again.
            Pump(3);
            Assert.NotNull(card.Transitions);
            var bounds = card.Bounds;
            var invalidated = 0;
            void Watch(object? s, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property == Visual.BoundsProperty || e.Property == Layoutable.DesiredSizeProperty) invalidated++;
            }
            card.PropertyChanged += Watch;
            var glass = menu.FindControl<GlassPanel>("Glass")!;
            glass.PropertyChanged += Watch;
            for (var i = 0; i < 25; i++)
            {
                Pump(1);
                Assert.True(card.IsMeasureValid && card.IsArrangeValid);
            }
            card.PropertyChanged -= Watch;
            glass.PropertyChanged -= Watch;
            Assert.Equal(bounds, card.Bounds);
            Assert.Equal(0, invalidated);
            Assert.Equal(1, ScaleOf(card), 3);

            rig.Shell.CloseSheet();
            Pump((int)(PopMenu.PopCloseMs / 16) + 8);
            Assert.False(menu.IsVisible);
        }
        output.WriteLine($"origins {origins[0]} {origins[1]}");
        Assert.NotEqual(origins[0], origins[1]);
        window.Close();
    }

    /// <summary>Still frames of the open at 0, 25, 50 and 100 % of its run, for a look (set
    /// NOCTIS_MENU_SHOTS to a folder; real rendering only). Each pose is set from the curves at
    /// that point of the run, not caught off the wall clock, which Skia's slow frames outran.</summary>
    [AvaloniaFact]
    public void Shots_TheMenuOpening()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_MENU_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var (rig, window, view) = OpenNowPlaying();
        using var _ = rig;
        var menu = MenuOf(view);
        var card = menu.FindControl<Panel>("Card")!;
        var glass = menu.FindControl<GlassPanel>("Glass")!;
        var body = menu.FindControl<ScrollViewer>("Body")!;
        var dim = menu.FindControl<Border>("Dim")!;
        var more = MobileFixtures.Named<Button>(view, "NowPlayingMore");
        more.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        more.Command!.Execute(null);
        Pump(30);
        var spring = new PopSpringEase();
        foreach (var pct in new[] { 0, 25, 50, 100 })
        {
            var t = pct / 100.0;
            var fade = PopMenu.PopFadeEase.Ease(Math.Min(1, t * PopMenu.PopOpenMs / PopMenu.PopFadeMs));
            var scale = PopMenu.PopScale + (1 - PopMenu.PopScale) * spring.Ease(t);
            foreach (var c in new Control[] { card, glass, body, dim }) c.Transitions = null;
            glass.Fade = fade;
            body.Opacity = fade;
            dim.Opacity = fade;
            card.RenderTransform = new PopPose(scale, 0).Transform();
            Pump(2);
            window.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"menus-{pct:000}.png"));
            output.WriteLine($"{pct} % ({t * PopMenu.PopOpenMs:0} ms): scale {scale:0.000}, fade {fade:0.000}");
        }
        window.Close();
    }

    [AvaloniaFact]
    public void BackAndATapOutside_CloseTheMenu_WithoutTouchingWhatIsUnderneath()
    {
        var (rig, window, view) = OpenNowPlaying();
        using var _ = rig;
        var more = MobileFixtures.Named<Button>(view, "NowPlayingMore");

        Tap(window, more);
        Pump(20);
        Assert.True(rig.Shell.TryHandleBack());
        Assert.False(rig.Shell.IsMenuOpen);
        Assert.True(rig.Shell.IsNowPlayingOpen);

        Tap(window, more);
        Pump(20);
        var play = MobileFixtures.Named<Button>(view, "PlayPauseButton");
        var wasPlaying = rig.Shell.Player.IsPlaying;
        Tap(window, play);
        Assert.False(rig.Shell.IsMenuOpen);
        Assert.Equal(wasPlaying, rig.Shell.Player.IsPlaying);            // the tap only closed the menu
        Assert.True(rig.Shell.IsNowPlayingOpen);

        rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
        rig.Shell.CloseNowPlayingCommand.Execute(null);                  // closing the player takes it too
        Assert.False(rig.Shell.IsMenuOpen);
        window.Close();
    }

    /// <summary>
    /// Now Playing's artist line (owner 10-14, Apple Music's): a tap pops a small glass bubble out
    /// of the artist with only Go to Album (the album under it) and Go to Artist (every credited
    /// artist under it), sitting just above the text with its lower-left corner by it, growing
    /// out of it. Back or a tap outside closes only the bubble; each row closes the player and
    /// opens its page, the artist row the one the ⋯ menu's Go to Artist opens.
    /// </summary>
    [AvaloniaFact]
    public void TheArtistLine_PopsGoToAlbumAndGoToArtist_OutOfItself()
    {
        var song = MobileFixtures.Song("Leave the Door Open", artist: "Bruno Mars, Anderson .Paak & Silk Sonic");
        var album = MobileFixtures.MakeAlbum("An Evening with Silk Sonic", "Bruno Mars", song);
        using var rig = MobileFixtures.MakeRig(new[] { song }, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var menu = MenuOf(view);
        var card = menu.FindControl<Panel>("Card")!;

        void OpenPlayer()
        {
            rig.Shell.OpenNowPlayingCommand.Execute(null);
            Pump(30);
            MobileFixtures.Land(window);
        }

        Button ArtistLine() => MobileFixtures.Named<Button>(view.FindControl<NowPlayingPage>("NowPlaying")!, "ArtistButton");

        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        OpenPlayer();
        var artist = ArtistLine();
        Tap(window, artist);
        Pump(30);

        Assert.True(menu.IsVisible);
        Assert.Same(artist, menu.Anchor);
        Assert.IsType<TrackLinksMenuViewModel>(rig.Shell.PopMenu);
        Assert.True(rig.Shell.IsNowPlayingOpen);
        Assert.Equal(1, ScaleOf(card), 3);
        foreach (var name in new[] { "LinkGoToAlbum", "LinkGoToArtist" })
            Assert.True(MobileFixtures.Named<Control>(menu, name).IsEffectivelyVisible, name);
        Assert.Equal(2, MobileFixtures.Named<StackPanel>(menu, "LinkRows").Children.Count);
        Assert.DoesNotContain(menu.GetVisualDescendants().OfType<Control>(), c => c.Name is "MenuFavourite" or "Tiles" or "MenuAddToQueue");
        var texts = menu.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Equal(new[] { "Go to Album", "An Evening with Silk Sonic", "Go to Artist", "Bruno Mars, Anderson .Paak & Silk Sonic" }, texts);
        // The ⋯ menu's glyphs (the desktop's).
        var glyphs = menu.GetVisualDescendants().OfType<PathIcon>().Select(i => i.Data).ToList();
        Assert.Equal(new[] { card.FindResource("PhAlbum"), card.FindResource("PhArtist") }, glyphs);
        Assert.All(glyphs, Assert.NotNull);

        // Apple's place: just above the artist, its lower-left corner by the text, over the title.
        var cardBox = BoxIn(card, view);
        var artistBox = BoxIn(artist, view);
        output.WriteLine($"artist {artistBox}, card {cardBox}");
        Assert.Equal(artistBox.Top, cardBox.Bottom, 1);
        Assert.Equal(Math.Max(PopMenu.EdgeGap, artistBox.Left - PopMenu.LinksLead), cardBox.Left, 1);
        Assert.True(cardBox.Top > 0);
        // It grows out of the text: the origin is the artist's centre, at the card's foot.
        var origin = card.RenderTransformOrigin.ToPixels(card.Bounds.Size);
        Assert.Equal(artistBox.Center.X - cardBox.Left, origin.X, 1);
        Assert.Equal(cardBox.Height, origin.Y, 1);

        // Back, then a tap outside: only the bubble closes.
        Assert.True(rig.Shell.TryHandleBack());
        Assert.False(rig.Shell.IsMenuOpen);
        Assert.True(rig.Shell.IsNowPlayingOpen);
        Pump(15);
        Tap(window, artist);
        Pump(25);
        var wasPlaying = rig.Shell.Player.IsPlaying;
        Tap(window, MobileFixtures.Named<Button>(view, "PlayPauseButton"));
        Assert.False(rig.Shell.IsMenuOpen);
        Assert.Equal(wasPlaying, rig.Shell.Player.IsPlaying);
        Assert.True(rig.Shell.IsNowPlayingOpen);
        Pump(15);

        // Go to Album: the player closes and the album opens.
        Tap(window, artist);
        Pump(25);
        Tap(window, MobileFixtures.Named<Button>(menu, "LinkGoToAlbum"));
        Assert.False(rig.Shell.IsMenuOpen);
        Assert.False(rig.Shell.IsNowPlayingOpen);
        Assert.Equal(album.Id, Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage).AlbumId);
        Pump(15);

        // Go to Artist: the artist the ⋯ menu's Go to Artist opens (the one the song is filed under).
        OpenPlayer();
        Tap(window, ArtistLine());
        Pump(25);
        Tap(window, MobileFixtures.Named<Button>(menu, "LinkGoToArtist"));
        Assert.False(rig.Shell.IsMenuOpen);
        Assert.False(rig.Shell.IsNowPlayingOpen);
        Assert.Equal(song.GroupingArtist, Assert.IsType<ArtistPageViewModel>(rig.Shell.CurrentPage).Title);
        Pump(15);
        rig.Shell.OpenTrackSheetCommand.Execute(song);
        rig.Shell.Sheet!.GoToArtistCommand.Execute(null);
        Assert.Equal(song.GroupingArtist, rig.Shell.CurrentPage!.Title);
        window.Close();
    }

    /// <summary>
    /// Now Playing's quality badge (owner 10-14): a tap pops the quality card out of the badge,
    /// centred on it and a gap above it, with the menu's own pop - the big glyph, the class, the
    /// technical line, the plain line and OK. OK, Back and a tap outside close only the card.
    /// </summary>
    [AvaloniaFact]
    public void TheQualityBadge_PopsTheQualityCardOutOfItself()
    {
        var song = MobileFixtures.Song("Leave the Door Open", artist: "Silk Sonic");
        song.Codec = "MPEG-4 Audio (alac)"; song.BitsPerSample = 24; song.SampleRate = 48000;
        using var rig = MobileFixtures.MakeRig(new[] { song });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);
        var menu = MenuOf(view);
        var card = menu.FindControl<Panel>("Card")!;
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        var badge = MobileFixtures.Named<Button>(page, "QualityBadge");

        // The badge sits between the times, on their row, centred on the page.
        var badgeBox = BoxIn(badge, view);
        var elapsed = BoxIn(MobileFixtures.Named<TextBlock>(page, "ElapsedText"), view);
        var remaining = BoxIn(MobileFixtures.Named<TextBlock>(page, "RemainingText"), view);
        Assert.True(elapsed.Right < badgeBox.Left && badgeBox.Right < remaining.Left, $"{elapsed} {badgeBox} {remaining}");
        Assert.InRange(badgeBox.Center.X, view.Bounds.Width / 2 - 1, view.Bounds.Width / 2 + 1);
        Assert.InRange(badgeBox.Center.Y, elapsed.Center.Y - 2, elapsed.Center.Y + 2);
        Assert.Equal("Lossless", MobileFixtures.Named<TextBlock>(page, "QualityBadgeText").Text);
        AssertWaveform(page);

        // A tap's order (Click, then the command): the card starts small at the badge's centre.
        badge.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        badge.Command!.Execute(null);
        Assert.Same(badge, menu.Anchor);
        Assert.IsType<QualityCardViewModel>(rig.Shell.PopMenu);
        Assert.Equal(PopMenu.PopScale, ScaleOf(card), 3);
        var origin = card.RenderTransformOrigin.ToPixels(card.Bounds.Size);
        // Layout bounds, not a translated box: the card is drawn scaled down round the origin just now.
        var badgeCentre = badge.TranslatePoint(new Point(badge.Bounds.Width / 2, badge.Bounds.Height / 2), menu)!.Value - card.Bounds.Position;
        Assert.Equal(badgeCentre.X, origin.X, 1);
        Assert.Equal(card.Bounds.Height, origin.Y, 1);                   // the badge is below the card's foot
        Pump(30);
        Assert.Equal(1, ScaleOf(card), 3);

        // Centred on the badge, a gap above it, over the cover; the same card width every time.
        var cardBox = BoxIn(card, view);
        output.WriteLine($"badge {badgeBox}, card {cardBox}");
        Assert.InRange(cardBox.Center.X, badgeBox.Center.X - 0.5, badgeBox.Center.X + 0.5);   // on the pixel grid
        Assert.Equal(badgeBox.Top - PopMenu.CardGap, cardBox.Bottom, 1);
        Assert.Equal(PopMenu.CardWidth, cardBox.Width, 1);
        Assert.True(cardBox.Top > 0);
        // Compact (owner 10-18): a small note, not a dialog (it was 300 wide, a 27-high glyph, a
        // 20 pt class and a 52-high OK row) - yet OK keeps a full 44 dp tap target. Heights are
        // checked per part: the headless font wraps the lines wider than Inter does.
        Assert.InRange(cardBox.Width, 200, 260);
        Assert.InRange(MobileFixtures.Named<Control>(menu, "QualityGlyph").Bounds.Height, 1, 20);
        Assert.True(MobileFixtures.Named<TextBlock>(menu, "QualityLabel").FontSize <= 17);
        Assert.Equal(44, MobileFixtures.Named<Button>(menu, "QualityDone").Bounds.Height, 1);

        var texts = menu.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Equal(new[] { "Lossless", "24-bit/48 kHz ALAC", Track.LosslessDescription, "OK" }, texts);
        AssertWaveform(menu);
        Assert.DoesNotContain(menu.GetVisualDescendants().OfType<Control>(), c => c.Name is "Tiles" or "MenuAddToQueue" or "OptionItems");

        // OK: only the card closes, with the menu's close.
        Tap(window, MobileFixtures.Named<Button>(menu, "QualityDone"));
        Assert.False(rig.Shell.IsMenuOpen);
        Assert.True(rig.Shell.IsNowPlayingOpen);
        Assert.False(menu.IsHitTestVisible);
        Assert.Equal(TimeSpan.FromMilliseconds(PopMenu.PopCloseMs),
            Assert.IsType<TransformOperationsTransition>(Assert.Single(card.Transitions!)).Duration);
        Pump(15);
        Assert.False(menu.IsVisible);

        // Back, then a tap outside: the same, and the tap goes no further.
        Tap(window, badge);
        Pump(25);
        Assert.True(rig.Shell.TryHandleBack());
        Assert.False(rig.Shell.IsMenuOpen);
        Assert.True(rig.Shell.IsNowPlayingOpen);
        Pump(15);
        Tap(window, badge);
        Pump(25);
        var wasPlaying = rig.Shell.Player.IsPlaying;
        Tap(window, MobileFixtures.Named<Button>(view, "PlayPauseButton"));
        Assert.False(rig.Shell.IsMenuOpen);
        Assert.Equal(wasPlaying, rig.Shell.Player.IsPlaying);
        Assert.True(rig.Shell.IsNowPlayingOpen);
        Pump(15);

        // A compressed file: the same waveform (the desktop's, beside every label) and the codec,
        // on the badge and the card.
        var mp3 = MobileFixtures.Song("Tone");
        mp3.Codec = "MPEG Version 1 Audio, Layer 3"; mp3.SampleRate = 44100; mp3.Bitrate = 320;
        rig.Shell.Player.PlayTracks(new[] { mp3 }, 0);
        window.UpdateLayout();
        Assert.Equal("MP3", MobileFixtures.Named<TextBlock>(page, "QualityBadgeText").Text);
        AssertWaveform(page);
        Tap(window, badge);
        Pump(25);
        texts = menu.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Equal(new[] { "MP3", "320 kbps 44.1 kHz MP3", Track.LossyDescription, "OK" }, texts);
        AssertWaveform(menu);
        window.Close();
    }

    /// <summary>The compact quality card (owner 10-18) still fits the longest class and line - "Hi-Res
    /// Lossless", "24-bit/192 kHz FLAC" - on one line each, in the light glass and the dark, the
    /// waveform above them. Under NOCTIS_TEST_SKIA=1 (real fonts) the one-line checks run and
    /// NOCTIS_QUALITY_SHOTS=dir saves each to a PNG.</summary>
    [AvaloniaFact]
    public void TheQualityCard_FitsTheLongestClass_InLightAndDark()
    {
        var song = MobileFixtures.Song("Hi-Res", artist: "Band");
        song.Codec = "FLAC"; song.BitsPerSample = 24; song.SampleRate = 192000;
        using var rig = MobileFixtures.MakeRig(new[] { song });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);
        var menu = MenuOf(view);
        var page = view.FindControl<NowPlayingPage>("NowPlaying")!;
        Assert.Equal("Hi-Res Lossless", MobileFixtures.Named<TextBlock>(page, "QualityBadgeText").Text);
        AssertWaveform(page);
        Tap(window, MobileFixtures.Named<Button>(page, "QualityBadge"));
        Pump(30);
        var dir = Environment.GetEnvironmentVariable("NOCTIS_QUALITY_SHOTS");
        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            menu.FindControl<ThemeVariantScope>("Scope")!.RequestedThemeVariant = variant;
            Pump(3);
            var card = BoxIn(menu.FindControl<Panel>("Card")!, view);
            Assert.Equal(PopMenu.CardWidth, card.Width, 1);
            foreach (var (name, text) in new[] { ("QualityLabel", "Hi-Res Lossless"), ("QualityDetail", "24-bit/192 kHz FLAC") })
            {
                var block = MobileFixtures.Named<TextBlock>(menu, name);
                Assert.Equal(text, block.Text);
                // The stub text layout's glyphs are wider than Inter's: one line is checked on real fonts.
                if (HeadlessTestApp.RealRendering)
                    Assert.True(block.Bounds.Height < block.FontSize * 1.9, $"{name} wrapped: {block.Bounds}");
            }
            var meaning = MobileFixtures.Named<TextBlock>(menu, "QualityMeaning");
            Assert.Equal(TrackQuality.HiResMeaning, meaning.Text);
            Assert.True(BoxIn(meaning, view).Right <= card.Right && BoxIn(meaning, view).Left >= card.Left);
            AssertWaveform(menu);
            output.WriteLine($"{variant}: card {card}");
            if (HeadlessTestApp.RealRendering && !string.IsNullOrEmpty(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
                window.CaptureRenderedFrame()!.Save(System.IO.Path.Combine(dir, $"quality-card-hires-{variant}.png"));
            }
        }
        window.Close();
    }

    /// <summary>The quality glyph is shown and is the desktop's waveform (PhLossless), whatever the class.</summary>
    private static void AssertWaveform(Control root)
    {
        var glyph = MobileFixtures.Named<Viewbox>(root, "QualityGlyph");
        Assert.True(glyph.IsEffectivelyVisible);
        Assert.Same(root.FindResource("PhLossless"), Assert.IsType<Avalonia.Controls.Shapes.Path>(glyph.Child).Data);
        Assert.Empty(root.GetVisualDescendants().OfType<Control>().Where(c => c.Name is "QualityLosslessGlyph" or "QualityLossyGlyph"));
    }

    [AvaloniaFact]
    public async Task TheMenusActions_ActOnTheSong_AndCloseIt()
    {
        var (rig, window, _) = OpenNowPlaying();
        using var __ = rig;
        var track = rig.Shell.Player.CurrentTrack!;

        rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
        await rig.Shell.Sheet!.ToggleFavouriteCommand.ExecuteAsync(null);
        Assert.True(track.IsFavorite);
        Assert.False(rig.Shell.IsMenuOpen);

        // Add to Playlist: the card gives way to the bottom sheet's playlist list.
        rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
        rig.Shell.Sheet!.ShowPlaylistsCommand.Execute(null);
        Assert.False(rig.Shell.IsMenuOpen);
        Assert.True(rig.Shell.IsPlaylistPickerOpen);
        Assert.Same(track, rig.Shell.Sheet.Track);
        rig.Shell.CloseSheet();

        rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
        rig.Shell.Sheet!.ShowLyricsCommand.Execute(null);
        Assert.False(rig.Shell.IsMenuOpen);
        Assert.True(rig.Shell.IsLyricsOpen);
        window.Close();
    }

    /// <summary>Bug (10-13): the old Now Playing menu's Add to Playlist opened the playlist list
    /// for a song only; from an album's (or playlist's, artist's) ⋯ it closed and did nothing.</summary>
    [AvaloniaFact]
    public void AddToPlaylist_FromAnAlbumsMenu_OpensTheListForTheAlbum()
    {
        var a = MobileFixtures.Song("A1");
        var b = MobileFixtures.Song("A2");
        var album = MobileFixtures.MakeAlbum("Record", "Band", a, b);
        using var rig = MobileFixtures.MakeRig(new[] { a, b }, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);

        rig.Shell.OpenAlbumSheetCommand.Execute(album);
        Assert.True(rig.Shell.IsMenuOpen);
        rig.Shell.Sheet!.ShowPlaylistsCommand.Execute(null);
        Assert.True(rig.Shell.IsPlaylistPickerOpen);
        Assert.Same(album, rig.Shell.Sheet!.Album);
        Assert.Equal(2, rig.Shell.Sheet.Tracks.Count);
        window.UpdateLayout();
        Assert.True(view.FindControl<ContextSheet>("Sheet")!.IsVisible);
        window.Close();
    }

    /// <summary>Lyrics are the playing song's: the row shows on that song's menu only, and opens
    /// over the Queue's menu by taking the Queue down (it sits over the lyrics page).</summary>
    [AvaloniaFact]
    public void Lyrics_ShowsOnlyForThePlayingSong()
    {
        var songs = new[] { MobileFixtures.Song("One"), MobileFixtures.Song("Two") };
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out _);
        rig.Shell.Player.PlayTracks(songs, 0);

        rig.Shell.OpenTrackSheetCommand.Execute(songs[1]);
        Assert.False(rig.Shell.Sheet!.CanShowLyrics);
        rig.Shell.OpenTrackSheetCommand.Execute(songs[0]);
        Assert.True(rig.Shell.Sheet!.CanShowLyrics);

        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.ToggleQueueCommand.Execute(null);
        rig.Shell.OpenCurrentTrackSheetCommand.Execute(null);
        rig.Shell.Sheet!.ShowLyricsCommand.Execute(null);
        Assert.False(rig.Shell.IsQueueOpen);
        Assert.True(rig.Shell.IsLyricsOpen);
        // The lyrics page's own ⋯: no Lyrics row, it is already up.
        rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
        Assert.False(rig.Shell.Sheet!.CanShowLyrics);
        window.Close();
    }

    /// <summary>Lyrics (and the Queue over it) can be up without Now Playing: opened from a song
    /// row's menu. Go to Album / Go to Artist from their ⋯ must take them down, or the page
    /// opened underneath and the tap looked dead.</summary>
    [AvaloniaFact]
    public void GoToAlbum_FromLyricsOpenedOverAList_ClosesLyricsAndTheQueue()
    {
        var songs = new[] { MobileFixtures.Song("One", artist: "Band"), MobileFixtures.Song("Two", artist: "Band") };
        var album = MobileFixtures.MakeAlbum("Record", "Band", songs);
        using var rig = MobileFixtures.MakeRig(songs, new[] { album });
        rig.Shell.Player.PlayTracks(songs, 0);

        rig.Shell.OpenTrackSheetCommand.Execute(songs[0]);
        rig.Shell.Sheet!.ShowLyricsCommand.Execute(null);
        Assert.False(rig.Shell.IsNowPlayingOpen);
        rig.Shell.ToggleQueueCommand.Execute(null);
        rig.Shell.OpenCurrentTrackSheetCommand.Execute(null);
        rig.Shell.Sheet!.GoToAlbumCommand.Execute(null);

        Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        Assert.False(rig.Shell.IsLyricsOpen);
        Assert.False(rig.Shell.IsQueueOpen);
    }

    /// <summary>On an album's own page, a track's menu offered Go to Album (it pushed the same page
    /// again, and Back then showed it twice); from Now Playing over that page, Go to Album only
    /// needs the player out of the way. Go to Artist on that artist's page likewise.</summary>
    [AvaloniaFact]
    public void GoToAlbum_OnThatAlbumsPage_IsNotOffered_AndNeverPushesItTwice()
    {
        var songs = new[] { MobileFixtures.Song("One", artist: "Band"), MobileFixtures.Song("Two", artist: "Band") };
        var album = MobileFixtures.MakeAlbum("Record", "Band", songs);
        using var rig = MobileFixtures.MakeRig(songs, new[] { album });
        rig.Shell.OpenAlbumCommand.Execute(album);
        var page = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);

        rig.Shell.OpenTrackSheetCommand.Execute(songs[1]);
        Assert.False(rig.Shell.Sheet!.CanGoToAlbum);
        Assert.True(rig.Shell.Sheet.CanGoToArtist);

        rig.Shell.Player.PlayTracks(songs, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
        Assert.True(rig.Shell.Sheet!.CanGoToAlbum);                              // the player covers the page
        rig.Shell.Sheet.GoToAlbumCommand.Execute(null);
        Assert.False(rig.Shell.IsNowPlayingOpen);
        Assert.Same(page, rig.Shell.CurrentPage);
        Assert.Single(rig.Shell.Pages);

        rig.Shell.OpenArtistCommand.Execute("Band");
        rig.Shell.OpenArtistCommand.Execute("Band");
        Assert.Equal(2, rig.Shell.Pages.Count);
    }

    /// <summary>Every ⋯ in the app opens the same glass menu out of itself: a song row (Songs), an
    /// album's track row, the album page's own ⋯, the Library header's and the Queue's.</summary>
    [AvaloniaFact]
    public void EveryMoreButton_OpensTheGlassMenu_OutOfItself()
    {
        var tracks = Enumerable.Range(1, 4).Select(i => MobileFixtures.Song($"T{i}", artist: "Band")).ToArray();
        var album = MobileFixtures.MakeAlbum("Record", "Band", tracks);
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var menu = MenuOf(view);
        var sheet = view.FindControl<ContextSheet>("Sheet")!;

        void Check(Button more, string what, string firstRow)
        {
            Tap(window, more);
            Pump(25);
            Assert.True(menu.IsVisible, what);
            Assert.Same(more, menu.Anchor);
            Assert.False(sheet.IsVisible, what);
            Assert.True(MobileFixtures.Named<Control>(menu, firstRow).IsEffectivelyVisible, what);
            var card = BoxIn(menu.FindControl<Panel>("Card")!, view);
            output.WriteLine($"{what}: button {BoxIn(more, view)}, card {card}");
            Assert.True(card.Intersects(BoxIn(more, view)), what + ": the card covers its button");
            Assert.True(card.Left >= 0 && card.Right <= view.Bounds.Width && card.Top >= 0 && card.Bottom <= view.Bounds.Height, what);
            rig.Shell.CloseSheet();
            Pump(15);
        }

        rig.Shell.OpenSongsCommand.Execute(null);
        MobileFixtures.Land(window);   // the page takes touches once it has slid in
        var songRow = view.GetVisualDescendants().OfType<SongRow>().First();
        Check(MobileFixtures.Named<Button>(songRow, "MoreButton"), "Songs row", "MenuAddToQueue");

        rig.Shell.OpenAlbumCommand.Execute(album);
        Pump(30);                                                        // the page settles (push, hero)
        MobileFixtures.Land(window);
        var albumPage = MobileFixtures.Find<AlbumPage>(view);
        // The rows' ⋯ open the page's song menu (with Select for its Select mode).
        var albumVm = (AlbumPageViewModel)rig.Shell.CurrentPage!;
        var rowMore = albumPage.GetVisualDescendants().OfType<Button>().First(b => b.Command == albumVm.OpenSongMenuCommand);
        Check(rowMore, "album track row", "MenuGoToArtist");   // no Go to Album on its own page
        Check(MobileFixtures.Named<Button>(albumPage, "MoreButton"), "album header", "MenuAddToQueue");

        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        window.UpdateLayout();
        Check(MobileFixtures.Named<Button>(view, "LibraryMoreButton"), "Library header", "MenuAddFolder");

        rig.Shell.Player.PlayTracks(tracks, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.ToggleQueueCommand.Execute(null);
        Pump(40);
        MobileFixtures.Land(window);   // the player and the Queue have risen
        Check(MobileFixtures.Named<Button>(view, "QueueMoreButton"), "Queue", "MenuAddToQueue");
        window.Close();
    }

    /// <summary>A ⋯ low on a list opens up, above the mini player and the tab bar, never under
    /// them; one high on the screen opens down from it.</summary>
    [AvaloniaFact]
    public void AMoreButtonNearTheBottom_OpensAbove_ClearOfTheMiniPlayerAndTabBar()
    {
        var tracks = Enumerable.Range(1, 14).Select(i => MobileFixtures.Song($"T{i:00}", artist: "Band")).ToArray();
        var album = MobileFixtures.MakeAlbum("Record", "Band", tracks);
        using var rig = MobileFixtures.MakeRig(tracks, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(tracks, 0);                          // the mini player is up
        rig.Shell.OpenAlbumCommand.Execute(album);
        Pump(30);
        window.UpdateLayout();
        var menu = MenuOf(view);
        var tabBar = BoxIn(MobileFixtures.Named<Control>(view, "TabCapsule"), view);
        var mini = BoxIn(MobileFixtures.Named<Control>(view, "MiniBar"), view);
        var chromeTop = Math.Min(tabBar.Top, mini.Top);

        // The lowest ⋯ still above the mini player.
        var page = MobileFixtures.Find<AlbumPage>(view);
        var low = page.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Command == ((AlbumPageViewModel)rig.Shell.CurrentPage!).OpenSongMenuCommand && b.IsEffectivelyVisible)
            .Select(b => (button: b, box: BoxIn(b, view)))
            .Where(x => x.box.Bottom < chromeTop - 4)
            .OrderBy(x => x.box.Top).Last();
        Tap(window, low.button);
        Pump(25);
        var card = BoxIn(menu.FindControl<Panel>("Card")!, view);
        output.WriteLine($"button {low.box}, card {card}, chrome top {chromeTop}");
        Assert.True(card.Bottom <= chromeTop + 0.5, "the card runs under the mini player / tab bar");
        Assert.Equal(low.box.Bottom, card.Bottom, 1);                    // flipped: up from the button
        Assert.True(card.Top >= 0);
        rig.Shell.CloseSheet();
        Pump(15);

        // The Library header's ⋯, at the top: down from it.
        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        window.UpdateLayout();
        var more = MobileFixtures.Named<Button>(view, "LibraryMoreButton");
        Tap(window, more);
        Pump(25);
        card = BoxIn(menu.FindControl<Panel>("Card")!, view);
        var moreBox = BoxIn(more, view);
        Assert.Equal(moreBox.Top, card.Top, 1);
        Assert.Equal(moreBox.Right, card.Right, 1);
        window.Close();
    }

    /// <summary>The Library ⋯ rows run the desktop's three library actions, closing the menu first.</summary>
    [AvaloniaFact]
    public void LibraryMenu_SettingsOpensSettings_AndClosesTheMenu()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("A") });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        rig.Shell.OpenLibraryMenuCommand.Execute(null);
        Assert.True(rig.Shell.IsMenuOpen);
        Assert.True(rig.Shell.TryHandleBack());
        Assert.False(rig.Shell.IsMenuOpen);

        rig.Shell.OpenLibraryMenuCommand.Execute(null);
        rig.Shell.LibraryMenu!.OpenSettingsCommand.Execute(null);
        Assert.False(rig.Shell.IsMenuOpen);
        Assert.IsType<SettingsPageViewModel>(rig.Shell.CurrentPage);
        window.Close();
    }

    /// <summary>Apple's menu glass has no outline of its own (owner 10-14): no stroke round the card,
    /// only the catch-light along its top; a light blur so the cover shows through; and dark content
    /// lifted under a white veil rather than scaled into a vivid blotch.</summary>
    [AvaloniaFact]
    public void TheGlass_HasNoOutline_OnlyACatchLight_AndAClearView()
    {
        var glass = new PopMenu().FindControl<GlassPanel>("Glass")!;
        Assert.True(glass.EdgeThickness <= 0 || glass.EdgeBrush is null, "no stroke round the card");
        Assert.Equal(GlassSpecularStyle.TopCatch, glass.SpecularStyle);
        Assert.True(glass.Specular > 0);
        Assert.True(glass.BlurRadius <= 10, $"blur {glass.BlurRadius}: the cover's shapes stay readable");
        Assert.Equal(1, glass.BackdropLift);
    }

    /// <summary>The menu wears the theme of what it opened from: Now Playing's always-dark page
    /// gives the dark glass (white text) whatever the app theme; a page gives the app's.</summary>
    [AvaloniaFact]
    public void TheCard_TakesTheThemeOfWhatItOpenedFrom()
    {
        var (rig, window, view) = OpenNowPlaying();
        using var _ = rig;
        var menu = MenuOf(view);
        Tap(window, MobileFixtures.Named<Button>(view, "NowPlayingMore"));
        Pump(25);
        var card = menu.FindControl<Panel>("Card")!;
        Assert.Equal(ThemeVariant.Dark, card.ActualThemeVariant);
        var label = menu.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Add to Queue");
        Assert.Equal(Colors.White, ((ISolidColorBrush)label.Foreground!).Color);

        rig.Shell.CloseNowPlayingCommand.Execute(null);
        Pump(15);
        rig.Shell.SelectTabCommand.Execute(MobileTab.Library);
        window.UpdateLayout();
        var more = MobileFixtures.Named<Button>(view, "LibraryMoreButton");
        Tap(window, more);
        Pump(25);
        Assert.Equal(more.ActualThemeVariant, card.ActualThemeVariant);
        rig.Shell.CloseSheet();
        Pump(15);

        // An album page is painted in a deep shade of its cover's colour, from a light cover as
        // from a dark one (PageTint.HeroFade): the dark glass, whatever the app theme.
        var album = MobileFixtures.MakeAlbum("Record", "Band", rig.Library.TrackList.ToArray());
        rig.Shell.OpenAlbumCommand.Execute(album);
        Pump(30);
        var albumVm = (ITintedPage)rig.Shell.CurrentPage!;
        foreach (var tint in new[] { Color.Parse("#F2EEE6"), Color.Parse("#3A1A08") })
        {
            albumVm.Tint.Apply(tint);
            Assert.False(albumVm.Tint.IsLight);
            window.UpdateLayout();
            var header = MobileFixtures.Named<Button>(MobileFixtures.Find<AlbumPage>(view), "MoreButton");
            Tap(window, header);
            Pump(25);
            Assert.Equal(ThemeVariant.Dark, card.ActualThemeVariant);
            rig.Shell.CloseSheet();
            Pump(15);
        }
        window.Close();
    }

    /// <summary>
    /// "Liquid glass, not dark" (owner 10-13), and still readable: the glass is a light frost over
    /// the blurred backdrop, whose brightness the panel remaps into a range. Worst cases: dark
    /// glass over the brightest cover (the backdrop at its max, the frost and the top sheen on it)
    /// must keep white labels at 4.5:1 and the second lines at 3:1; light glass over black at its
    /// min must keep black labels at 4.5:1. And over pure black the dark glass is visibly lighter
    /// than the backdrop — not the old smoked card (#73161618 tint, backdrop capped at 0.5).
    /// </summary>
    [AvaloniaFact]
    public void TheGlass_IsALightFrost_AndItsTextStaysReadableOverAnyBackdrop()
    {
        var glass = new MenuGlass();
        ResourceDictionary Theme(ThemeVariant v) => (ResourceDictionary)glass.Resources.ThemeDictionaries[v];
        static double Over(double under, Color c) => under * (1 - c.A / 255.0) + c.R / 255.0 * (c.A / 255.0);
        static Color Gray(double g) { var b = (byte)Math.Round(Math.Clamp(g, 0, 1) * 255); return Color.FromRgb(b, b, b); }
        static Color Composite(Color text, Color under)
        {
            var a = text.A / 255.0;
            return Color.FromRgb((byte)Math.Round(text.R * a + under.R * (1 - a)), (byte)Math.Round(text.G * a + under.G * (1 - a)),
                (byte)Math.Round(text.B * a + under.B * (1 - a)));
        }
        Color Brush(ResourceDictionary d, string key) => ((SolidColorBrush)d[key]!).Color;
        Color SheenTop(ResourceDictionary d) => ((LinearGradientBrush)d["MenuGlassSheen"]!).GradientStops[0].Color;

        var dark = Theme(ThemeVariant.Dark);
        var tint = Brush(dark, "MenuGlassTint");
        Assert.True(tint.R == 255 && tint.G == 255 && tint.B == 255, "the frost is white, not a dark smoke");
        var brightest = Over(Over((double)dark["MenuGlassMaxLuminance"]!, tint), SheenTop(dark));
        var overBlack = Over((double)dark["MenuGlassMinLuminance"]!, tint);
        output.WriteLine($"dark glass: {overBlack:0.000} over black .. {brightest:0.000} over the brightest cover");
        Assert.True(overBlack >= 0.25, $"over black the glass must read lighter than the backdrop ({overBlack:0.000})");
        Assert.True(PageTint.ContrastRatio(Brush(dark, "MenuGlassText"), Gray(brightest)) >= 4.5);
        Assert.True(PageTint.ContrastRatio(Composite(Brush(dark, "MenuGlassSecondaryText"), Gray(brightest)), Gray(brightest)) >= 3);

        var light = Theme(ThemeVariant.Light);
        var darkest = Over((double)light["MenuGlassMinLuminance"]!, Brush(light, "MenuGlassTint"));
        output.WriteLine($"light glass: {darkest:0.000} over black");
        Assert.True(PageTint.ContrastRatio(Brush(light, "MenuGlassText"), Gray(darkest)) >= 4.5);
        Assert.True(PageTint.ContrastRatio(Composite(Brush(light, "MenuGlassSecondaryText"), Gray(darkest)), Gray(darkest)) >= 4.5);
    }
}
