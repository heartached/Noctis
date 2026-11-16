using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
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
/// GitHub #242: with Settings › "Wider spacing between albums" on, Home's album rows (Albums,
/// New in Library) must still show five covers per row, as the Albums grid does.
/// </summary>
public class HomeAlbumRowColumnsTests
{
    private readonly ITestOutputHelper _o;
    public HomeAlbumRowColumnsTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int n = 4)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static int FirstRowCount(ItemsControl row)
    {
        var tiles = row.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("album-tile")).ToList();
        if (tiles.Count == 0) return 0;
        var ys = tiles.Select(t => t.TranslatePoint(default, row)!.Value.Y).ToList();
        var top = ys.Min();
        return ys.Count(y => Math.Abs(y - top) < 0.5);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HomeAlbumRows_ShowFivePerRow_AtEveryWidth(bool wide)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        for (var i = 0; i < 12; i++)
        {
            var id = Guid.NewGuid();
            var t = new Track { Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Artist", AlbumArtist = "Artist", Album = $"Album {i}", AlbumId = id, Duration = TimeSpan.FromSeconds(176), PlayCount = 3, FilePath = $"C:\\m\\{i}.mp3", DateAdded = DateTime.UtcNow.AddDays(-i) };
            lib.TrackList.Add(t);
            ((List<Album>)lib.Albums).Add(new Album { Id = id, Name = t.Album, Artist = t.Artist, Year = 2017, Tracks = new List<Track> { t } });
            player.History.Add(t);
        }
        var settings = new SettingsViewModel(persistence, lib, new NoOpPlayHistory());
        settings.AlbumGridWideSpacing = wide;
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib), settings: settings);
        await vm.RefreshAsync();
        var view = new HomeView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 3200, Content = view };
        win.Show(); Pump();

        var rows = new[] { view.FindControl<ItemsControl>("AlbumsRow")!, view.FindControl<ItemsControl>("NewInLibraryRow")! };
        var failures = new List<string>();
        for (var w = 900; w <= 2200; w += 37)
        {
            win.Width = w;
            Pump(6);
            foreach (var row in rows)
            {
                if (!row.IsEffectivelyVisible) continue;
                var n = FirstRowCount(row);
                if (n != 5) failures.Add($"{row.Name}@{w}: {n} (tile {vm.AlbumTileSize:0.##}, row {row.Bounds.Width:0.##})");
            }
        }
        _o.WriteLine($"wide={wide}: " + (failures.Count == 0 ? "all five" : string.Join("; ", failures)));
        Assert.Empty(failures);
        win.Close();
    }
}
