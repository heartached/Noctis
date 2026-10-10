using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;
using Path = Avalonia.Controls.Shapes.Path;

namespace Noctis.Tests;

/// <summary>
/// View Artist ▸ on Home's Most Played / Last Played rows shows each credited artist's round
/// picture (owner 10-09). Opt-in per open through Bind(artistPhotoSource:), so every other
/// menu keeps plain names; an artist with no cached picture gets the Artists page placeholder.
/// </summary>
public class MenuArtistAvatarTests
{
    private readonly ITestOutputHelper _o;
    public MenuArtistAvatarTests(ITestOutputHelper o) => _o = o;

    private static readonly RelayCommand None = new(() => { });

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
        });
    }

    private static TrackContextMenuBuilder BuildMenu()
    {
        EnsureAppStyles();
        var builder = new TrackContextMenuBuilder();
        builder.Build("Remove from Library", null, new Border(), v2: true, removeIsDanger: true);
        return builder;
    }

    private static void Bind(TrackContextMenuBuilder builder, Track track, ICommand viewArtist,
        Func<string, string?>? photos)
        => builder.Bind(track, None, None, None, None, None, None, None, None, None, None,
            viewAlbumCommand: new RelayCommand<Track>(_ => { }), viewArtistCommand: viewArtist,
            artistPhotoSource: photos);

    private static Track T(string artist, string album = "An Evening with Silk Sonic") => new()
    {
        Id = Guid.NewGuid(), Title = "Leave the Door Open", Artist = artist, AlbumArtist = "Silk Sonic", Album = album,
        AlbumId = Track.ComputeAlbumId("Silk Sonic", album), FilePath = "C:/m/door.flac",
    };

    private static List<MenuItem> ArtistRows(TrackContextMenuBuilder b) => b.ViewArtist.Items.OfType<MenuItem>().ToList();

    private static Border Avatar(MenuItem row)
    {
        var avatar = Assert.IsType<Border>(row.Icon);
        Assert.Contains("mv2-avatar", avatar.Classes);
        return avatar;
    }

    private static CachedImage Photo(MenuItem row) => ((Panel)Avatar(row).Child!).Children.OfType<CachedImage>().Single();
    private static Path Glyph(MenuItem row) => ((Panel)Avatar(row).Child!).Children.OfType<Path>().Single();

    // ── Builder ──

    [AvaloniaFact]
    public void WithoutAPhotoSource_ArtistRowsStayPlainNames()
    {
        var b = BuildMenu();
        Bind(b, T("Silk Sonic, Bruno Mars, Anderson .Paak"), new RelayCommand<string>(_ => { }), photos: null);

        var rows = ArtistRows(b);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Null(r.Icon));
    }

    [AvaloniaFact]
    public async Task WithAPhotoSource_EachRowGetsARoundPicture_OrThePlaceholder()
    {
        var b = BuildMenu();
        var asked = new List<string>();
        var uiThread = Environment.CurrentManagedThreadId;
        var lookupThread = uiThread;
        string? Source(string name)
        {
            lock (asked) asked.Add(name);
            lookupThread = Environment.CurrentManagedThreadId;
            return name == "Bruno Mars" ? "C:/art/bruno.jpg" : null;
        }
        string? opened = null;
        Bind(b, T("Silk Sonic, Bruno Mars, Anderson .Paak"), new RelayCommand<string>(n => opened = n), Source);

        var rows = ArtistRows(b);
        Assert.Equal(new[] { "Silk Sonic", "Bruno Mars", "Anderson .Paak" }, rows.Select(r => r.Header as string));
        // The placeholder is there at once: the lookup has not answered yet.
        foreach (var row in rows)
        {
            var avatar = Avatar(row);
            Assert.Equal(MenuV2.AvatarSize, avatar.Width);
            Assert.Equal(MenuV2.AvatarSize / 2, avatar.CornerRadius.TopLeft);
            Assert.True(avatar.ClipToBounds);
            Assert.NotNull(Glyph(row).Data);
        }

        await b.ArtistAvatarsLoaded;
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(uiThread, lookupThread);
        Assert.Equal(3, asked.Count);
        Assert.Equal("C:/art/bruno.jpg", Photo(rows[1]).SourcePath);
        Assert.True(Photo(rows[1]).IsVisible);
        foreach (var i in new[] { 0, 2 })
        {
            Assert.Null(Photo(rows[i]).SourcePath);
            Assert.False(Photo(rows[i]).IsVisible);
        }

        // Still the same command per name.
        rows[2].Command!.Execute(rows[2].CommandParameter);
        Assert.Equal("Anderson .Paak", opened);
    }

    [AvaloniaFact]
    public async Task ReboundForAnotherTrack_TheOldLookupIsDropped()
    {
        var b = BuildMenu();
        using var release = new ManualResetEventSlim();
        Bind(b, T("Silk Sonic, Bruno Mars"), new RelayCommand<string>(_ => { }), name =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return "C:/art/stale-" + name + ".jpg";
        });
        var stale = b.ArtistAvatarsLoaded;
        var oldRows = ArtistRows(b);

        Bind(b, T("Daft Punk, Pharrell Williams", album: "Random Access Memories"),
            new RelayCommand<string>(_ => { }), name => name == "Daft Punk" ? "C:/art/daft.jpg" : null);
        release.Set();
        await stale;
        await b.ArtistAvatarsLoaded;
        Dispatcher.UIThread.RunJobs();

        var rows = ArtistRows(b);
        Assert.Equal(new[] { "Daft Punk", "Pharrell Williams" }, rows.Select(r => r.Header as string));
        Assert.Equal("C:/art/daft.jpg", Photo(rows[0]).SourcePath);
        Assert.Null(Photo(rows[1]).SourcePath);
        Assert.All(oldRows, r => Assert.Null(Photo(r).SourcePath));

        // Re-bound without a source (another Home section): plain names again.
        Bind(b, T("Silk Sonic, Bruno Mars"), new RelayCommand<string>(_ => { }), photos: null);
        Assert.All(ArtistRows(b), r => Assert.Null(r.Icon));
    }

    [AvaloniaFact]
    public async Task AThrowingLookup_LeavesThePlaceholder()
    {
        var b = BuildMenu();
        Bind(b, T("Silk Sonic, Bruno Mars"), new RelayCommand<string>(_ => { }),
            _ => throw new IOException("cache folder unreadable"));
        await b.ArtistAvatarsLoaded;
        Dispatcher.UIThread.RunJobs();

        Assert.All(ArtistRows(b), r =>
        {
            Assert.False(Photo(r).IsVisible);
            Assert.NotNull(Glyph(r).Data);
        });
    }

    /// <summary>
    /// In the real menu styles the picture is 20px, centred on the same line as the 16px line
    /// icons of the rows around it, and the names start where every other row's label starts.
    /// </summary>
    [AvaloniaFact]
    public async Task OpenSubmenu_AvatarSitsInTheIconColumn_LabelsLineUp()
    {
        var b = BuildMenu();
        Bind(b, T("Silk Sonic, Bruno Mars, Anderson .Paak"), new RelayCommand<string>(_ => { }), _ => null);
        await b.ArtistAvatarsLoaded;

        var owner = new Border { Width = 50, Height = 50 };
        var win = new Window { Width = 1200, Height = 1000, Content = owner };
        win.Show();
        owner.ContextMenu = b.Menu;
        b.Menu.Open(owner);
        Dispatcher.UIThread.RunJobs();
        b.ViewArtist.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();

        var (albumIconCentre, albumLabelX) = IconAndLabel(b.ViewAlbum);
        foreach (var row in ArtistRows(b))
        {
            var avatar = Avatar(row);
            // Drawn 2px past the 16px slot on each side: nothing between it and the row clips it.
            Assert.All(avatar.GetVisualAncestors().OfType<Control>().TakeWhile(c => c != row), c => Assert.False(c.ClipToBounds));
            Assert.True(avatar.IsEffectivelyVisible);
            Assert.Equal(MenuV2.AvatarSize, avatar.Bounds.Width, 1);
            Assert.Equal(MenuV2.AvatarSize, avatar.Bounds.Height, 1);
            var (centre, labelX) = IconAndLabel(row);
            _o.WriteLine($"{row.Header}: avatar centre {centre}, label {labelX:0.#} (View Album icon {albumIconCentre}, label {albumLabelX:0.#}), row {row.Bounds.Height:0.#}");
            Assert.Equal(albumLabelX, labelX, 0.5);
            Assert.Equal(albumIconCentre.X, centre.X, 0.5);
            Assert.Equal(row.Bounds.Height / 2, centre.Y, 0.5);
            Assert.Equal(32, row.Bounds.Height, 0.5);
        }

        b.Menu.Close();
        win.Close();
    }

    private static (Point IconCentre, double LabelX) IconAndLabel(MenuItem row)
    {
        var icon = (Control)row.Icon!;
        var centre = icon.TranslatePoint(new Point(icon.Bounds.Width / 2, icon.Bounds.Height / 2), row)!.Value;
        var header = row.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_HeaderPresenter");
        return (centre, header.TranslatePoint(new Point(0, 0), row)!.Value.X);
    }

    // ── Home ──

    [AvaloniaFact]
    public async Task Home_MostAndLastPlayed_ShowPictures_TheRailsDoNot()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var a = T("Silk Sonic, Bruno Mars, Anderson .Paak");
        a.PlayCount = 9;
        lib.TrackList.Add(a);
        ((List<Album>)lib.Albums).Add(new Album { Id = a.AlbumId, Name = a.Album, Artist = "Silk Sonic", Tracks = new List<Track> { a } });
        using var persistence = new TestPersistenceService();
        var images = new ArtistImageService(new HttpClient(new NoNetwork()), persistence);
        // Bruno Mars has a portrait in the Artists page cache; the other two do not.
        // (> 5 KB: the service purges tiny files as old Last.fm placeholders.)
        var bruno = images.GetCachedImagePath(LibraryService.ComputeArtistId("Bruno Mars"));
        await File.WriteAllBytesAsync(bruno, new byte[8 * 1024]);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        player.History.Add(a);
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib), artistImages: images);
        await vm.RefreshAsync();

        Assert.Equal(bruno, vm.CachedArtistPhoto("Bruno Mars"));
        Assert.Null(vm.CachedArtistPhoto("Anderson .Paak"));

        var view = new HomeView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var rows = view.GetVisualDescendants().OfType<Button>().Where(r => r.Classes.Contains("home-chart-row")).ToList();
        foreach (var lastPlayed in new[] { false, true })
        {
            var row = rows.First(r => r.DataContext is TopSongRow { IsLastPlayed: var lp } tr && lp == lastPlayed && tr.Track == a);
            row.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = row });
            var menu = row.ContextMenu!;
            Assert.True(menu.IsOpen);
            var viewArtist = menu.Items.OfType<MenuItem>().Single(i => i.Header as string == "View Artist");
            var names = viewArtist.Items.OfType<MenuItem>().ToList();
            Assert.Equal(new[] { "Silk Sonic", "Bruno Mars", "Anderson .Paak" }, names.Select(i => i.Header as string));
            await WaitFor(() => Photo(names[1]).SourcePath != null);
            Assert.Equal(bruno, Photo(names[1]).SourcePath);
            Assert.False(Photo(names[0]).IsVisible);
            Assert.False(Photo(names[2]).IsVisible);
            menu.Close();
        }

        // A rail (Heavy Rotation) shares the builder but did not opt in: plain names.
        var railOwner = new Border { DataContext = a };
        var railWin = new Window { Content = railOwner };
        railWin.Show();
        typeof(HomeView).GetMethod("OnHeavyRotationContextRequested",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(view, new object?[] { railOwner, new ContextRequestedEventArgs() });
        var railMenu = railOwner.ContextMenu!;
        var railArtist = railMenu.Items.OfType<MenuItem>().Single(i => i.Header as string == "View Artist");
        Assert.Equal(3, railArtist.Items.Count);
        Assert.All(railArtist.Items.OfType<MenuItem>(), i => Assert.Null(i.Icon));
        railMenu.Close();
        railWin.Close();
        win.Close();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition());
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    // ── Eyeball probe (NOCTIS_TEST_SKIA=1): the View Artist ▸ card with one picture and two
    //    placeholders beside a few ordinary rows, dark and light ──

    [AvaloniaFact]
    public async Task Probe_RenderArtistSubmenu()
    {
        if (!HeadlessTestApp.RealRendering) return;
        EnsureAppStyles();
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "noctis-avatar-probe");
        Directory.CreateDirectory(dir);
        var photo = System.IO.Path.Combine(dir, "bruno.png");
        using (var bmp = new WriteableBitmap(new PixelSize(256, 256), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
        {
            using (var fb = bmp.Lock())
            {
                var px = new byte[256 * 256 * 4];
                for (var y = 0; y < 256; y++)
                for (var x = 0; x < 256; x++)
                {
                    var o = (y * 256 + x) * 4;
                    px[o] = (byte)(60 + x / 2); px[o + 1] = (byte)(90 + y / 3); px[o + 2] = (byte)(200 - y / 2); px[o + 3] = 255;
                }
                System.Runtime.InteropServices.Marshal.Copy(px, 0, fb.Address, px.Length);
            }
            bmp.Save(photo);
        }

        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var b = BuildMenu();
            Bind(b, T("Silk Sonic, Bruno Mars, Anderson .Paak"), new RelayCommand<string>(_ => { }),
                name => name == "Bruno Mars" ? photo : null);
            await b.ArtistAvatarsLoaded;

            // The submenu's rows, laid out inline in a card under a few top-level rows.
            var card = new ContextMenu();
            card.Items.Add(MenuV2.Row(new Border(), "View Album", "MenuLineAlbum"));
            card.Items.Add(MenuV2.Row(new Border(), "Start Radio", "MenuLineRadio"));
            card.Items.Add(new Separator());
            foreach (var row in ArtistRows(b))
            {
                b.ViewArtist.Items.Remove(row);
                card.Items.Add(row);
            }
            var win = new Window
            {
                Width = 420, Height = 300, Content = new Border { Margin = new Thickness(16), Child = card },
                Background = (IBrush?)(Application.Current.TryGetResource("AppMainBackground", variant, out var bg) ? bg : null),
            };
            win.Show();
            for (var i = 0; i < 50; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"noctis-menu-artist-avatars-{variant}.png");
            win.CaptureRenderedFrame()!.Save(path);
            _o.WriteLine(path);
            win.Close();
        }
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
    }
}
