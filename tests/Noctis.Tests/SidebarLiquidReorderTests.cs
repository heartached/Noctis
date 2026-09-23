using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Sidebar playlist reorder (09-22): an in-app pointer drag with the queue's liquid
/// motion — the card lifts, the rows between source and target slide apart, and the
/// move commits when the card lands.
/// </summary>
public class SidebarLiquidReorderTests
{
    private sealed class PlaylistPersistence : TestPersistenceService
    {
        public List<Playlist> Saved { get; private set; } = new();
        public List<Playlist> Initial { get; } = new()
        {
            new Playlist { Id = Guid.NewGuid(), Name = "A" },
            new Playlist { Id = Guid.NewGuid(), Name = "B" },
            new Playlist { Id = Guid.NewGuid(), Name = "C" },
        };
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(Initial.ToList());
        public override Task SavePlaylistsAsync(List<Playlist> playlists)
        {
            Saved = playlists.ToList();
            return Task.CompletedTask;
        }
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task DraggingAPlaylistDown_OpensAGap_ThenCommitsTheMove()
    {
        EnsureAppStyles();
        var persistence = new PlaylistPersistence();
        var vm = new SidebarViewModel(persistence, new FakeLibraryService()) { IsExpanded = true };
        await vm.LoadPlaylistsAsync();
        var view = new SidebarView { DataContext = vm };
        var win = new Window { Width = 260, Height = 900, Content = view };
        win.Show();
        Pump(4);

        var list = view.FindControl<ListBox>("PlaylistList")!;
        var rows = Enumerable.Range(0, 3).Select(i => (ListBoxItem)list.ContainerFromIndex(i)!).ToArray();
        Point Centre(Control c) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), win)!.Value;

        var start = Centre(rows[0]);
        var end = Centre(rows[2]);
        win.MouseDown(start, MouseButton.Left);
        for (var k = 1; k <= 8; k++)
        {
            win.MouseMove(new Point(start.X, start.Y + (end.Y - start.Y) * k / 8), RawInputModifiers.LeftMouseButton);
            Pump(2);
        }
        Pump(20);

        // Mid-drag: the card is up and the rows between source and target moved up a slot.
        var card = view.FindControl<Border>("PlaylistDragCard")!;
        Assert.True(card.IsVisible, "drag card should be showing");
        Assert.True(rows[1].RenderTransform is TranslateTransform { Y: < -10 }, $"row B should slide up, got {rows[1].RenderTransform}");
        Assert.True(rows[2].RenderTransform is TranslateTransform { Y: < -10 }, "row C should slide up");
        Assert.Equal(0, rows[0].Opacity); // the dragged row's slot is empty

        win.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        Pump(40);

        Assert.Equal(new[] { "B", "C", "A" }, vm.Playlists.Select(p => p.Name));
        Assert.Equal(new[] { "B", "C", "A" }, persistence.Saved.Select(p => p.Name));
        foreach (var c in list.GetRealizedContainers())
        {
            Assert.Equal(1, c.Opacity);
            Assert.False(c.RenderTransform is TranslateTransform { Y: not 0 }, "rows snap home after the drop");
        }
    }
}
