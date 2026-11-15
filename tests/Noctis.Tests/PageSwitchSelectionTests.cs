using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 11-15 live report: Albums, Ctrl-click a cover, Songs, back to Albums: the plate is gone.
/// Leaving a page has reset its Ctrl-selection since before PageDeck: each view's
/// OnDetachedFromVisualTree clears the selected set and strips the plates ("so it doesn't leak
/// back when the view is revisited"), and PageDeck still takes a hidden page out of the visual
/// tree. These drive the real MainWindow (MainWindowViewModel.Navigate -> CurrentView -> PageDeck)
/// with a kept page and with PageDeck.KeepSections off (every page leaves the tree on a switch,
/// as the ContentControl did before), and pin that both agree and that data and plates go
/// together: a Ctrl-selection is cleared with its plates, a list's own selection (Artists list,
/// Queue) stays with its highlight; never a selection the user cannot see.
/// </summary>
public class PageSwitchSelectionTests
{
    private sealed class Boot : IDisposable
    {
        public required MainWindowViewModel Vm;
        public required MainWindow Window;
        public required ServiceProvider Provider;
        public required IServiceProvider? PreviousServices;
        public void Dispose()
        {
            Window.Close();
            Pump();
            App.Services = PreviousServices!;
            Provider.Dispose();
            PageDeck.KeepSections = true;
        }
    }

    private static void Pump(int n = 3)
    {
        for (var i = 0; i < n; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpUntil(Func<bool> done, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (!done() && Environment.TickCount64 < deadline)
        {
            Pump(1);
            Thread.Sleep(5);
        }
        Pump();
    }

    private sealed class PlaylistPersistence : TestPersistenceService
    {
        public List<Playlist> Lists { get; } = new();
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(Lists.ToList());
    }

    private static Boot Start(bool keep)
    {
        PageDeck.KeepSections = keep;
        AddToPlaylistDialogTests.EnsureAppStyles();
        var lib = new FakeLibraryService();
        var albums = (List<Album>)lib.Albums;
        for (var a = 0; a < 40; a++)
        {
            var artist = $"Artist {a % 12:00}";
            var album = new Album { Id = Guid.NewGuid(), Name = $"Album {a:00}", Artist = artist, Year = 2000 + a % 20 };
            for (var t = 0; t < 4; t++)
            {
                var track = new Track
                {
                    Id = Guid.NewGuid(), Title = $"Song {a:00}-{t}", Artist = artist, AlbumArtist = artist, Album = album.Name,
                    AlbumId = album.Id, TrackNumber = t + 1, Duration = TimeSpan.FromMinutes(3), IsFavorite = t == 0,
                    FilePath = TestPaths.Primary("switch", $"{a:00}-{t}.mp3"),
                };
                album.Tracks.Add(track);
                lib.TrackList.Add(track);
            }
            album.TrackCount = album.Tracks.Count;
            albums.Add(album);
        }
        for (var i = 0; i < 12; i++)
            lib.ArtistList.Add(new Artist { Id = Guid.NewGuid(), Name = $"Artist {i:00}", AlbumCount = 3, TrackCount = 12 });
        var persistence = new PlaylistPersistence();
        persistence.Lists.Add(new Playlist { Name = "Mix", TrackIds = lib.TrackList.Take(30).Select(t => t.Id).ToList() });

        var services = new ServiceCollection();
        typeof(App).Assembly.GetType("Noctis.Program")!
            .GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [services]);
        services.AddSingleton<IAudioPlayer>(new FakeAudioPlayer());
        services.AddSingleton<ILibraryService>(lib);
        services.AddSingleton<IPersistenceService>(persistence);
        var provider = services.BuildServiceProvider();
        var previous = App.Services;
        App.Services = provider;
        var vm = provider.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow(vm) { Width = 1400, Height = 900 };
        // The app's page views (App.axaml.cs registers these with its CachedViewLocator).
        window.DataTemplates.Insert(0, new CachedViewLocator(new Dictionary<Type, Func<Control>>
        {
            [typeof(HomeViewModel)] = () => new HomeView(),
            [typeof(LibrarySongsViewModel)] = () => new LibrarySongsView(),
            [typeof(LibraryAlbumsViewModel)] = () => new LibraryAlbumsView(),
            [typeof(LibraryArtistsViewModel)] = () => new LibraryArtistsView(),
            [typeof(FavoritesViewModel)] = () => new FavoritesView(),
            [typeof(QueueViewModel)] = () => new QueueView(),
        }));
        window.DataTemplates.Insert(1, new FuncDataTemplate<PlaylistViewModel>((_, _) => new PlaylistView(), supportsRecycling: true));
        window.Show();
        PumpUntil(() => vm.CurrentView != null && vm.Sidebar.Playlists.Count == 1);
        return new Boot { Vm = vm, Window = window, Provider = provider, PreviousServices = previous };
    }

    private static void Navigate(MainWindowViewModel vm, string key)
    {
        typeof(MainWindowViewModel).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [key]);
        Pump(4);
    }

    private static T Page<T>(Boot b) where T : Control =>
        b.Window.GetVisualDescendants().OfType<T>().Single(v => v.IsEffectivelyVisible);

    private static void CtrlA(Window window) => window.RaiseEvent(new KeyEventArgs
    {
        RoutedEvent = InputElement.KeyDownEvent, Key = Key.A, KeyModifiers = PlatformHelper.PrimaryModifier, Source = window,
    });

    private static HashSet<T> Field<T>(object view, string name) =>
        (HashSet<T>)view.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;

    private static int Plates(Control page) =>
        page.GetVisualDescendants().OfType<Control>().Count(c => c.IsEffectivelyVisible && c.Classes.Contains("ctrl-selected"));

    /// <summary>Ctrl+A on the page, then away and back: before and after, data and plates agree.</summary>
    private static void CtrlSelection_AfterAPageSwitch_IsClearedWithItsPlates<TView, TItem>(bool keep, string key, string field,
        Func<Boot, string?>? openKey = null) where TView : Control where TItem : class
    {
        using var b = Start(keep);
        var target = openKey?.Invoke(b) ?? key;
        Navigate(b.Vm, target);
        var page = Page<TView>(b);
        PumpUntil(() => page.GetVisualDescendants().OfType<ListBoxItem>().Any());
        CtrlA(b.Window);
        Pump();
        var selected = Field<TItem>(page, field);
        Assert.NotEmpty(selected);
        Assert.True(Plates(page) > 0, "Ctrl+A put no plate on screen");

        Navigate(b.Vm, key == "songs" ? "albums" : "songs");
        Navigate(b.Vm, target);
        var back = Page<TView>(b);
        // A section is the same cached view; a playlist opens a new view model and, with a
        // section shown in between, a new view, as before PageDeck.
        if (openKey == null) Assert.Same(page, back);
        Assert.Empty(Field<TItem>(back, field));
        Assert.Equal(0, Plates(back));
        // What Delete, the shortcuts and the menus act on (MainWindow.PageSelectedTracks): nothing.
        if (back is ITrackSelectionSource source) Assert.Empty(source.GetSelectedTracks());
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Albums_CtrlSelection_IsClearedWithItsPlates(bool keep) =>
        CtrlSelection_AfterAPageSwitch_IsClearedWithItsPlates<LibraryAlbumsView, Album>(keep, "albums", "_selectedAlbums");

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Favorites_CtrlSelection_IsClearedWithItsPlates(bool keep) =>
        CtrlSelection_AfterAPageSwitch_IsClearedWithItsPlates<FavoritesView, FavoriteItem>(keep, "favorites", "_selectedItems");

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Songs_CtrlSelection_IsClearedWithItsPlates(bool keep) =>
        CtrlSelection_AfterAPageSwitch_IsClearedWithItsPlates<LibrarySongsView, Track>(keep, "songs", "_selectedTracks");

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Playlist_CtrlSelection_IsClearedWithItsPlates(bool keep) =>
        CtrlSelection_AfterAPageSwitch_IsClearedWithItsPlates<PlaylistView, Track>(keep, "playlist", "_selectedTracks",
            b => "playlist:" + b.Vm.Sidebar.Playlists[0].Id);

    /// <summary>The Artists list's own selection (arrow keys, a click) survives a switch, with
    /// its highlight, as it did before.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArtistsList_Selection_SurvivesWithItsHighlight(bool keep)
    {
        using var b = Start(keep);
        Navigate(b.Vm, "artists");
        var artistsVm = (LibraryArtistsViewModel)b.Vm.CurrentView!;
        if (!artistsVm.IsListView) artistsVm.ToggleViewModeCommand.Execute(null);
        var page = Page<LibraryArtistsView>(b);
        var table = page.FindControl<ListBox>("ArtistTable")!;
        PumpUntil(() => table.ContainerFromIndex(3) != null);
        table.SelectedIndex = 3;
        Pump();

        Navigate(b.Vm, "songs");
        Navigate(b.Vm, "artists");
        PumpUntil(() => table.ContainerFromIndex(3) is { IsEffectivelyVisible: true });
        Assert.Equal(3, table.SelectedIndex);
        var selectedRows = table.GetVisualDescendants().OfType<ListBoxItem>().Where(r => r.IsSelected && r.IsEffectivelyVisible).ToList();
        Assert.Equal(3, table.IndexFromContainer(Assert.Single(selectedRows)));
        artistsVm.ToggleViewModeCommand.Execute(null);
    }

    /// <summary>The Queue page's Up Next selection survives a switch, with its highlight, as before.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void QueuePage_Selection_SurvivesWithItsHighlight(bool keep)
    {
        using var b = Start(keep);
        b.Vm.Player.ReplaceQueueAndPlay(b.Provider.GetRequiredService<ILibraryService>().Tracks.Take(40).ToList(), 0);
        Navigate(b.Vm, "queue");
        var page = Page<QueueView>(b);
        var list = page.FindControl<ListBox>("UpNextList")!;
        PumpUntil(() => list.ContainerFromIndex(2) != null);
        list.SelectedIndex = 2;
        Pump();

        Navigate(b.Vm, "songs");
        Navigate(b.Vm, "queue");
        PumpUntil(() => list.ContainerFromIndex(2) is { IsEffectivelyVisible: true });
        Assert.Equal(2, list.SelectedIndex);
        var selectedRows = list.GetVisualDescendants().OfType<ListBoxItem>().Where(r => r.IsSelected && r.IsEffectivelyVisible).ToList();
        Assert.Equal(2, list.IndexFromContainer(Assert.Single(selectedRows)));
        b.Vm.Player.IsQueuePopupOpen = false;
    }
}
