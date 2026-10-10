using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord, veil 10-07: on an artist page the album tiles bounced ("twerk") and the scrollbar
/// kept appearing and disappearing. Tiles were sized from the scroll viewer's CONTENT width,
/// which narrows by the scrollbar's gutter while it shows: the bar appearing shrank the tiles
/// enough to hide it again, which grew them back. Tile size now depends on the window width
/// only, so a page height at the edge of needing a scrollbar can't flip it.
/// </summary>
public class ArtistDetailTileBounceTests
{
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

    private static Album MakeAlbum(string name, string artist, int year)
    {
        var album = new Album { Id = Guid.NewGuid(), Name = name, Artist = artist, Year = year };
        album.Tracks = Enumerable.Range(1, 3).Select(n => new Track
        {
            Id = Guid.NewGuid(), Title = $"{name} {n}", Artist = artist, AlbumArtist = artist,
            Album = name, AlbumId = album.Id, TrackNumber = n, Duration = TimeSpan.FromSeconds(200),
            FilePath = TestPaths.Primary("Music", name, $"{n:00}.mp3"),
        }).ToList();
        return album;
    }

    [AvaloniaFact]
    public void TileSize_DoesNotChangeWithWindowHeight()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        foreach (var album in new[] { MakeAlbum("WITHERED", "d4vd", 2025), MakeAlbum("Petals to Thorns", "d4vd", 2023) })
        {
            lib.TrackList.AddRange(album.Tracks);
            ((List<Album>)lib.Albums).Add(album);
        }
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("d4vd", lib, player);
        var win = new Window { Width = 1200, Height = 700, Content = new ArtistDetailView { DataContext = vm } };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        var settled = vm.TileArtworkSize;

        // Sweep every height from "scrollbar needed" to "not needed": each pixel step
        // crosses the band where the old content-width sizing flipped back and forth.
        var changes = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ArtistDetailViewModel.TileArtworkSize)) changes++;
        };
        for (var h = 400; h <= 1400; h++)
        {
            win.Height = h;
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(0, changes);
        Assert.Equal(settled, vm.TileArtworkSize);
        win.Close();
    }
}
