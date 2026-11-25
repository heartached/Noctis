using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// An album page can't filter, but the search pill stays up on it (it follows the section's tab).
/// A new query typed there was dropped: the album page stayed and nothing was searched (11-25,
/// Songs "Hoshizora" → album link → typed "Scorpion Drake" + Enter). The query now goes to the
/// list the page belongs to: Back to it when it sits right behind the page, else the section's root.
/// </summary>
public class DetailPageSearchRedirectTests
{
    private static MainWindowViewModel BuildShell()
    {
        var services = new ServiceCollection();
        var program = typeof(App).Assembly.GetType("Noctis.Program")!;
        program.GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [services]);
        services.AddSingleton<IAudioPlayer>(new FakeAudioPlayer());
        var provider = services.BuildServiceProvider();
        App.Services = provider;
        return provider.GetRequiredService<MainWindowViewModel>();
    }

    private static void Navigate(MainWindowViewModel vm, string key)
    {
        typeof(MainWindowViewModel).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [key]);
        Dispatcher.UIThread.RunJobs();
    }

    private static T Field<T>(MainWindowViewModel vm, string name) =>
        (T)typeof(MainWindowViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;

    private static int HistoryCount(MainWindowViewModel vm) =>
        Field<System.Collections.ICollection>(vm, "_navigationHistory").Count;

    /// <summary>Types into the pill and lets the 300 ms search debounce land.</summary>
    private static void Type(MainWindowViewModel vm, string text)
    {
        vm.TopBar.SearchText = text;
        Settle();
    }

    private static void Settle()
    {
        var until = DateTime.UtcNow.AddMilliseconds(700);
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Album SomeAlbum() => new() { Id = Guid.NewGuid(), Name = "Hoshizora", Artist = "Somebody" };

    private static void Run(Action<MainWindowViewModel> body)
    {
        var vm = BuildShell();
        try { body(vm); }
        finally
        {
            (App.Services as IDisposable)?.Dispose();
            App.Services = null;
        }
    }

    [AvaloniaFact]
    public void NewQueryOnAlbumPage_OpenedFromFilteredSongs_GoesBackToSongsAndFilters() => Run(vm =>
    {
        var songs = Field<LibrarySongsViewModel>(vm, "_songsVm");
        Navigate(vm, "songs");
        vm.TopBar.IsSearchOpen = true;
        Type(vm, "Hoshizora");
        Assert.Equal("Hoshizora", songs.SearchText);

        vm.OpenAlbumDetail(SomeAlbum());
        Dispatcher.UIThread.RunJobs();
        var album = Assert.IsType<AlbumDetailViewModel>(vm.CurrentView);
        var depth = HistoryCount(vm);

        Type(vm, "Scorpion Drake");

        Assert.Same(songs, vm.CurrentView);
        Assert.Equal("Scorpion Drake", songs.SearchText);
        Assert.True(vm.TopBar.IsSearchOpen);
        Assert.Equal("Scorpion Drake", vm.TopBar.SearchText);
        Assert.Equal("Songs", vm.TopBar.CurrentTabName);
        // Back stepped off the album page (Forward returns to it), no new entry stacked.
        Assert.Equal(depth - 1, HistoryCount(vm));
        Assert.True(vm.CanGoForward);

        // Later keystrokes only filter.
        Type(vm, "Scorpion Drake II");
        Assert.Same(songs, vm.CurrentView);
        Assert.Equal("Scorpion Drake II", songs.SearchText);
        Assert.Equal(depth - 1, HistoryCount(vm));

        vm.GoForwardInHistoryCommand.Execute(null);
        Settle();
        Assert.Same(album, vm.CurrentView);
    });

    [AvaloniaFact]
    public void NewQueryOnAlbumPage_OpenedFromAnArtistPage_SearchesTheSectionRoot() => Run(vm =>
    {
        var albums = Field<LibraryAlbumsViewModel>(vm, "_albumsVm");
        Navigate(vm, "albums");
        vm.OpenArtistByName("Somebody");
        Dispatcher.UIThread.RunJobs();
        vm.OpenAlbumDetail(SomeAlbum());
        Dispatcher.UIThread.RunJobs();
        var album = Assert.IsType<AlbumDetailViewModel>(vm.CurrentView);
        Assert.True(vm.TopBar.IsSearchVisible);
        vm.TopBar.IsSearchOpen = true;
        var depth = HistoryCount(vm);

        Type(vm, "Scorpion Drake");

        Assert.Same(albums, vm.CurrentView);
        Assert.Equal("Scorpion Drake", albums.SearchText);
        Assert.True(vm.TopBar.IsSearchOpen);
        Assert.Equal("Scorpion Drake", vm.TopBar.SearchText);
        Assert.Equal(depth + 1, HistoryCount(vm));

        Type(vm, "Scorpion");
        Assert.Same(albums, vm.CurrentView);
        Assert.Equal(depth + 1, HistoryCount(vm));

        vm.GoBackInHistoryCommand.Execute(null);
        Settle();
        Assert.Same(album, vm.CurrentView);
    });

    /// <summary>Back to an album page restores the pill text it was left with; that restore is
    /// not a new query and must not bounce the user off the page.</summary>
    [AvaloniaFact]
    public void BackToAlbumPage_WithItsSavedQuery_StaysOnTheAlbumPage() => Run(vm =>
    {
        Navigate(vm, "songs");
        vm.TopBar.IsSearchOpen = true;
        Type(vm, "Hoshizora");
        vm.OpenAlbumDetail(SomeAlbum());
        Dispatcher.UIThread.RunJobs();
        var album = Assert.IsType<AlbumDetailViewModel>(vm.CurrentView);

        Navigate(vm, "albums");
        Settle();
        vm.GoBackInHistoryCommand.Execute(null);
        Settle();

        Assert.Same(album, vm.CurrentView);
        Assert.Equal("Hoshizora", vm.TopBar.SearchText);
    });

    [AvaloniaFact]
    public void StatisticsPage_PillSaysSongs_AndASearchLandsOnSongs() => Run(vm =>
    {
        // Statistics has no list: its search goes to Songs, so the pill must not read
        // "Search in Statistics".
        Navigate(vm, "statistics");
        Assert.Equal(Noctis.Localization.Loc.T("TopBar.SearchIn", Noctis.Localization.Loc.T("Nav.Songs")), vm.TopBar.SearchWatermark);

        vm.TopBar.IsSearchOpen = true;
        Type(vm, "Hoshizora");

        Assert.IsType<LibrarySongsViewModel>(vm.CurrentView);
    });
}
