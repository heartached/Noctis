using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Evidence and pins for three Home chart-row bugs reported together:
/// hover fill not showing, the context menu snapping shut after a favorite
/// toggle, and the heart not fading out on un-favorite.
/// </summary>
public class HomeChartRowBehaviourTests
{
    // ── 1. Hover: a Button.Background setter on :pointerover is overridden by the
    //       Fluent template, which paints its own ContentPresenter background. ──

    private static (Button Button, ContentPresenter Presenter) MountButton(string selector, IBrush brush)
    {
        var button = new Button { Content = "row" };
        button.Classes.Add("row");
        var window = new Window { Content = new Panel { Children = { button } } };
        window.Styles.Add(new Style(x => x.OfType<Button>().Class("row").Class(":pointerover"))
        {
            Setters = { new Setter(Button.BackgroundProperty, brush) },
        });
        if (selector.Contains("/template/"))
        {
            window.Styles.Add(new Style(x => x.OfType<Button>().Class("row").Class(":pointerover")
                    .Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
            {
                Setters = { new Setter(ContentPresenter.BackgroundProperty, brush) },
            });
        }
        window.Show();
        var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        return (button, presenter);
    }

    [AvaloniaFact]
    public void Hover_ButtonBackgroundSetter_IsMaskedByFluentTemplate()
    {
        var red = Brushes.Red;
        var (button, presenter) = MountButton("Button.row:pointerover", red);

        ((IPseudoClasses)button.Classes).Set(":pointerover", true);

        Assert.NotSame(red, presenter.Background);
    }

    [AvaloniaFact]
    public void Hover_TemplatePresenterSetter_Applies()
    {
        var red = Brushes.Red;
        var (button, presenter) = MountButton("Button.row:pointerover /template/ ContentPresenter#PART_ContentPresenter", red);

        ((IPseudoClasses)button.Classes).Set(":pointerover", true);

        Assert.Same(red, presenter.Background);
    }

    // ── 2. Context menu: Avalonia closes a ContextMenu when the control it was opened
    //       on leaves the visual tree. A Home refresh that rebuilds the chart rows
    //       therefore shuts any menu that is open on one of them. ──

    [AvaloniaFact]
    public void ContextMenu_ClosesWhenOwnerLeavesTree()
    {
        var button = new Button { Content = "row" };
        var panel = new Panel { Children = { button } };
        var window = new Window { Content = panel };
        window.Show();
        var menu = new ContextMenu { Items = { new MenuItem { Header = "x" } } };
        button.ContextMenu = menu;

        menu.Open(button);
        Assert.True(menu.IsOpen);

        panel.Children.Remove(button);

        Assert.False(menu.IsOpen);
    }

    private static Track T(string title, int plays) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        PlayCount = plays,
    };

    [AvaloniaFact]
    public async Task Refresh_WithUnchangedTopSongs_DoesNotRebuildRows()
    {
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(new[] { T("a", 5), T("b", 4), T("c", 3) });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));

        await vm.RefreshAsync();
        var rowsBefore = vm.TopSongRows.ToList();
        var events = new List<NotifyCollectionChangedAction>();
        vm.TopSongRows.CollectionChanged += (_, e) => events.Add(e.Action);

        // A favorite toggle dirties Home and triggers exactly this: a refresh with the
        // same top tracks in the same order.
        vm.MarkDirty();
        await vm.RefreshAsync();

        Assert.Empty(events);
        Assert.Equal(rowsBefore, vm.TopSongRows);
    }

    [AvaloniaFact]
    public async Task Refresh_WithChangedTopSongs_RebuildsRows()
    {
        var lib = new FakeLibraryService();
        var a = T("a", 5);
        var b = T("b", 4);
        lib.TrackList.AddRange(new[] { a, b });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));

        await vm.RefreshAsync();
        b.PlayCount = 9;
        vm.MarkDirty();
        await vm.RefreshAsync();

        Assert.Equal(new[] { "b", "a" }, vm.TopSongRows.Select(r => r.Track.Title));
        Assert.Equal(new[] { 1, 2 }, vm.TopSongRows.Select(r => r.Rank));
    }

    // Discord (Andre, 10-05): a counted play bumps the row's "N plays" live (bound per
    // track) but never re-ranked Most Played — a counted play raises no LibraryUpdated,
    // so Home stayed clean and showed 47 plays at #5 under 46 at #4.
    [AvaloniaFact]
    public async Task CountedPlay_ReRanksMostPlayed()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"noctis-home-rank-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var lib = new FakeLibraryService();
            var a = T("a", 5);
            var b = T("b", 5);
            b.FilePath = Path.Combine(dir, "b.mp3");
            File.WriteAllBytes(b.FilePath, new byte[] { 0 });
            b.Duration = TimeSpan.FromMinutes(3);
            lib.TrackList.AddRange(new[] { a, b });
            var persistence = new TestPersistenceService();
            var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
            var vm = new HomeViewModel(player, lib, new SidebarViewModel(persistence, lib));

            await vm.RefreshAsync();
            Assert.Equal(new[] { "a", "b" }, vm.TopSongRows.Select(r => r.Track.Title));

            player.ReplaceQueueAndPlay(new[] { b }, 0);
            // The re-rank sorts off the UI thread (like RefreshAsync); pump until it lands.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            do
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                if (vm.TopSongRows.FirstOrDefault()?.Track == b) break;
                await Task.Delay(10);
            } while (DateTime.UtcNow < deadline);

            Assert.Equal(6, b.PlayCount);
            Assert.Equal(new[] { "b", "a" }, vm.TopSongRows.Select(r => r.Track.Title));
            Assert.Equal(new[] { 1, 2 }, vm.TopSongRows.Select(r => r.Rank));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
