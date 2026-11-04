using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #159: Home's recently played Albums row gets an adjustable size, a New in Library
/// row shows the newest albums by date added (played or not) with its own size, and a
/// setting puts New in Library first.
/// </summary>
public class HomeNewInLibraryTests
{
    private sealed class FakePlayHistory : IPlayHistoryService
    {
        public List<PlayHistoryEvent> Log { get; } = new();
        public IReadOnlyList<PlayHistoryEvent> Events => Log;
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) => Log.Add(new PlayHistoryEvent { TrackId = track.Id, Title = track.Title, PlayedAtUtc = DateTime.UtcNow });
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static Album AddAlbum(FakeLibraryService lib, string name, DateTime added, int tracks = 1)
    {
        var album = new Album { Id = Guid.NewGuid(), Name = name, Artist = "A" };
        for (var i = 0; i < tracks; i++)
        {
            var t = new Track
            {
                Id = Guid.NewGuid(), Title = $"{name} {i + 1}", Artist = "A", AlbumArtist = "A", Album = name,
                AlbumId = album.Id, TrackNumber = i + 1, Duration = TimeSpan.FromSeconds(180),
                FilePath = $"C:/m/{name}/{i + 1}.mp3", DateAdded = added,
            };
            album.Tracks.Add(t);
            lib.TrackList.Add(t);
        }
        ((List<Album>)lib.Albums).Add(album);
        return album;
    }

    private static (HomeViewModel Home, SettingsViewModel Settings, FakeLibraryService Lib, FakePlayHistory Log, PlayerViewModel Player) Build()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var settings = new SettingsViewModel(persistence, lib, new FakeHistoryLog());
        var log = new FakePlayHistory();
        var home = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib), playHistory: log, settings: settings);
        return (home, settings, lib, log, player);
    }

    // ── Pure builders ──

    [Fact]
    public void BuildNewInLibrary_NewestAddedFirst_PlayedOrNot_CappedAndStable()
    {
        var lib = new FakeLibraryService();
        var now = DateTime.UtcNow;
        var old = AddAlbum(lib, "Old", now.AddDays(-30));
        var mid = AddAlbum(lib, "Mid", now.AddDays(-3));
        var newest = AddAlbum(lib, "Newest", now.AddHours(-1));
        old.Tracks[0].PlayCount = 50; // played or not makes no difference
        // One new song on an old album makes the album new (the Albums page's Date Added sort).
        var refreshed = AddAlbum(lib, "Refreshed", now.AddDays(-90), tracks: 2);
        refreshed.Tracks[1].DateAdded = now.AddDays(-1);
        ((List<Album>)lib.Albums).Add(new Album { Id = Guid.NewGuid(), Name = "Empty" });
        // Same instant (one folder scan): name order.
        var b = AddAlbum(lib, "B tie", now.AddDays(-10));
        var a = AddAlbum(lib, "a tie", now.AddDays(-10));

        var all = HomeRowsBuilder.BuildNewInLibrary(lib.Albums, 100);
        Assert.Equal(new[] { "Newest", "Refreshed", "Mid", "a tie", "B tie", "Old" }, all.Select(x => x.Name));

        Assert.Equal(new[] { "Newest", "Refreshed" }, HomeRowsBuilder.BuildNewInLibrary(lib.Albums, 2).Select(x => x.Name));
        Assert.Empty(HomeRowsBuilder.BuildNewInLibrary(lib.Albums, 0));
    }

    [Fact]
    public void BuildRecentAlbums_DistinctNewestFirst_UnresolvedTakeNoSlot()
    {
        var lib = new FakeLibraryService();
        var x = AddAlbum(lib, "X", DateTime.UtcNow, 2);
        var y = AddAlbum(lib, "Y", DateTime.UtcNow);
        var z = AddAlbum(lib, "Z", DateTime.UtcNow);
        var loose = new Track { Id = Guid.NewGuid(), Title = "loose" };                     // no album id
        var gone = new Track { Id = Guid.NewGuid(), Title = "gone", AlbumId = Guid.NewGuid() }; // album not in the library

        var history = new[] { x.Tracks[0], loose, gone, x.Tracks[1], y.Tracks[0], z.Tracks[0] };

        Assert.Equal(new[] { "X", "Y" }, HomeRowsBuilder.BuildRecentAlbums(history, lib.GetAlbumById, 2).Select(a => a.Name));
        Assert.Equal(new[] { "X", "Y", "Z" }, HomeRowsBuilder.BuildRecentAlbums(history, lib.GetAlbumById, 10).Select(a => a.Name));
        Assert.Empty(HomeRowsBuilder.BuildRecentAlbums(history, lib.GetAlbumById, 0));
    }

    [Fact]
    public void AppSettings_Defaults_KeepTodaysHome_AndHandEditedCountsSnap()
    {
        var s = new AppSettings();
        Assert.Equal(6, s.HomeRecentAlbumsCount);
        Assert.Equal(10, s.HomeNewAlbumsCount);
        Assert.False(s.HomeNewAlbumsFirst);
        Assert.True(s.HomeNewAlbumsExpanded);

        s.HomeRecentAlbumsCount = 0;
        s.HomeNewAlbumsCount = 4000;
        s.ClampToValidRanges();
        Assert.Equal(6, s.HomeRecentAlbumsCount);
        Assert.Equal(100, s.HomeNewAlbumsCount);
        Assert.Equal(20, AppSettings.SnapHomeAlbumCount(18));
    }

    // ── Settings round trip ──

    private sealed class RoundTripPersistence : TestPersistenceService
    {
        public AppSettings Loaded { get; init; } = new();
        public AppSettings? Saved { get; private set; }
        public override Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Loaded);
        public override Task SaveSettingsAsync(AppSettings settings) { Saved = settings; return Task.CompletedTask; }
    }

    [AvaloniaFact]
    public async Task Settings_LoadAndSave_CarryTheHomeAlbumOptions()
    {
        var persistence = new RoundTripPersistence
        {
            Loaded = new AppSettings { HomeRecentAlbumsCount = 50, HomeNewAlbumsCount = 20, HomeNewAlbumsFirst = true, HomeNewAlbumsExpanded = false },
        };
        var settings = new SettingsViewModel(persistence, new FakeLibraryService(), new FakeHistoryLog());
        await settings.LoadAsync();

        Assert.Equal(50, settings.HomeRecentAlbumsCount);
        Assert.Equal(20, settings.HomeNewAlbumsCount);
        Assert.True(settings.HomeNewAlbumsFirst);
        Assert.False(settings.HomeNewAlbumsExpanded);

        settings.HomeNewAlbumsCount = 100;
        await settings.SaveAsync();
        Assert.NotNull(persistence.Saved);
        Assert.Equal(50, persistence.Saved!.HomeRecentAlbumsCount);
        Assert.Equal(100, persistence.Saved.HomeNewAlbumsCount);
        Assert.True(persistence.Saved.HomeNewAlbumsFirst);
        Assert.False(persistence.Saved.HomeNewAlbumsExpanded);
    }

    [AvaloniaFact]
    public void ResetEverything_UnfoldsTheHomeSectionsOnTheLiveHomePage()
    {
        // Settings reset put HomeNewAlbumsExpanded (and the other folds) back to open, but the
        // Home page adopted them only when it was built, so its folded sections stayed shut
        // until a restart while Settings already said open.
        var (home, settings, _, _, _) = Build();
        home.ToggleSectionCommand.Execute("NewInLibrary");
        home.ToggleSectionCommand.Execute("TopSongs");
        Assert.False(home.IsNewInLibraryExpanded);
        Assert.False(home.IsTopSongsExpanded);

        settings.ResetSettingsToDefaults(new AppSettings());

        Assert.True(home.IsNewInLibraryExpanded);
        Assert.True(home.IsTopSongsExpanded);
    }

    // ── Home view model ──

    [AvaloniaFact]
    public async Task NewInLibrary_FollowsItsCount_AndPicksUpALibraryPublish()
    {
        var (home, settings, lib, log, _) = Build();
        var now = DateTime.UtcNow;
        for (var i = 0; i < 12; i++) AddAlbum(lib, $"N{i:00}", now.AddDays(-i));
        log.RecordPlay(lib.TrackList[0]); // something played, so Home is not the "nothing played" page

        await home.RefreshAsync();
        Assert.Equal(10, home.NewInLibraryAlbums.Count);
        Assert.Equal("N00", home.NewInLibraryAlbums[0].Name);
        Assert.True(home.IsNewInLibraryVisible);

        // The row's pill writes the setting; the row follows.
        home.SetNewAlbumsCountCommand.Execute("6");
        Assert.Equal(6, settings.HomeNewAlbumsCount);
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(6, home.NewInLibraryAlbums.Count);

        // A scan adds an album: the next rebuild after the publish puts it first.
        AddAlbum(lib, "Fresh", now.AddMinutes(1));
        home.MarkDirty();
        await home.RefreshAsync();
        Assert.Equal("Fresh", home.NewInLibraryAlbums[0].Name);
        Assert.Equal(6, home.NewInLibraryAlbums.Count);
    }

    [AvaloniaFact]
    public async Task RecentAlbums_ShowSixByDefault_AndTheChosenCount()
    {
        var (home, settings, lib, log, _) = Build();
        for (var i = 0; i < 15; i++) log.RecordPlay(AddAlbum(lib, $"P{i:00}", DateTime.UtcNow).Tracks[0]);

        await home.RefreshAsync();
        Assert.Equal(6, home.RecentlyPlayedAlbums.Count);
        Assert.Equal("P14", home.RecentlyPlayedAlbums[0].Name);

        settings.HomeRecentAlbumsCount = 10;
        Assert.Equal(10, home.RecentlyPlayedAlbums.Count);
        Assert.Equal("P05", home.RecentlyPlayedAlbums[^1].Name);
    }

    // Adjacent bug: a counted play trimmed the row to 10 while a rebuild keeps 6, so playing an
    // album not on the row grew it to seven tiles until the next rebuild.
    [AvaloniaFact]
    public async Task ACountedPlay_KeepsTheRowAtItsSize()
    {
        var (home, _, lib, log, player) = Build();
        for (var i = 0; i < 8; i++) log.RecordPlay(AddAlbum(lib, $"P{i}", DateTime.UtcNow).Tracks[0]);
        await home.RefreshAsync();
        Assert.Equal(6, home.RecentlyPlayedAlbums.Count);

        var other = AddAlbum(lib, "Other", DateTime.UtcNow);
        typeof(PlayerViewModel).GetMethod("CountPlay", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(player, new object[] { other.Tracks[0] });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Other", home.RecentlyPlayedAlbums[0].Name);
        Assert.Equal(6, home.RecentlyPlayedAlbums.Count);
    }

    [AvaloniaFact]
    public async Task NewInLibrary_StaysOffTheNothingPlayedPage()
    {
        var (home, _, lib, _, _) = Build();
        AddAlbum(lib, "Unplayed", DateTime.UtcNow);
        await home.RefreshAsync();

        Assert.True(home.ShowNothingPlayed);
        Assert.Single(home.NewInLibraryAlbums);
        Assert.False(home.IsNewInLibraryVisible);
    }

    [AvaloniaFact]
    public void FoldState_RoundTripsThroughSettings()
    {
        var (home, settings, _, _, _) = Build();
        Assert.True(home.IsNewInLibraryExpanded);
        home.ToggleSectionCommand.Execute("NewInLibrary");
        Assert.False(home.IsNewInLibraryExpanded);
        Assert.False(settings.HomeNewAlbumsExpanded);
    }

    // ── View: order ──

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

    [AvaloniaFact]
    public async Task View_ShowsBothRows_AndNewFirstSwapsThem()
    {
        EnsureAppStyles();
        var (home, settings, lib, log, _) = Build();
        log.RecordPlay(AddAlbum(lib, "Played", DateTime.UtcNow.AddDays(-5)).Tracks[0]);
        AddAlbum(lib, "Added", DateTime.UtcNow);
        await home.RefreshAsync();

        var view = new HomeView { DataContext = home };
        var win = new Window { Width = 1300, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var recent = view.FindControl<StackPanel>("RecentAlbumsSection")!;
        var added = view.FindControl<StackPanel>("NewInLibrarySection")!;
        var page = (Panel)recent.Parent!;
        Assert.True(recent.IsVisible);
        Assert.True(added.IsVisible);
        Assert.Equal(page.Children.IndexOf(recent) + 1, page.Children.IndexOf(added)); // default: Albums first

        settings.HomeNewAlbumsFirst = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(page.Children.IndexOf(added) + 1, page.Children.IndexOf(recent));

        settings.HomeNewAlbumsFirst = false;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(page.Children.IndexOf(recent) + 1, page.Children.IndexOf(added));

        // Both rows use the shared album tile; the new row lists the added album first.
        var row = view.FindControl<ItemsControl>("NewInLibraryRow")!;
        Assert.Equal("Added", ((Album)row.Items[0]!).Name);
        win.Close();
    }

    [AvaloniaFact]
    public async Task View_CountPillMenu_SetsTheRowSize()
    {
        EnsureAppStyles();
        var (home, settings, lib, log, _) = Build();
        for (var i = 0; i < 25; i++) log.RecordPlay(AddAlbum(lib, $"A{i:00}", DateTime.UtcNow.AddDays(-i)).Tracks[0]);
        await home.RefreshAsync();

        var view = new HomeView { DataContext = home };
        var win = new Window { Width = 1300, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        foreach (var (pillName, pick, read) in new (string, string, Func<int>)[]
                 {
                     ("NewAlbumsCountPill", "20", () => settings.HomeNewAlbumsCount),
                     ("RecentAlbumsCountPill", "10", () => settings.HomeRecentAlbumsCount),
                 })
        {
            var pill = view.FindControl<Button>(pillName)!;
            var menu = (MenuFlyout)pill.Flyout!;
            menu.ShowAt(pill);
            Dispatcher.UIThread.RunJobs();
            var item = menu.Items.OfType<MenuItem>().Single(m => (string?)m.Header == pick);
            Assert.NotNull(item.Command);
            Assert.True(item.Command!.CanExecute(item.CommandParameter));
            item.Command.Execute(item.CommandParameter);
            Assert.Equal(int.Parse(pick), read());
            menu.Hide();
        }
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(20, home.NewInLibraryAlbums.Count);
        Assert.Equal(10, home.RecentlyPlayedAlbums.Count);
        win.Close();
    }
}
