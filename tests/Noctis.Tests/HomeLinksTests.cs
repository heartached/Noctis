using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Home links (11-23 feedback): Continue listening's artwork and title open the song's album
/// (the album page then reveals the song), its artist line opens each artist; the Most Played
/// / Last Played rows' artist names open the artist while a double-click elsewhere still plays.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class HomeLinksTests
{
    public HomeLinksTests() => ArtistCredit.ResetToDefaults();

    private static Track Song(string title, Guid albumId, int plays) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = "Bonobo, Jordan Rakei", AlbumArtist = "Bonobo",
        Album = "Migration", AlbumId = albumId, FilePath = TestPaths.Primary("home", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(4), PlayCount = plays,
    };

    [AvaloniaFact]
    public async Task Hero_art_and_title_open_the_album_and_artists_open_their_page()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var albumId = Guid.NewGuid();
        var a = Song("Kerala", albumId, 12);
        var b = Song("Break Apart", albumId, 9);
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(new[] { a, b });
        ((List<Album>)lib.Albums).Add(new Album { Id = albumId, Name = "Migration", Artist = "Bonobo", Tracks = new List<Track> { a, b } });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        player.History.Add(a);
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));
        var albums = new List<Track>();
        var artists = new List<string>();
        vm.ViewAlbumRequested += (_, t) => albums.Add(t);
        vm.SetViewArtistAction(artists.Add);
        await vm.RefreshAsync();

        var view = new HomeView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 900, Content = view };
        window.Show();
        try
        {
            var deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline && vm.ContinueTrack == null)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.NotNull(vm.ContinueTrack);

            foreach (var name in new[] { "ContinueArtButton", "ContinueTitleButton" })
            {
                var button = view.FindControl<Button>(name)!;
                Assert.True(button.Command!.CanExecute(button.CommandParameter), name);
                button.Command.Execute(button.CommandParameter);
            }
            Assert.Equal(new[] { vm.ContinueTrack, vm.ContinueTrack }, albums);

            var artist = view.FindControl<Button>("ContinueArtistButton")!;
            Assert.True(ArtistCreditLink.GetIsEnabled(artist));
            artist.Command!.Execute(artist.CommandParameter);
            Assert.Equal("Bonobo, Jordan Rakei", Assert.Single(artists));

            // Chart rows: the artist name is its own per-name link inside the row.
            var deadline2 = Environment.TickCount64 + 5000;
            List<Button> RowArtistLinks() => view.GetVisualDescendants().OfType<Button>()
                .Where(x => x.Classes.Contains("artist-credit-link") && x.FindAncestorOfType<Button>() is { } row
                            && row.Classes.Contains("home-chart-row"))
                .ToList();
            while (Environment.TickCount64 < deadline2 && RowArtistLinks().Count == 0)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }
            var links = RowArtistLinks();
            Assert.NotEmpty(links);
            Assert.All(links, l => Assert.Same(vm.ViewArtistCommand, l.Command));
        }
        finally
        {
            window.Close();
        }
    }
}
