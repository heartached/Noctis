using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "On the Wine accent, clicking a track flickers white for a split second" (user, 09-24).
/// Track-list rows (Album page, Songs, Playlist, Folders) paint hover, stripe and the
/// now-playing fill on one Border.row-body with a 40ms Background BrushTransition. Starting
/// playback flips the clicked (hovered) row from the hover wash (#0AFFFFFF: white at ~4%)
/// to the opaque accent, and Avalonia tweens colours in linear light, so the in-between
/// frames are a pale, half-opaque pink far brighter than either end. Dark accents (Wine)
/// show it most. Transitions are deliberately left live here: nulling them hides the bug.
/// </summary>
public class NowPlayingRowSnapTests
{
    private readonly ITestOutputHelper _output;

    public NowPlayingRowSnapTests(ITestOutputHelper output) => _output = output;

    private static readonly Color Wine = Color.Parse("#8E1B3A");

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
        public Task<string?> GetAlbumDescriptionAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string artistName, string albumName, string? description, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.CompletedTask;
    }

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

    private static List<Track> Tracks(Guid albumId, int count) =>
        Enumerable.Range(1, count).Select(i => new Track
        {
            Id = Guid.NewGuid(),
            FilePath = TestPaths.Primary("wine", "Album", $"{i:00} Song.flac"),
            Title = $"Song {i}",
            Artist = "Juice WRLD",
            AlbumArtist = "Juice WRLD",
            Album = "Fighting Demons",
            AlbumId = albumId,
            TrackNumber = i,
            DiscNumber = 1,
            Duration = TimeSpan.FromMinutes(3),
        }).ToList();

    /// <summary>Pumps the render clock for ~150ms of wall time (the tween runs on real
    /// time) and records every Background the row takes. The Transitions setter must come
    /// BEFORE the Background setter in the .now-playing style: setters apply in order, and
    /// the other way round the 40ms tween still catches the change.</summary>
    private static List<Color> Sample(Border row)
    {
        var seen = new List<Color>();
        var deadline = Environment.TickCount64 + 150;
        while (Environment.TickCount64 < deadline)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            if (row.Background is ISolidColorBrush b && (seen.Count == 0 || seen[^1] != b.Color))
                seen.Add(b.Color);
            Thread.Sleep(2);
        }
        return seen;
    }

    [AvaloniaFact]
    public void AlbumPage_ClickedRow_SnapsToWineAccent_WithoutTweeningThroughWhite()
    {
        EnsureAppStyles();
        var albumId = Guid.NewGuid();
        var tracks = Tracks(albumId, 4);
        var album = new Album
        {
            Id = albumId, Name = "Fighting Demons", Artist = "Juice WRLD",
            TrackCount = tracks.Count, Tracks = tracks,
        };
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm());
        var view = new AlbumDetailView { DataContext = vm };
        var win = new Window
        {
            Width = 1280, Height = 900, Content = view,
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
        };
        // App.SetAccent("#8E1B3A") writes the row fill as the raw accent.
        win.Resources["NowPlayingRowBrush"] = new SolidColorBrush(Wine);
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var row = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("row-body"));
        ((Avalonia.Controls.IPseudoClasses)row.Classes).Set(":pointerover", true);
        Sample(row);
        // The resolved brush, not a live read: the headless clock can leave the rest→hover
        // tween short of its end.
        var hover = ((ISolidColorBrush)view.FindResource(Avalonia.Styling.ThemeVariant.Dark, "TrackListHoverBrush")!).Color;

        // Click → play: the hovered row becomes the now-playing row.
        tracks[0].IsNowPlaying = true;
        var toPlaying = Sample(row);
        _output.WriteLine($"hover {hover} -> playing: {string.Join(" ", toPlaying)}");
        Assert.Contains("now-playing", row.Classes);
        Assert.Equal(new[] { Wine }, toPlaying);

        // Next track: this row stops playing and must not fade back out through pink either.
        tracks[0].IsNowPlaying = false;
        var fromPlaying = Sample(row);
        _output.WriteLine($"playing -> hover: {string.Join(" ", fromPlaying)}");
        Assert.Equal(new[] { hover }, fromPlaying);

        win.Close();
    }
}
