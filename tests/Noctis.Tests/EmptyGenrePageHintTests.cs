using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Live check 11-23 (#260): removing the ',' genre separator left the open "Indie" genre page
/// empty, and it said "Add music folders in Settings to get started" as if the library were
/// empty. A genre or artist page with nothing in it keeps "No albums found" without that hint.
/// </summary>
public class EmptyGenrePageHintTests
{
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int n = 6)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static async Task<(Window Win, LibraryAlbumsView View)> Mount(bool withAlbum, string? genre)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        if (withAlbum)
        {
            var album = new Album { Id = Guid.NewGuid(), Name = "Rock Album", Artist = "Artist", Year = 2020 };
            album.Tracks.Add(new Track { Id = Guid.NewGuid(), Title = "T", Album = "Rock Album", AlbumId = album.Id, Genre = "Rock" });
            ((List<Album>)lib.Albums).Add(album);
        }
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib), settings);
        var view = new LibraryAlbumsView { DataContext = vm };
        var win = new Window { Width = 1200, Height = 800, Content = view };
        win.Show(); Pump();
        vm.IsActive = true;
        if (genre != null) vm.SetGenreFilter(genre);
        for (var i = 0; i < 40; i++) { Pump(1); await Task.Delay(5); }
        Pump();
        return (win, view);
    }

    private static bool Shows(Control view, string key)
    {
        var text = Loc.T(key);
        return view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text && t.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task EmptyGenrePage_SaysNoAlbums_WithoutTheAddFoldersHint()
    {
        var (win, view) = await Mount(withAlbum: true, genre: "Indie");
        Assert.True(Shows(view, "LibraryAlbums.NoAlbumsFound"));
        Assert.False(Shows(view, "LibraryAlbums.AddMusicFoldersSettings"));
        win.Close();
    }

    [AvaloniaFact]
    public async Task EmptyLibrary_StillSaysAddMusicFolders()
    {
        var (win, view) = await Mount(withAlbum: false, genre: null);
        Assert.True(Shows(view, "LibraryAlbums.NoAlbumsFound"));
        Assert.True(Shows(view, "LibraryAlbums.AddMusicFoldersSettings"));
        win.Close();
    }
}
