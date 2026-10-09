using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Audit 10-09 of the album tile menu on the Albums page and the album page's facts:
/// the tile's Play button queues in the album page's order, a Favorites click on a
/// selection follows the label that was clicked, and one-song counts read "1 song".
/// </summary>
public class AlbumsMenuAuditTests
{
    private sealed class NoOpPlayHistoryService : Noctis.Services.IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static Track Trk(string title, int disc = 1, int number = 0) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = "Artist",
        DiscNumber = disc,
        TrackNumber = number,
        FilePath = TestPaths.Primary("albummenu", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
    };

    private static Album Alb(string name, params Track[] tracks) =>
        new() { Id = Guid.NewGuid(), Name = name, Artist = "Artist", TrackCount = tracks.Length, Tracks = tracks.ToList() };

    private static LibraryAlbumsViewModel AlbumsVm(params Album[] libraryAlbums)
    {
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(libraryAlbums);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        return new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService()));
    }

    [Fact]
    public void TilePlayOrder_SinksSongsWithoutATrackNumber_LikeTheAlbumPage()
    {
        // The library and the album page put untagged track numbers last (LibraryService
        // RebuildIndexesCoreAsync, AlbumDetailViewModel.InAlbumOrder); the tile's Play
        // button started the album on the untagged song instead.
        var bonus = Trk("Bonus", 1, 0);
        var one = Trk("One", 1, 1);
        var two = Trk("Two", 1, 2);
        var discTwo = Trk("Disc Two", 2, 1);
        var album = Alb("X", discTwo, bonus, two, one);

        var ordered = AlbumTile.OrderedTracks(album);

        Assert.Equal(new[] { one, two, bonus, discTwo }, ordered);
    }

    [AvaloniaFact]
    public async Task Favorites_OnASelection_FollowsTheClickedLabel()
    {
        // Right-click a partly-favorited album: the menu says "Favorites" (add). With a
        // fully-favorited album also selected, that click used to un-favorite the other one.
        var vm = AlbumsVm();
        var partial = Alb("Partial", Trk("p1", number: 1), Trk("p2", number: 2));
        partial.Tracks[0].IsFavorite = true;
        var full = Alb("Full", Trk("f1", number: 1), Trk("f2", number: 2));
        foreach (var t in full.Tracks) t.IsFavorite = true;
        vm.CtrlSelectedAlbums = new List<Album> { partial, full };

        await vm.ToggleAlbumFavoritesCommand.ExecuteAsync(partial);

        Assert.True(partial.IsAllTracksFavorite);
        Assert.True(full.IsAllTracksFavorite);

        // And "Remove from Favorites" on a full album clears the whole selection.
        vm.CtrlSelectedAlbums = new List<Album> { partial, full };
        await vm.ToggleAlbumFavoritesCommand.ExecuteAsync(full);

        Assert.DoesNotContain(partial.Tracks, t => t.IsFavorite);
        Assert.DoesNotContain(full.Tracks, t => t.IsFavorite);
    }

    [AvaloniaFact]
    public async Task Favorites_OnAPartlyFavoritedAlbum_FavoritesEveryTrack_AndRaisesTheLabelFlip()
    {
        var album = Alb("Partial", Trk("p1", number: 1), Trk("p2", number: 2), Trk("p3", number: 3));
        var vm = AlbumsVm(album);
        album.Tracks[1].IsFavorite = true;
        var raised = new List<string?>();
        album.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.ToggleAlbumFavoritesCommand.ExecuteAsync(album);

        Assert.True(album.IsAllTracksFavorite);
        Assert.Contains(nameof(Album.IsAllTracksFavorite), raised);
    }

    [AvaloniaFact]
    public void SearchLyrics_OnAnAlbum_SearchesItsFirstSong()
    {
        var vm = AlbumsVm();
        Track? searched = null;
        vm.SetSearchLyricsAction(t => searched = t);
        var one = Trk("One", number: 1);
        var album = Alb("X", one, Trk("Two", number: 2));

        vm.SearchLyricsAlbumCommand.Execute(album);

        Assert.Same(one, searched);
    }

    [Fact]
    public void YearSongsLine_OneSong_IsSingular()
    {
        Assert.Equal("2020 · 1 song", new Album { Year = 2020, TrackCount = 1 }.YearSongsLine);
        Assert.Equal("1 song", new Album { TrackCount = 1 }.YearSongsLine);
        Assert.Equal("2020 · 8 songs", new Album { Year = 2020, TrackCount = 8 }.YearSongsLine);
    }

    [Fact]
    public void AlbumFooter_OneSong_IsSingular()
    {
        var convert = (Func<IList<object?>, string>)(values =>
            (string)AlbumFacts.SongsAndLength.Convert(values, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture)!);

        Assert.Equal("1 song, 3 min", convert(new object?[] { 1, "3 min" }));
        Assert.Equal("12 songs, 45 min", convert(new object?[] { 12, "45 min" }));
    }

    private sealed class FakeLastFm : ILastFmService
    {
        public bool IsAuthenticated => false;
        public string? Username => null;
        public void Configure(string? sessionKey) { }
        public Task<string> GetAuthUrlAsync() => Task.FromResult(string.Empty);
        public Task<bool> CompleteAuthAsync() => Task.FromResult(false);
        public string? GetSessionKey() => null;
        public void Logout() { }
        public Task ScrobbleAsync(Track track, DateTime startedAt) => Task.CompletedTask;
        public Task UpdateNowPlayingAsync(Track track) => Task.CompletedTask;
        public Task<string?> GetAlbumDescriptionAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string a, string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string a, string b, string? d, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string a, string b, CancellationToken ct = default) => Task.CompletedTask;
    }

    [AvaloniaFact]
    public void AlbumPage_FooterOfAOneSongAlbum_ReadsOneSong()
    {
        // Owner 10-09: the album page footer read "1 songs, 3 min".
        AddToPlaylistDialogTests.EnsureAppStyles();
        var track = Trk("Only", number: 1);
        var album = Alb("Single", track);
        album.TotalDuration = track.Duration;
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm());
        var view = new AlbumDetailView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("1 song, 3 min", texts);
            Assert.DoesNotContain(texts, t => t != null && t.Contains("1 songs"));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AlbumPage_Footer_FollowsALiveLanguageSwitch()
    {
        // Review 10-09: the footer's converter only re-ran when the count or length changed,
        // so an album page open during a language switch kept the old language.
        AddToPlaylistDialogTests.EnsureAppStyles();
        var track = Trk("Only", number: 1);
        var album = Alb("Single", track);
        album.TotalDuration = track.Duration;
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm());
        var view = new AlbumDetailView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains("1 song, 3 min", view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

            // The footer keys are English-only so far; a language pack supplies the Spanish.
            Noctis.Localization.Loc.Instance.SetOverlays(new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                ["es"] = new Dictionary<string, string> { ["AlbumDetail.FooterOneSong"] = "1 canción, {0}" },
            });
            Noctis.Localization.Loc.Instance.SetCulture("es");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.Contains("1 canción, 3 min", view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        }
        finally
        {
            Noctis.Localization.Loc.Instance.SetCulture("en");
            Noctis.Localization.Loc.Instance.SetOverlays(null);
            window.Close();
        }
    }
}
