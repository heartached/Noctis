using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 09-14 probes: (1) the player-bar title is clipped mid-glyph at the viewport's right
/// edge while the marquee rests, (2) the tile/row explicit badge's size and its vertical
/// placement against the title's capitals.
/// </summary>
public class TitleBadgeAndMarqueeProbeTests
{
    private readonly ITestOutputHelper _o;
    public TitleBadgeAndMarqueeProbeTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int n = 4)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private static PlayerViewModel MakePlayer() => new(
        new FakeAudioPlayer(), new FakeLibraryService(),
        new TestPersistenceService(), new FakeAnimatedCoverService());

    [AvaloniaFact]
    public void Bar_LongTitle_ClipsAGlyphAtTheViewportEdge()
    {
        var player = MakePlayer();
        player.CurrentTrack = new Track { Id = Guid.NewGuid(), Title = "Bad Blood (Taylor's Version) [feat. Kendrick Lamar]", Artist = "Taylor Swift", Album = "1989 (Taylor's Version) [Deluxe]" };
        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 900, Height = 200, Content = bar };
        win.Show();
        Pump(6);

        var viewport = bar.FindControl<Border>("TrackTitleViewport")!;
        var tb = bar.FindControl<TextBlock>("TrackTitleTextBlock")!;
        var edgeInText = viewport.TranslatePoint(new Point(viewport.Bounds.Width, 0), tb)!.Value.X;
        var layout = tb.TextLayout;
        var textWidth = layout.WidthIncludingTrailingWhitespace;
        var boxes = Enumerable.Range(0, tb.Text!.Length).Select(i => (i, box: layout.HitTestTextPosition(i))).ToList();
        var (index, glyph) = boxes.FirstOrDefault(b => b.box.Left < edgeInText && b.box.Right > edgeInText);
        _o.WriteLine($"viewport {viewport.Bounds.Width:0.##} clip={viewport.ClipToBounds} mask={(viewport.OpacityMask == null ? "none" : viewport.OpacityMask.GetType().Name)} classes={string.Join(",", viewport.Classes)} | tb.Width={tb.Width} trimming={tb.TextTrimming} textWidth={textWidth:0.##} | edge@{edgeInText:0.##} glyph#{index} '{tb.Text[index]}' box {glyph.Left:0.##}-{glyph.Right:0.##}");

        Assert.True(textWidth > viewport.Bounds.Width, "title must overflow for this probe");
        Assert.True(double.IsNaN(tb.Width), "marquee mode leaves the TextBlock unconstrained (no ellipsis)");
        // A glyph straddles the clip edge: part of it is painted, the rest cut — the sliver.
        Assert.True(glyph.Left < edgeInText && glyph.Right > edgeInText, "expected a glyph straddling the clip edge");
        // Fix pin: the viewport fades that edge out instead of cutting it.
        Assert.True(viewport.Classes.Contains("overflow"), "viewport carries .overflow while the title overflows");
        Assert.IsType<LinearGradientBrush>(viewport.OpacityMask);
        win.Close();
    }

    [AvaloniaFact]
    public void Bar_ShortTitle_HasNoEdgeFade()
    {
        var player = MakePlayer();
        player.CurrentTrack = new Track { Id = Guid.NewGuid(), Title = "Volví", Artist = "Aventura" };
        var bar = new PlaybackBarView { DataContext = player, CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 900, Height = 200, Content = bar };
        win.Show();
        Pump(6);
        var viewport = bar.FindControl<Border>("TrackTitleViewport")!;
        Assert.DoesNotContain("overflow", viewport.Classes);
        Assert.Null(viewport.OpacityMask);
        win.Close();
    }

    [AvaloniaFact]
    public void TileBadge_SizeAndVerticalCentre()
    {
        EnsureAppStyles();
        var tb = new HighlightTextBlock { DisplayText = "Volví", IsExplicit = true, FontSize = 13, FontWeight = FontWeight.SemiBold };
        var win = new Window { Width = 400, Height = 100, Content = tb };
        win.Show();
        Pump(4);

        var badge = tb.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("explicit-badge"));
        var e = badge.GetVisualDescendants().OfType<TextBlock>().First();
        var line = tb.TextLayout.TextLines[0];
        var top = badge.TranslatePoint(new Point(0, 0), tb)!.Value;
        var ty = badge.RenderTransform is TranslateTransform t ? t.Y : (badge.RenderTransform?.Value.M32 ?? 0);
        var centre = top.Y + badge.Bounds.Height / 2 + ty;
        _o.WriteLine($"line h={line.Height:0.##} baseline={line.Baseline:0.##} | badge {badge.Bounds.Width:0.##}x{badge.Bounds.Height:0.##} at y={top.Y:0.##} transformY={ty} centre={centre:0.##} pad={badge.Padding} | E font={e.FontSize} | centre-rule (lineH-h)/2={(line.Height - badge.Bounds.Height) / 2:0.##}");

        // Avalonia's BaselineAlignment.Center rule: the run is centred on the line box.
        Assert.InRange(top.Y, (line.Height - badge.Bounds.Height) / 2 - 0.6, (line.Height - badge.Bounds.Height) / 2 + 0.6);
        // Target after the change: 12px box, 8px glyph, no nudge (Inter's cap centre IS the line-box centre).
        Assert.Equal(12, badge.Bounds.Height, 1);
        Assert.Equal(8, e.FontSize);
        Assert.Equal(0, ty, 1);
        win.Close();
    }

    [Fact]
    public void Inter_CapCentre_IsTheLineBoxCentre()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Noctis.UI/Assets/Fonts/Inter-SemiBold.ttf"));
        Assert.True(File.Exists(path), path);
        using var tf = SKTypeface.FromFile(path);
        using var font = new SKFont(tf, 13);
        var m = font.Metrics;
        var lineH = -m.Ascent + m.Descent + m.Leading;
        var capCentreFromTop = -m.Ascent - m.CapHeight / 2;
        _o.WriteLine($"Inter 13px: ascent={-m.Ascent:0.##} descent={m.Descent:0.##} leading={m.Leading:0.##} cap={m.CapHeight:0.##} | line h={lineH:0.##} centre={lineH / 2:0.##} capCentre={capCentreFromTop:0.##}");
        Assert.InRange(capCentreFromTop - lineH / 2, -0.5, 0.5);
    }

    private static Track T(string title, string artist, Guid albumId, string album) => new()
    { Id = Guid.NewGuid(), Title = title, Artist = artist, AlbumArtist = artist, Album = album, AlbumId = albumId, Duration = TimeSpan.FromSeconds(176), FilePath = "C:/m/" + title + ".mp3" };

    /// <summary>Playlists grid tile (09-14): hovering the cover reveals the album tile's dim + Play + dots; Play plays the playlist and does not open it.</summary>
    [AvaloniaFact]
    public async Task PlaylistTile_HoverRevealsOverlay_AndPlayPlaysThePlaylist()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albumId = Guid.NewGuid();
        var a = T("Cruel Summer", "Taylor Swift", albumId, "Lover");
        var b = T("Bad Blood (Taylor's Version)", "Taylor Swift", albumId, "1989 (Taylor's Version)");
        lib.TrackList.AddRange(new[] { a, b });
        var persistence = new TestPersistenceService();
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(audio, lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "sddss", TrackIds = new() { a.Id, b.Id } };
        sidebar.Playlists.Add(playlist);
        sidebar.PlaylistItems.Add(new PlaylistNavItem { Label = "sddss", PlaylistId = playlist.Id, TrackCount = 2, MetaText = "2 tracks" });
        var vm = new LibraryPlaylistsViewModel(sidebar, player, lib, persistence);
        vm.Refresh();
        var opened = 0; vm.PlaylistOpened += (_, _) => opened++;
        var view = new LibraryPlaylistsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump();

        var tile = view.GetVisualDescendants().OfType<Button>().First(x => x.Classes.Contains("album-tile"));
        var dim = tile.GetVisualDescendants().OfType<Border>().First(x => x.Classes.Contains("tile-dim"));
        var play = tile.GetVisualDescendants().OfType<Button>().First(x => x.Classes.Contains("tile-play"));
        var more = tile.GetVisualDescendants().OfType<Button>().First(x => x.Classes.Contains("tile-more"));
        _o.WriteLine($"rest: dim={dim.Opacity} play={play.Opacity} more={more.Opacity} tile {tile.Bounds.Width:0.#}x{tile.Bounds.Height:0.#}");
        Assert.Equal(0, play.Opacity); Assert.Equal(0, more.Opacity); Assert.Equal(0, dim.Opacity);

        var pc = play.TranslatePoint(new Point(16, 16), win)!.Value;
        win.MouseMove(pc); Pump(2); await Task.Delay(300); Pump(4);
        _o.WriteLine($"hover: tile.IsPointerOver={tile.IsPointerOver} dim={dim.Opacity} play={play.Opacity} more={more.Opacity} playAt=({pc.X:0.#},{pc.Y:0.#})");
        Assert.True(tile.IsPointerOver);
        Assert.Equal(1, play.Opacity); Assert.Equal(1, more.Opacity); Assert.Equal(0.3, dim.Opacity, 2);

        var before = audio.PlayedPaths.Count;
        win.MouseDown(pc, MouseButton.Left); Pump(1);
        win.MouseUp(pc, MouseButton.Left); Pump(4);
        _o.WriteLine($"click play: played={audio.PlayedPaths.Count - before} current={player.CurrentTrack?.Title} opened={opened}");
        Assert.Equal(1, audio.PlayedPaths.Count - before);
        Assert.Equal(a.Title, player.CurrentTrack?.Title);
        Assert.Equal(0, opened);
        // The dots button opens the shared v2 playlist menu (10-09), attached to this tile on open.
        more.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(2);
        Assert.NotNull(tile.ContextMenu);
        Assert.True(tile.ContextMenu!.IsOpen);
        Assert.Contains("v2", tile.ContextMenu.Classes);
        tile.ContextMenu.Close();
        win.Close();
    }

    /// <summary>Songs list row (09-14): the artwork thumb carries a hover Play; clicking it plays from that row without changing the row layout.</summary>
    [AvaloniaFact]
    public async Task SongsRow_HoverPlayOverThumb_PlaysFromThatRow()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albumId = Guid.NewGuid();
        var a = T("Zoom", "Juice WRLD", albumId, "Juice WRLD (Unreleased)");
        var b = T("Zero Toleration", "Juice WRLD", albumId, "Juice WRLD (Unreleased)");
        lib.TrackList.AddRange(new[] { a, b });
        var persistence = new TestPersistenceService();
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(audio, lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        vm.Refresh();
        var view = new LibrarySongsView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump(6);

        var overlays = view.GetVisualDescendants().OfType<Button>().Where(x => x.Classes.Contains("art-play-overlay")).ToList();
        _o.WriteLine($"rows with overlay: {overlays.Count}");
        Assert.Equal(2, overlays.Count);
        var second = overlays.First(o => ReferenceEquals(o.DataContext, b));
        var thumb = second.FindAncestorOfType<Border>()!;
        _o.WriteLine($"rest: overlay {second.Bounds.Width:0.#}x{second.Bounds.Height:0.#} in thumb {thumb.Bounds.Width:0.#}x{thumb.Bounds.Height:0.#} opacity={second.Opacity}");
        Assert.Equal(0, second.Opacity);
        Assert.Equal(36, second.Bounds.Width, 1); Assert.Equal(36, second.Bounds.Height, 1);

        var pc = second.TranslatePoint(new Point(18, 18), win)!.Value;
        win.MouseMove(pc); Pump(2); await Task.Delay(300); Pump(4);
        _o.WriteLine($"hover: opacity={second.Opacity} glyphs={string.Join(",", second.GetVisualDescendants().OfType<Viewbox>().Select(v => v.IsVisible))}");
        Assert.Equal(1, second.Opacity);

        var before = audio.PlayedPaths.Count;
        win.MouseDown(pc, MouseButton.Left); Pump(1);
        win.MouseUp(pc, MouseButton.Left); Pump(4);
        _o.WriteLine($"click: played={audio.PlayedPaths.Count - before} current={player.CurrentTrack?.Title}");
        Assert.Equal(1, audio.PlayedPaths.Count - before);
        Assert.Equal(b.Title, player.CurrentTrack?.Title);
        win.Close();
    }
}
