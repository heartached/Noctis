using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Headless mount of the new artist page and of the lyrics page's video backdrop
/// layer: pins that the XAML resolves its resources/bindings at runtime (compile-time
/// XAML checks don't catch a missing StaticResource) and that the sections realize.
/// </summary>
public class ArtistDetailViewMountTests
{
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis/Assets/Styles.axaml")
        });
    }

    private static Album MakeAlbum(string name, string artist, int year, int trackCount)
    {
        var id = Guid.NewGuid();
        var album = new Album { Id = id, Name = name, Artist = artist, Year = year, Tracks = new List<Track>() };
        for (var i = 1; i <= trackCount; i++)
        {
            album.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"{name} {i}", Artist = artist, AlbumArtist = artist, Album = name,
                AlbumId = id, TrackNumber = i, DiscNumber = 1, Year = year, Duration = TimeSpan.FromMinutes(3),
                PlayCount = trackCount - i,
            });
        }
        album.TrackCount = trackCount;
        return album;
    }

    [AvaloniaFact]
    public void ArtistPage_MountsWithHeroPopularAndReleases()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(new[]
        {
            MakeAlbum("Phases", "Chase Atlantic", 2019, 12),
            MakeAlbum("Beauty in Death", "Chase Atlantic", 2021, 10),
            MakeAlbum("Single", "Chase Atlantic", 2022, 1),
        });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, player);
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Chase Atlantic", texts);
        Assert.DoesNotContain("ARTIST", texts); // kicker removed (user ask, 09-03)
        Assert.Contains("Popular", texts);
        Assert.Contains("Latest Release", texts);
        Assert.Contains("Releases", texts);

        // Three release tiles and ten popular pills realized.
        var tiles = view.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("album-tile"));
        Assert.Equal(3, tiles);
        var pills = view.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("browse-item"));
        Assert.Equal(ArtistDetailViewModel.MaxPopular, pills);

        // Podium numerals: #1 gold, #2 silver, #3 bronze, #4 plain.
        var ranks = view.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("rank-num")).ToDictionary(t => t.Text!);
        Assert.True(ranks["1"].Classes.Contains("gold"));
        Assert.True(ranks["2"].Classes.Contains("silver"));
        Assert.True(ranks["3"].Classes.Contains("bronze"));
        Assert.False(ranks["4"].Classes.Contains("gold") || ranks["4"].Classes.Contains("silver") || ranks["4"].Classes.Contains("bronze"));

        // Singles chip narrows the grid live.
        vm.SetReleaseFilterCommand.Execute("singles");
        Dispatcher.UIThread.RunJobs();
        tiles = view.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("album-tile"));
        Assert.Equal(1, tiles);
    }

    [AvaloniaFact]
    public void ArtistPage_RestoresScrollPositionAfterBackNavigation()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        // Enough releases to make the page taller than the window.
        ((List<Album>)lib.Albums).AddRange(Enumerable.Range(0, 30)
            .Select(i => MakeAlbum($"Album {i:00}", "Tall Artist", 1990 + i, 8)));
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new ArtistDetailViewModel("Tall Artist", lib, player);

        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 700, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var scroll = view.FindControl<ScrollViewer>("PageScrollViewer")!;
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height, "page must be scrollable for this test");
        scroll.Offset = new Vector(0, 900);
        Dispatcher.UIThread.RunJobs();
        var left = scroll.Offset.Y;
        Assert.True(left > 0);

        // Navigate away (view detaches, VM lives on in history) and come back to a
        // freshly built view for the same VM — the way the ContentControl does it.
        win.Content = null;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(left, vm.SavedScrollOffset);

        var view2 = new ArtistDetailView { DataContext = vm };
        win.Content = view2;
        for (var i = 0; i < 6; i++) Dispatcher.UIThread.RunJobs();
        var scroll2 = view2.FindControl<ScrollViewer>("PageScrollViewer")!;
        Assert.Equal(left, scroll2.Offset.Y, 1);
    }

    [AvaloniaFact]
    public void LyricsPage_MediaBackdropFollowsThePlayerSetting()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(), persistence, lib);
        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var layer = view.FindControl<Grid>("LyricsMediaBackdrop");
        var backdrop = view.FindControl<VideoBackdrop>("MediaBackdrop");
        Assert.NotNull(layer);
        Assert.NotNull(backdrop);
        Assert.False(layer!.IsVisible);           // no clip chosen → layer hidden, decoder idle

        // A path that doesn't exist must show the layer (scrim) but never start a decoder.
        player.LyricsBackgroundMediaPath = Path.Combine(Path.GetTempPath(), "missing-clip.mp4");
        Dispatcher.UIThread.RunJobs();
        Assert.True(layer.IsVisible);
        Assert.Equal(player.LyricsBackgroundMediaPath, backdrop!.Source);

        player.LyricsBackgroundMediaPath = string.Empty;
        Dispatcher.UIThread.RunJobs();
        Assert.False(layer.IsVisible);
    }

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }
}
