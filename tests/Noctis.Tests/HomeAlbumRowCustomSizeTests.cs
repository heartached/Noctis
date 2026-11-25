using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 11-23 feedback: at 2560x1440 with 125% Windows scaling and Automatic cover size off, Home
/// showed 4 albums per row and the cover-size setting jumped from 4 to 6 per row. The row
/// count must be the computed column count for every cover size. Not reproduced so far: at
/// 125% (headless render scaling), with and without wide spacing and Interface Size, every
/// cover size shows exactly the computed columns; this pins it.
/// </summary>
public class HomeAlbumRowCustomSizeTests
{
    private readonly ITestOutputHelper _o;
    public HomeAlbumRowCustomSizeTests(ITestOutputHelper o) => _o = o;

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static void Pump(int n = 6)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private static int FirstRowCount(ItemsControl row)
    {
        var tiles = row.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();
        if (tiles.Count == 0) return 0;
        var ys = tiles.Select(t => t.TranslatePoint(default, row)!.Value.Y).ToList();
        var top = ys.Min();
        return ys.Count(y => Math.Abs(y - top) < 0.5);
    }

    /// <summary>The shell around Home: the 76 px sidebar rail margin (MainWindow RootPanel).</summary>
    private const double ShellChrome = 76;

    [AvaloniaTheory]
    [InlineData(false, 1.0)]
    [InlineData(true, 1.0)]
    [InlineData(false, 1.1)]
    [InlineData(true, 1.25)]
    public async Task Custom_cover_size_rows_hold_the_computed_columns_at_125_percent(bool wide, double interfaceSize)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        for (var i = 0; i < 30; i++)
        {
            var id = Guid.NewGuid();
            var t = new Track { Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Artist", AlbumArtist = "Artist", Album = $"Album {i}", AlbumId = id, Duration = TimeSpan.FromSeconds(176), PlayCount = 3, FilePath = $"C:\\m\\{i}.mp3", DateAdded = DateTime.UtcNow.AddDays(-i) };
            lib.TrackList.Add(t);
            ((List<Album>)lib.Albums).Add(new Album { Id = id, Name = t.Album, Artist = t.Artist, Year = 2017, Tracks = new List<Track> { t } });
            player.History.Add(t);
        }
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory())
        {
            AlbumGridWideSpacing = wide,
            AlbumTileSizeAuto = false,
            HomeRecentAlbumsCount = 20,
        };
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib), settings: settings);
        await vm.RefreshAsync();
        var view = new HomeView { DataContext = vm };
        // Settings › Interface Size wraps the window content in a scaling LayoutTransformControl (UiScaleHost).
        Control content = interfaceSize == 1.0
            ? view
            : new LayoutTransformControl { LayoutTransform = new Avalonia.Media.ScaleTransform(interfaceSize, interfaceSize), Child = view };
        var win = new Window { Width = 2560 / 1.25 - ShellChrome, Height = 1300, Content = content };
        win.SetRenderScaling(1.25);
        win.Show();
        Pump();

        var row = view.FindControl<ItemsControl>("AlbumsRow")!;
        var failures = new List<string>();
        // The maximised window, then narrower ones (a window not maximised).
        foreach (var w in new[] { win.Width, 1700, 1500, 1300, 1100 })
        {
            win.Width = w;
            for (var target = 140; target <= 320; target += 5)
            {
                settings.AlbumTileTargetSize = target;
                Pump();
                var n = FirstRowCount(row);
                if (n != vm.AlbumColumns)
                    failures.Add($"w {w} target {target}: {n} shown, {vm.AlbumColumns} computed (tile {vm.AlbumTileSize:0.###}, row {row.Bounds.Width:0.###})");
            }
        }
        _o.WriteLine($"wide={wide} ui={interfaceSize} failures={failures.Count}");
        _o.WriteLine(string.Join("\n", failures));
        win.Close();
        Assert.Empty(failures);
    }
}
