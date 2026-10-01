using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #112: right-clicking a Most Played / Last Played row on Home offers View Album and
/// View Artist. The items live in the shared track menu builder and stay hidden unless a view
/// wires them, so the other views' menus are unchanged.
/// </summary>
public class HomeTrackMenuViewAlbumArtistTests
{
    private static readonly RelayCommand None = new(() => { });

    private static TrackContextMenuBuilder BuildMenu()
    {
        var builder = new TrackContextMenuBuilder();
        var resources = new Border();
        foreach (var key in new[] { "HeartFillIcon", "StarIcon", "TrashIcon" })
            resources.Resources[key] = Avalonia.Media.Geometry.Parse("M0 0L1 1");
        builder.Build("Remove from Library", null, resources);
        return builder;
    }

    private static void Bind(TrackContextMenuBuilder builder, Track track,
        ICommand? viewAlbum = null, ICommand? viewArtist = null)
        => builder.Bind(track, None, None, None, None, None, None, None, None, None, None,
            viewAlbumCommand: viewAlbum, viewArtistCommand: viewArtist);

    private static Track T(string artist = "Adele", string album = "25") => new()
    {
        Id = Guid.NewGuid(), Title = "Song", Artist = artist, AlbumArtist = artist, Album = album,
        AlbumId = Track.ComputeAlbumId(artist, album),
    };

    private static Separator SeparatorAbove(TrackContextMenuBuilder b)
        => (Separator)b.Menu.Items[b.Menu.Items.IndexOf(b.ViewAlbum) - 1]!;

    // ── Builder ──

    [AvaloniaFact]
    public void ViewItems_StayHidden_WhenTheViewWiresNoCommands()
    {
        var b = BuildMenu();
        Bind(b, T());

        Assert.False(b.ViewAlbum.IsVisible);
        Assert.False(b.ViewArtist.IsVisible);
        Assert.False(SeparatorAbove(b).IsVisible);
    }

    [AvaloniaFact]
    public void ViewAlbum_OpensTheClickedTrack()
    {
        Track? opened = null;
        var b = BuildMenu();
        var track = T();
        Bind(b, track, viewAlbum: new RelayCommand<Track>(t => opened = t));

        Assert.True(b.ViewAlbum.IsVisible);
        Assert.Equal("View Album", b.ViewAlbum.Header);
        Assert.True(SeparatorAbove(b).IsVisible);
        b.ViewAlbum.Command!.Execute(b.ViewAlbum.CommandParameter);
        Assert.Same(track, opened);
    }

    [AvaloniaTheory]
    [InlineData("Unknown Album")]
    [InlineData(" unknown album ")]
    [InlineData("")]
    public void ViewAlbum_Hidden_ForTheUnknownAlbumPlaceholder(string album)
    {
        var b = BuildMenu();
        Bind(b, T(album: album), viewAlbum: new RelayCommand<Track>(_ => { }));

        Assert.False(b.ViewAlbum.IsVisible);
    }

    [AvaloniaFact]
    public void ViewAlbum_Hidden_WhenTheCommandCannotOpenIt_AndBackOnceItCan()
    {
        var canOpen = false;
        var command = new RelayCommand<Track>(_ => { }, _ => canOpen);
        var b = BuildMenu();
        var track = T();

        Bind(b, track, viewAlbum: command);
        Assert.False(b.ViewAlbum.IsVisible);

        // Same row re-opened after the album appeared (a scan finished): shown and clickable.
        canOpen = true;
        Bind(b, track, viewAlbum: command);
        Assert.True(b.ViewAlbum.IsVisible);
        Assert.True(b.ViewAlbum.IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public void ViewArtist_OneCreditedArtist_IsAPlainItem()
    {
        string? opened = null;
        var b = BuildMenu();
        Bind(b, T(artist: "Adele"), viewArtist: new RelayCommand<string>(n => opened = n));

        Assert.True(b.ViewArtist.IsVisible);
        Assert.Equal("View Artist", b.ViewArtist.Header);
        Assert.Empty(b.ViewArtist.Items);
        b.ViewArtist.Command!.Execute(b.ViewArtist.CommandParameter);
        Assert.Equal("Adele", opened);
    }

    [AvaloniaFact]
    public void ViewArtist_SeveralCreditedArtists_OneSubItemPerName()
    {
        var opened = new List<string?>();
        var b = BuildMenu();
        Bind(b, T(artist: "Kanye West, GLC, Consequence"), viewArtist: new RelayCommand<string>(opened.Add));

        Assert.True(b.ViewArtist.IsVisible);
        Assert.Null(b.ViewArtist.Command);
        var subItems = b.ViewArtist.Items.OfType<MenuItem>().ToList();
        Assert.Equal(new[] { "Kanye West", "GLC", "Consequence" }, subItems.Select(i => i.Header as string));
        foreach (var item in subItems)
            item.Command!.Execute(item.CommandParameter);
        Assert.Equal(new[] { "Kanye West", "GLC", "Consequence" }, opened);

        // Re-bound to a solo track, the submenu is gone again.
        Bind(b, T(artist: "Adele"), viewArtist: new RelayCommand<string>(opened.Add));
        Assert.Empty(b.ViewArtist.Items);
        Assert.Equal("Adele", b.ViewArtist.CommandParameter);
    }

    [AvaloniaTheory]
    [InlineData("Unknown Artist")]
    [InlineData("")]
    public void ViewArtist_Hidden_WithoutARealArtist(string artist)
    {
        var b = BuildMenu();
        Bind(b, T(artist: artist), viewArtist: new RelayCommand<string>(_ => { }));

        Assert.False(b.ViewArtist.IsVisible);
    }

    // ── Home ──

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
        });
    }

    [AvaloniaFact]
    public void HomeViewAlbum_CanOpenOnlyAlbumsTheLibraryHas()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));
        var inLibrary = T();
        ((List<Album>)lib.Albums).Add(new Album { Id = inLibrary.AlbumId, Name = "25", Artist = "Adele" });

        Assert.True(vm.ViewAlbumFromTrackCommand.CanExecute(inLibrary));
        Assert.False(vm.ViewAlbumFromTrackCommand.CanExecute(T(album: "Gone")));
    }

    [AvaloniaFact]
    public async Task MostAndLastPlayedRows_RightClick_ViewAlbumAndArtistNavigate()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var a = T(artist: "Bad Bunny, Chencho Corleone", album: "Un Verano Sin Ti");
        a.AlbumArtist = "Bad Bunny";
        a.AlbumId = Track.ComputeAlbumId("Bad Bunny", "Un Verano Sin Ti");
        a.PlayCount = 9;
        var b = T(artist: "Chase Atlantic", album: "Beauty in Death");
        b.PlayCount = 5;
        lib.TrackList.AddRange(new[] { a, b });
        ((List<Album>)lib.Albums).AddRange(new[]
        {
            new Album { Id = a.AlbumId, Name = a.Album, Artist = "Bad Bunny", Tracks = new List<Track> { a } },
            new Album { Id = b.AlbumId, Name = b.Album, Artist = b.Artist, Tracks = new List<Track> { b } },
        });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        player.History.Add(a);
        player.History.Add(b);
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));
        var openedAlbums = new List<Track>();
        var openedArtists = new List<string>();
        vm.ViewAlbumRequested += (_, t) => openedAlbums.Add(t);
        vm.SetViewArtistAction(openedArtists.Add);
        await vm.RefreshAsync();

        var view = new HomeView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var rows = view.GetVisualDescendants().OfType<Button>()
            .Where(r => r.Classes.Contains("home-chart-row")).ToList();
        var mostPlayed = rows.First(r => r.DataContext is TopSongRow { IsLastPlayed: false } row && row.Track == a);
        var lastPlayed = rows.First(r => r.DataContext is TopSongRow { IsLastPlayed: true } row && row.Track == b);

        // Most Played: View Album opens the row's album; the two credited artists get one entry each.
        var menu = OpenMenu(mostPlayed);
        var viewAlbum = Item(menu, "View Album");
        Assert.True(viewAlbum.IsVisible);
        viewAlbum.Command!.Execute(viewAlbum.CommandParameter);
        var viewArtist = Item(menu, "View Artist");
        Assert.True(viewArtist.IsVisible);
        var names = viewArtist.Items.OfType<MenuItem>().ToList();
        Assert.Equal(new[] { "Bad Bunny", "Chencho Corleone" }, names.Select(i => i.Header as string));
        names[1].Command!.Execute(names[1].CommandParameter);
        menu.Close();

        // Last Played: same items, single artist opens directly.
        menu = OpenMenu(lastPlayed);
        viewAlbum = Item(menu, "View Album");
        Assert.True(viewAlbum.IsVisible);
        viewAlbum.Command!.Execute(viewAlbum.CommandParameter);
        viewArtist = Item(menu, "View Artist");
        Assert.True(viewArtist.IsVisible);
        viewArtist.Command!.Execute(viewArtist.CommandParameter);
        menu.Close();

        Assert.Equal(new[] { a, b }, openedAlbums);
        Assert.Equal(new[] { "Chencho Corleone", "Chase Atlantic" }, openedArtists);
        win.Close();
    }

    private static ContextMenu OpenMenu(Button row)
    {
        row.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = row });
        var menu = row.ContextMenu;
        Assert.NotNull(menu);
        Assert.True(menu!.IsOpen);
        return menu;
    }

    private static MenuItem Item(ContextMenu menu, string header)
        => menu.Items.OfType<MenuItem>().Single(i => i.Header as string == header);
}
