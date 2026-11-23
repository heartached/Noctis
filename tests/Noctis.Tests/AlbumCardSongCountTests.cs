using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #258: album cards (Albums grid, Home, artist page, More By Artist) carry the
/// album's song count on their one-line caption, switchable in Settings. The switch is
/// app-wide static state, so every test that flips it lives in this one class (xUnit runs a
/// class's tests one at a time) and puts it back.
/// </summary>
public sealed class AlbumCardSongCountTests
{
    [Fact]
    public void CardCaption_AddsTheSongCount_AfterArtistAndYear()
    {
        var album = new Album { Artist = "Tycho", Year = 2011, TrackCount = 12 };
        Assert.Equal("Tycho · 2011 · 12 songs", album.CardCaption);
    }

    [Fact]
    public void CardCaption_WithoutYear_IsArtistAndCount()
    {
        var album = new Album { Artist = "Tycho", TrackCount = 12 };
        Assert.Equal("Tycho · 12 songs", album.CardCaption);
    }

    [Fact]
    public void CardCaption_OneSong_IsSingular()
    {
        var album = new Album { Artist = "Tycho", Year = 2011, TrackCount = 1 };
        Assert.Equal("Tycho · 2011 · 1 song", album.CardCaption);
    }

    [Fact]
    public void KindCardCaption_AddsTheSongCount_AfterKindAndYear()
    {
        var album = new Album { Artist = "Tycho", Year = 1982, TrackCount = 12, Tracks = Songs(12) };
        Assert.Equal("Album · 1982 · 12 songs", album.KindCardCaption);
        var undated = new Album { Artist = "Tycho", TrackCount = 1, Tracks = Songs(1) };
        Assert.Equal("Single · 1 song", undated.KindCardCaption);
    }

    [Fact]
    public void Captions_SettingOff_DropTheCount()
    {
        var album = new Album { Artist = "Tycho", Year = 2011, TrackCount = 12, Tracks = Songs(12) };
        Album.ShowSongCountOnCards = false;
        try
        {
            Assert.Equal("Tycho · 2011", album.CardCaption);
            Assert.Equal("Album · 2011", album.KindCardCaption);
        }
        finally { Album.ShowSongCountOnCards = true; }
    }

    [Fact]
    public void Captions_UseTheSongCountFormat_TheAppSets()
    {
        var album = new Album { Artist = "Tycho", Year = 2011, TrackCount = 3 };
        Album.SetSongCountFormat(n => $"{n} песни");
        try
        {
            Assert.Equal("Tycho · 2011 · 3 песни", album.CardCaption);
            Assert.Equal("2011 · 3 песни", album.YearSongsLine);
        }
        finally { Album.SetSongCountFormat(null); }
    }

    [AvaloniaFact]
    public async Task SettingsToggle_UpdatesTheLibraryAlbumsCaptions_AtOnce()
    {
        var library = new FakeLibraryService();
        var album = new Album { Artist = "Tycho", Year = 2011, TrackCount = 12, Tracks = Songs(12) };
        ((List<Album>)library.Albums).Add(album);
        var root = Path.Combine(Path.GetTempPath(), "noctis-258-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var vm = new SettingsViewModel(new PersistenceService(root), library, new FakeHistoryLog());
        try
        {
            await vm.LoadAsync();
            Assert.True(vm.ShowAlbumSongCount);

            var raised = new List<string?>();
            album.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            vm.ShowAlbumSongCount = false;

            Assert.Equal("Tycho · 2011", album.CardCaption);
            Assert.Contains(nameof(Album.CardCaption), raised);
            Assert.Contains(nameof(Album.KindCardCaption), raised);

            vm.ShowAlbumSongCount = true;
            Assert.Equal("Tycho · 2011 · 12 songs", album.CardCaption);
        }
        finally
        {
            Album.ShowSongCountOnCards = true;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void ShowAlbumSongCount_DefaultsOn()
    {
        Assert.True(new AppSettings().ShowAlbumSongCount);
    }

    private static List<Track> Songs(int n)
        => Enumerable.Range(1, n).Select(i => new Track { Title = $"T{i}", TrackNumber = i }).ToList();
}
