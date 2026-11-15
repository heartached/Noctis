using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Converters;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #136: an album tagged "Alternative Rock; Alt-Pop; Indie" showed that text as one
/// genre in its header. Each genre now shows on its own, joined with " · " like the artist
/// page's GENRE fact ("Alternative rock · Art pop · Indie pop"). The stored tag is unchanged.
/// </summary>
public class AlbumGenreDisplayTests
{
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

    private const string ReporterGenre = "Alternative Rock; Alt-Pop; Indie";

    private static Album MakeAlbum(string genre)
    {
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "One", Artist = "A", AlbumArtist = "A", Album = "Record",
            Genre = genre, Year = 2024, TrackNumber = 1, Duration = TimeSpan.FromMinutes(3),
        };
        return new Album
        {
            Id = Guid.NewGuid(), Name = "Record", Artist = "A", Year = 2024, Genre = genre,
            TrackCount = 1, TotalDuration = track.Duration, Tracks = new List<Track> { track },
        };
    }

    [Fact]
    public void DotGenres_SplitsOnSemicolon_TrimsDropsBlanksAndDuplicates_KeepsOrder()
    {
        Assert.Equal("Alternative Rock · Alt-Pop · Indie", Track.DotGenres(ReporterGenre));
        Assert.Equal("Indie · rock", Track.DotGenres(" Indie ;; rock ; ROCK;Rock ; indie")); // first spelling wins
        Assert.Equal("Rock", Track.DotGenres("Rock"));
        Assert.Equal(string.Empty, Track.DotGenres(null));
        Assert.Equal(string.Empty, Track.DotGenres(" ; "));
    }

    [Fact]
    public void DotGenres_KeepsSlashAndAmpersandNames_Whole()
    {
        // Only ';' separates: '/' and '&' are part of real genre names.
        Assert.Equal("Hip-Hop/Rap · R&B · Drum & Bass", Track.DotGenres("Hip-Hop/Rap; R&B; Drum & Bass"));
    }

    [Fact]
    public void AlbumGenreDisplay_IsDotted_EmptyForTheUnknownPlaceholder_TagUntouched()
    {
        var album = MakeAlbum(ReporterGenre);
        Assert.Equal("Alternative Rock · Alt-Pop · Indie", album.GenreDisplay);
        Assert.Equal(ReporterGenre, album.Genre); // the stored value is not rewritten
        Assert.Equal("Alternative Rock · Alt-Pop · Indie", album.Tracks[0].GenreDisplay);

        Assert.Equal(string.Empty, new Album().GenreDisplay); // default "Unknown"
        Assert.Equal(string.Empty, MakeAlbum("").GenreDisplay);
    }

    [Fact]
    public void ResolveGenre_TakesTheFirstTaggedTrack_InAlbumOrder()
    {
        static Track T(string genre) => new() { Title = "t", Genre = genre };
        Assert.Equal("Rock; Pop", Album.ResolveGenre(new[] { T("Rock; Pop"), T("Jazz") }));
        // An untagged opener no longer leaves the whole album without a genre.
        Assert.Equal("Indie", Album.ResolveGenre(new[] { T(""), T("  "), T("Indie"), T("Jazz") }));
        Assert.Equal(string.Empty, Album.ResolveGenre(new[] { T(""), T("") }));
        Assert.Equal(string.Empty, Album.ResolveGenre(Array.Empty<Track>()));
    }

    [Fact]
    public void HeaderLine_ShowsEachGenre_ThenYearTracksLength()
    {
        var album = MakeAlbum(ReporterGenre);
        var line = (string)new MetadataLineConverter().Convert(
            new object?[] { album.GenreDisplay, album.ReleaseDateFormatted, album.TrackCountText, album.HeaderDurationFormatted },
            typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("Alternative Rock · Alt-Pop · Indie · 2024 · 1 track · 3m 00s", line);
    }

    [Fact]
    public void MobileMetaLine_ShowsEachGenre()
    {
        Assert.Equal("Alternative Rock · Alt-Pop · Indie · 2024", AlbumPageViewModel.BuildMetaLine(MakeAlbum(ReporterGenre)));
        var unknown = MakeAlbum("x");
        unknown.Genre = "Unknown";
        Assert.Equal("2024", AlbumPageViewModel.BuildMetaLine(unknown));
    }

    [AvaloniaFact]
    public void MountedAlbumPage_HeaderShowsTheGenresDotted()
    {
        var album = MakeAlbum(ReporterGenre);
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(album.Tracks);
        using var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm());
        var view = new AlbumDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Empty).ToList();
        // GitHub #220: each genre is its own link now, followed by the year line.
        foreach (var genre in new[] { "Alternative Rock", "Alt-Pop", "Indie" })
            Assert.Contains(genre, texts);
        Assert.Contains(texts, t => t.StartsWith("2024", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains(ReporterGenre, StringComparison.Ordinal));
        win.Close();
    }
}
