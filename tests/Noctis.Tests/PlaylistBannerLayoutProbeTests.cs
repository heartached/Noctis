using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 09-14 playlist page banner layout (design "1 Banner · rail right"): a 236px hero band
/// on top carrying name, stats and the Play/Shuffle/star/options pills; the track list
/// below with the 292px Featured Artists / Suggested rail on its right; the star pill
/// toggles the sidebar pin.
/// </summary>
public class PlaylistBannerLayoutProbeTests
{
    private readonly ITestOutputHelper _o;
    public PlaylistBannerLayoutProbeTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int n = 4)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private static Track T(string title, string artist, string album) => new()
    { Id = Guid.NewGuid(), Title = title, Artist = artist, AlbumArtist = artist, Album = album, AlbumId = Guid.NewGuid(), Duration = TimeSpan.FromSeconds(200), FilePath = "C:/m/" + title + ".mp3" };

    [AvaloniaFact]
    public async Task Banner_Hero_List_And_Rail_Geometry_And_StarToggle()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var a = T("Cruel Summer", "Taylor Swift", "Lover");
        var b = T("Bad Blood (Taylor's Version)", "Taylor Swift", "1989 (Taylor's Version)");
        var c = T("Volví", "Aventura", "Volví - Single");
        var extra = T("Lucid Dreams", "Juice WRLD", "Goodbye & Good Riddance"); // library-only: suggestion material
        lib.TrackList.AddRange(new[] { a, b, c, extra });
        var persistence = new TestPersistenceService();
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(audio, lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var playlist = new Playlist { Id = Guid.NewGuid(), Name = "sddss", TrackIds = new() { a.Id, b.Id, c.Id } };
        sidebar.Playlists.Add(playlist);
        var vm = new PlaylistViewModel(playlist, player, lib, persistence, sidebar);
        var view = new PlaylistView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show(); Pump(6);

        // Hero: first row of the outer grid, 236px, full width.
        var nameText = view.GetVisualDescendants().OfType<TextBlock>().First(t => t.FontSize == 40 && t.Text == "sddss");
        var hero = nameText.GetVisualAncestors().OfType<Grid>().First(g => g.Height == 236);
        var hp = hero.TranslatePoint(new Point(0, 0), win)!.Value;
        var np = nameText.TranslatePoint(new Point(0, 0), win)!.Value;
        _o.WriteLine($"hero at ({hp.X:0.#},{hp.Y:0.#}) {hero.Bounds.Width:0.#}x{hero.Bounds.Height:0.#} | name at ({np.X:0.#},{np.Y:0.#}) {nameText.Bounds.Width:0.#}x{nameText.Bounds.Height:0.#}");
        Assert.Equal(236, hero.Bounds.Height, 1);
        Assert.Equal(win.Width, hero.Bounds.Width, 1);
        Assert.InRange(np.Y + nameText.Bounds.Height, 0, 236); // name sits inside the band

        // Controls live in the hero: Play, Shuffle, star, options.
        var play = view.GetVisualDescendants().OfType<Button>().First(x => x.Command == vm.PlayAllCommand);
        var shuffle = view.GetVisualDescendants().OfType<Button>().First(x => x.Command == vm.ShuffleAllCommand);
        var stars = view.GetVisualDescendants().OfType<Button>().Where(x => x.Command == vm.TogglePinCommand).ToList();
        var pp = play.TranslatePoint(new Point(0, 0), win)!.Value;
        var sp = shuffle.TranslatePoint(new Point(0, 0), win)!.Value;
        _o.WriteLine($"play at ({pp.X:0.#},{pp.Y:0.#}) {play.Bounds.Width:0.#}x{play.Bounds.Height:0.#} | shuffle at ({sp.X:0.#},{sp.Y:0.#}) | star buttons={stars.Count} visible={stars.Count(s => s.IsVisible)}");
        Assert.True(hero.IsVisualAncestorOf(play) && hero.IsVisualAncestorOf(shuffle));
        Assert.InRange(pp.Y + play.Bounds.Height, 0, 236);
        // 09-14: the playlist's cover sits in the hero beside the text, and the sort pill
        // joined the control row (no row of its own above the column headers).
        var cover = hero.GetVisualDescendants().OfType<Border>().First(b => b.Width == 156 && b.Height == 156);
        var cp = cover.TranslatePoint(new Point(0, 0), win)!.Value;
        var sort = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("glass-pill") && b.Flyout is MenuFlyout mf && mf.Items.Count == 11);
        var sp2 = sort.TranslatePoint(new Point(0, 0), win)!.Value;
        _o.WriteLine($"cover at ({cp.X:0.#},{cp.Y:0.#}) | sort pill at ({sp2.X:0.#},{sp2.Y:0.#}) inHero={hero.IsVisualAncestorOf(sort)} | name x={np.X:0.#}");
        Assert.InRange(cp.Y + 156, 0, 236);
        Assert.True(np.X > cp.X + 156, "name block starts right of the cover");
        Assert.True(hero.IsVisualAncestorOf(sort));
        Assert.InRange(Math.Abs(sp2.Y - pp.Y), 0, 6); // same row as Play
        // 09-15: the Sort pill sat higher (and taller) than the star/options pills: its
        // "12,6" padding around 12px text measured a different height from their "0,8"
        // around a 14px icon, and Fluent centres each button in the row. All the glass
        // pills now share one explicit height, so their tops and bottoms line up.
        var pills = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("glass-pill") && b.IsVisible).ToList();
        foreach (var p in pills)
        {
            var pt = p.TranslatePoint(new Point(0, 0), win)!.Value;
            _o.WriteLine($"glass pill top={pt.Y:0.##} height={p.Bounds.Height:0.##} width={p.Bounds.Width:0.##}");
        }
        Assert.Equal(3, pills.Count);
        Assert.Single(pills.Select(p => Math.Round(p.Bounds.Height, 1)).Distinct());
        Assert.Single(pills.Select(p => Math.Round(p.TranslatePoint(new Point(0, 0), win)!.Value.Y, 1)).Distinct());
        Assert.Equal(2, stars.Count);
        Assert.Equal(1, stars.Count(s => s.IsVisible));

        // Body: list column then the 292px rail on the right, holding Featured Artists + Suggested.
        var list = view.FindControl<ListBox>("TrackList")!;
        var rail = view.GetVisualDescendants().OfType<Border>().First(x => x.Width == 292);
        var lp = list.TranslatePoint(new Point(0, 0), win)!.Value;
        var rp = rail.TranslatePoint(new Point(0, 0), win)!.Value;
        var featured = rail.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(i => ReferenceEquals(i.ItemsSource, vm.TopFeaturedArtists) || i.ItemsSource == (object?)vm.TopFeaturedArtists);
        var itemsInRail = rail.GetVisualDescendants().OfType<ItemsControl>().Count();
        _o.WriteLine($"list at ({lp.X:0.#},{lp.Y:0.#}) {list.Bounds.Width:0.#}x{list.Bounds.Height:0.#} rows={list.ItemCount} | rail at ({rp.X:0.#},{rp.Y:0.#}) {rail.Bounds.Width:0.#}x{rail.Bounds.Height:0.#} itemsControls={itemsInRail} | featured={vm.HasFeaturedArtists} ({vm.TopFeaturedArtists.Count()}) suggestions={vm.HasSuggestions} ({vm.SuggestedTracks.Count})");
        Assert.Equal(3, list.ItemCount);
        Assert.True(rp.Y >= 236 - 0.5, "rail starts below the hero");
        Assert.Equal(win.Width, rp.X + rail.Bounds.Width, 1); // flush right
        Assert.True(lp.X + list.Bounds.Width <= rp.X + 0.5, "list ends before the rail");
        Assert.Equal(2, itemsInRail); // Featured Artists + Suggested

        // Star toggles the sidebar pin and swaps which pill shows.
        var visibleStar = stars.First(s => s.IsVisible);
        var star = visibleStar.TranslatePoint(new Point(visibleStar.Bounds.Width / 2, visibleStar.Bounds.Height / 2), win)!.Value;
        Assert.False(vm.IsPinned);
        win.MouseMove(star); Pump(2);
        win.MouseDown(star, MouseButton.Left); Pump(1);
        win.MouseUp(star, MouseButton.Left); Pump(2); await Task.Delay(50); Pump(4);
        _o.WriteLine($"after star click: IsPinned={vm.IsPinned} playlist.IsPinned={playlist.IsPinned} visible={string.Join(",", stars.Select(s => s.IsVisible))}");
        Assert.True(playlist.IsPinned);
        Assert.True(vm.IsPinned);
        Assert.False(visibleStar.IsVisible);
        Assert.True(stars.First(s => !ReferenceEquals(s, visibleStar)).IsVisible);
        win.Close();
    }
}
