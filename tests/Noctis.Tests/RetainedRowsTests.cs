using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
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
/// 11-14 "lag when I'm scrolling": Avalonia's VirtualizingStackPanel clears a recycled row
/// (its cleared Content drops the item template's controls) and takes it out of the tree
/// (dropping its styles), so every row scrolled in was built and styled from scratch: on the
/// Songs page ~1.3 MB and ~51 controls a 40 px wheel step (PageScrollBench). The lists use
/// <see cref="RetainingVirtualizingStackPanel"/>, which parks rows in the tree and hands them
/// the next item. These pin that rows are reused, and that a reused row shows its own item,
/// stripe and selection.
/// </summary>
public class RetainedRowsTests
{
    private readonly ITestOutputHelper _o;
    public RetainedRowsTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int n = 3)
    {
        for (var i = 0; i < n; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Pump(1);
            await Task.Delay(5);
        }
        Pump();
    }

    private static (LibrarySongsViewModel Vm, LibrarySongsView View, Window Window) MountSongs(int count)
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < count; i++)
            lib.TrackList.Add(new Track
            {
                Id = Guid.NewGuid(), Title = $"Song {i:0000}", Artist = $"Artist {i % 37}", Album = $"Album {i / 12}",
                FilePath = TestPaths.Primary("t", $"{i:0000}.mp3"), Duration = TimeSpan.FromMinutes(3),
                Rating = i % 3 == 0 ? 4 : 0, IsFavorite = i % 5 == 0, TrackNumber = i % 12 + 1,
            });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence);
        var view = new LibrarySongsView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        vm.IsActive = true;
        return (vm, view, window);
    }

    private static ScrollViewer Scroller(ListBox list) => list.FindDescendantOfType<ScrollViewer>()!;

    [AvaloniaFact]
    public async Task Songs_UsesTheRetainingPanel()
    {
        var (vm, view, window) = MountSongs(50);
        await PumpUntil(() => vm.FilteredTracks.Count == 50);
        Assert.IsType<RetainingVirtualizingStackPanel>(view.TrackList.ItemsPanelRoot);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Songs_ScrolledRows_ShowTheirOwnSongStripeAndSelection()
    {
        var (vm, view, window) = MountSongs(600);
        await PumpUntil(() => vm.FilteredTracks.Count == 600);
        var list = view.TrackList;
        list.Selection.Select(5);
        Pump();
        Assert.True(view.TryFindResource("TrackListStripeBrush", view.ActualThemeVariant, out var stripeObj));
        var stripe = Assert.IsAssignableFrom<IBrush>(stripeObj);
        var sv = Scroller(list);

        foreach (var y in new[] { 0.0, 1200, 1240, 1290, 5000, 5046, 9000, 300, 0, 23 })
        {
            sv.Offset = new Vector(0, y);
            Pump();
            var rows = list.GetRealizedContainers()!.ToList();
            Assert.NotEmpty(rows);
            foreach (var row in rows)
            {
                Assert.True(row.IsVisible, $"a parked row was reported realized at offset {y}");
                var index = list.IndexFromContainer(row);
                var track = vm.FilteredTracks[index];
                var item = Assert.IsType<ListBoxItem>(row);
                Assert.Same(track, item.Content);
                Assert.Same(track, item.DataContext);
                var body = item.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("row-body"));
                Assert.Same(track, body.DataContext);
                var title = body.GetVisualDescendants().OfType<HighlightTextBlock>().First(t => t.Classes.Contains("one-line-explicit-title"));
                Assert.Equal(track.TitleDisplay, title.DisplayText);
                var rating = body.GetVisualDescendants().OfType<RatingStars>().First();
                Assert.Equal(track.Rating, rating.Value);
                Assert.Equal(track.Rating > 0, rating.Classes.Contains("rated"));
                // :nth-child(2n) is 1-based: the second, fourth ... rows carry the stripe.
                if (index % 2 == 1)
                    Assert.Equal(stripe, body.Background);
                else
                    Assert.NotEqual(stripe, body.Background);
                Assert.Equal(index == 5, item.IsSelected);
            }
        }

        Assert.Equal(new[] { 5 }, list.Selection.SelectedIndexes.ToArray());
        window.Close();
    }

    /// <summary>A parked row whose next song is rated (or not) snaps to its stars' look, as a
    /// new row would, instead of fading across from the last song's (80 ms Opacity transition).</summary>
    [AvaloniaFact]
    public async Task Songs_ReusedRow_DoesNotFadeFromTheLastSongsLook()
    {
        var (vm, view, window) = MountSongs(600);
        await PumpUntil(() => vm.FilteredTracks.Count == 600);
        var sv = Scroller(view.TrackList);
        foreach (var y in new[] { 400.0, 800, 1600, 3000 })
        {
            sv.Offset = new Vector(0, y);
            Pump();
            foreach (var row in view.TrackList.GetRealizedContainers()!)
            {
                var track = vm.FilteredTracks[view.TrackList.IndexFromContainer(row)];
                var stars = row.GetVisualDescendants().OfType<RatingStars>().First();
                Assert.Equal(track.Rating > 0 ? 0.7 : 0.0, stars.Opacity, 3);
                Assert.NotNull(stars.Transitions); // given back after the rebind
            }
        }
        window.Close();
    }

    [AvaloniaFact]
    public async Task Songs_WheelGlide_ReusesRows_InsteadOfRebuildingThem()
    {
        var (vm, view, window) = MountSongs(3000);
        await PumpUntil(() => vm.FilteredTracks.Count == 3000);
        var list = view.TrackList;
        var sv = Scroller(list);
        sv.Offset = new Vector(0, 40);
        Pump();

        var roots = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var containers = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        long bytes = 0;
        const int frames = 120;
        for (var i = 2; i <= frames + 1; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            sv.Offset = new Vector(0, i * 40.0);
            Pump(1);
            bytes += GC.GetAllocatedBytesForCurrentThread() - before;
            foreach (var row in list.GetVisualDescendants().OfType<ListBoxItem>())
            {
                containers.Add(row);
                if (row.Presenter?.Child is { } child) roots.Add(child);
            }
        }
        var kbPerFrame = bytes / 1024.0 / frames;
        _o.WriteLine($"{containers.Count} containers, {roots.Count} template roots, {kbPerFrame:0} KB/frame");
        // Before (Avalonia's panel): a new template per row scrolled in, ~100 roots here and
        // ~1.3 MB a frame in the app; now every row keeps its template.
        Assert.Equal(containers.Count, roots.Count);
        Assert.True(kbPerFrame < 400, $"{kbPerFrame:0} KB per 40 px wheel step");
        window.Close();
    }
}
