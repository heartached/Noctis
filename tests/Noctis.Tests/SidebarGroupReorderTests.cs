using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Sidebar playlist folders (Discord Luwi, 09-25) reorder with the same liquid pointer drag
/// as playlists: the header lifts into the card with its open playlists, the folders it
/// passes slide over by the whole block, and the order is saved with the playlists
/// (Playlist.FolderOrder). A plain click on a header still folds / unfolds it.
/// </summary>
public class SidebarGroupReorderTests
{
    private sealed class PlaylistPersistence : TestPersistenceService
    {
        public List<Playlist> Saved { get; private set; } = new();
        public List<Playlist> Initial { get; } = new()
        {
            new Playlist { Id = Guid.NewGuid(), Name = "A1", Folder = "Alpha" },
            new Playlist { Id = Guid.NewGuid(), Name = "A2", Folder = "Alpha" },
            new Playlist { Id = Guid.NewGuid(), Name = "B1", Folder = "Beta" },
            new Playlist { Id = Guid.NewGuid(), Name = "G1", Folder = "Gamma" },
            new Playlist { Id = Guid.NewGuid(), Name = "Loose" },
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

    private sealed record Sidebar(SidebarViewModel Vm, PlaylistPersistence Persistence, SidebarView View, Window Win, ListBox List);

    private static async Task<Sidebar> ShowSidebarAsync()
    {
        EnsureAppStyles();
        var persistence = new PlaylistPersistence();
        var vm = new SidebarViewModel(persistence, new FakeLibraryService()) { IsExpanded = true };
        await vm.LoadPlaylistsAsync();
        var view = new SidebarView { DataContext = vm };
        // Tall enough that every playlist row sits inside the window under the nav rows.
        var win = new Window { Width = 260, Height = 1400, Content = view };
        win.Show();
        Pump(4);
        return new Sidebar(vm, persistence, view, win, view.FindControl<ListBox>("PlaylistList")!);
    }

    private static Point Centre(Control c, Visual space)
        => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), space)!.Value;

    private static ListBoxItem Row(ListBox list, int index) => (ListBoxItem)list.ContainerFromIndex(index)!;

    private static double OffsetY(Control row) => row.RenderTransform is TranslateTransform t ? t.Y : 0;

    private static void DragTo(Window win, Point start, Point end)
    {
        win.MouseDown(start, MouseButton.Left);
        for (var k = 1; k <= 8; k++)
        {
            win.MouseMove(new Point(start.X, start.Y + (end.Y - start.Y) * k / 8), RawInputModifiers.LeftMouseButton);
            Pump(2);
        }
        Pump(20);
    }

    private static string[] Labels(SidebarViewModel vm) => vm.SidebarRows.Select(r => r.Label).ToArray();

    [AvaloniaFact]
    public async Task DraggingAnOpenFolderDown_CarriesItsPlaylists_ThenCommitsAndSavesTheOrder()
    {
        var s = await ShowSidebarAsync();
        Assert.Equal(new[] { "Alpha", "A1", "A2", "Beta", "B1", "Gamma", "G1", "Loose" }, Labels(s.Vm));
        var header = Row(s.List, 0);
        var pitch = header.Bounds.Height + header.Margin.Top + header.Margin.Bottom;
        var host = s.View.FindControl<Panel>("PlaylistListHost")!;
        var headerTop = header.TranslatePoint(new Point(0, 0), host)!.Value.Y;

        // Down two rows: the Alpha block (3 rows) lands after Beta (2 rows).
        var start = Centre(header, s.Win);
        DragTo(s.Win, start, new Point(start.X, start.Y + 2 * pitch));

        // Mid-drag, on the real path: the card is up, lifted, three rows tall, following the
        // pointer; the folder AND its playlists left the list (nothing stays behind), and
        // Beta's rows slid up by the whole block, not by one row.
        var card = s.View.FindControl<Border>("PlaylistDragCard")!;
        Assert.True(card.IsVisible, "drag card should be showing");
        var cardTransform = (TransformGroup)card.RenderTransform!;
        Assert.True(cardTransform.Children.OfType<ScaleTransform>().Single().ScaleY > 1.02, "card should be lifted");
        Assert.Equal(headerTop + 2 * pitch, cardTransform.Children.OfType<TranslateTransform>().Single().Y, 1.0);
        Assert.Equal(3 * pitch - header.Margin.Top - header.Margin.Bottom, card.Height, 0.5);
        var cardRows = (StackPanel)s.View.FindControl<ContentControl>("PlaylistDragCardContent")!.Content!;
        Assert.Equal(new[] { "Alpha", "A1", "A2" }, cardRows.Children.Select(c => ((PlaylistNavItem)((ContentControl)c).Content!).Label));
        for (var i = 0; i < 3; i++)
            Assert.Equal(0, Row(s.List, i).Opacity);
        Assert.Equal(-3 * pitch, OffsetY(Row(s.List, 3)), 1.0); // Beta
        Assert.Equal(-3 * pitch, OffsetY(Row(s.List, 4)), 1.0); // B1
        for (var i = 5; i < 8; i++)
            Assert.Equal(0, OffsetY(Row(s.List, i)));             // Gamma, G1, Loose stay
        Assert.Empty(s.Persistence.Saved);                         // nothing committed yet

        s.Win.MouseUp(new Point(start.X, start.Y + 2 * pitch), MouseButton.Left, RawInputModifiers.None);
        Pump(40);

        Assert.Equal(new[] { "Beta", "B1", "Alpha", "A1", "A2", "Gamma", "G1", "Loose" }, Labels(s.Vm));
        Assert.True(s.Vm.SidebarRows.Single(r => r.Label == "Alpha").IsExpanded, "the folder lands still open");
        var saved = s.Persistence.Saved.ToDictionary(p => p.Name);
        Assert.Equal(2, saved["A1"].FolderOrder);
        Assert.Equal(2, saved["A2"].FolderOrder);
        Assert.Equal(1, saved["B1"].FolderOrder);
        Assert.Equal(3, saved["G1"].FolderOrder);
        // Only folder positions changed: the saved playlist order itself is untouched.
        Assert.Equal(new[] { "A1", "A2", "B1", "G1", "Loose" }, s.Persistence.Saved.Select(p => p.Name));
        foreach (var c in s.List.GetRealizedContainers())
        {
            Assert.Equal(1, c.Opacity);
            Assert.False(c.RenderTransform is TranslateTransform { Y: not 0 }, "rows snap home after the drop");
        }
    }

    [AvaloniaFact]
    public async Task DraggingAClosedFolderUp_OpensAOneRowGap_AndLandsInFront()
    {
        var s = await ShowSidebarAsync();
        s.Vm.ToggleFolderExpansion("Gamma");
        Pump(4);
        Assert.Equal(new[] { "Alpha", "A1", "A2", "Beta", "B1", "Gamma", "Loose" }, Labels(s.Vm));
        var gamma = Row(s.List, 5);
        var pitch = gamma.Bounds.Height + gamma.Margin.Top + gamma.Margin.Bottom;

        var start = Centre(gamma, s.Win);
        var end = Centre(Row(s.List, 0), s.Win);
        DragTo(s.Win, start, end);

        var card = s.View.FindControl<Border>("PlaylistDragCard")!;
        Assert.True(card.IsVisible, "drag card should be showing");
        Assert.Equal(gamma.Bounds.Height, card.Height, 0.5);   // one row: same pill as a playlist
        Assert.Equal(0, gamma.Opacity);
        for (var i = 0; i < 5; i++)
            Assert.Equal(pitch, OffsetY(Row(s.List, i)), 1.0); // everything above slides down one row
        Assert.Equal(0, OffsetY(Row(s.List, 6)));              // Loose stays

        s.Win.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        Pump(40);

        Assert.Equal(new[] { "Gamma", "Alpha", "A1", "A2", "Beta", "B1", "Loose" }, Labels(s.Vm));
        Assert.False(s.Vm.SidebarRows[0].IsExpanded, "a closed folder stays closed");
        var saved = s.Persistence.Saved.ToDictionary(p => p.Name);
        Assert.Equal(1, saved["G1"].FolderOrder);
        Assert.Equal(2, saved["A1"].FolderOrder);
        Assert.Equal(3, saved["B1"].FolderOrder);
    }

    [AvaloniaFact]
    public async Task ClickingAFolderHeader_StillTogglesIt_WithoutMovingAnything()
    {
        var s = await ShowSidebarAsync();
        var header = Row(s.List, 0);
        var at = Centre(header, s.Win);

        // A small wobble under the drag threshold is still a click.
        s.Win.MouseDown(at, MouseButton.Left);
        s.Win.MouseMove(new Point(at.X + 2, at.Y + 3), RawInputModifiers.LeftMouseButton);
        Pump(2);
        Assert.Contains("A1", Labels(s.Vm)); // the press alone does not fold it
        s.Win.MouseUp(new Point(at.X + 2, at.Y + 3), MouseButton.Left, RawInputModifiers.None);
        Pump(4);
        Assert.Equal(new[] { "Alpha", "Beta", "B1", "Gamma", "G1", "Loose" }, Labels(s.Vm));
        Assert.False(s.Vm.SidebarRows[0].IsExpanded);

        s.Win.MouseDown(at, MouseButton.Left);
        s.Win.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
        Pump(4);
        Assert.Equal(new[] { "Alpha", "A1", "A2", "Beta", "B1", "Gamma", "G1", "Loose" }, Labels(s.Vm));
        Assert.True(s.Vm.SidebarRows[0].IsExpanded);

        Assert.False(s.View.FindControl<Border>("PlaylistDragCard")!.IsVisible);
        Assert.Empty(s.Persistence.Saved);
        Assert.Null(s.Vm.SelectedNavItem); // the header never became the selection
    }

    [AvaloniaFact]
    public async Task DraggingAPlaylistOntoAFolderHeader_StillFilesItThere()
    {
        var s = await ShowSidebarAsync();
        var loose = Row(s.List, 7);
        var beta = Row(s.List, 3);

        var start = Centre(loose, s.Win);
        var end = Centre(beta, s.Win);
        DragTo(s.Win, start, end);

        Assert.True(beta.Classes.Contains("drop-target"), "the folder header lights up as the target");
        Assert.Equal(0, OffsetY(Row(s.List, 4))); // over a folder the gap closes

        s.Win.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        Pump(40);

        Assert.Equal(new[] { "Alpha", "A1", "A2", "Beta", "B1", "Loose", "Gamma", "G1" }, Labels(s.Vm));
        Assert.Equal("Beta", s.Persistence.Saved.Single(p => p.Name == "Loose").Folder);
        Assert.DoesNotContain("drop-target", beta.Classes);
    }

    // ── Order model (no UI) ──

    private static string[] Headers(SidebarViewModel vm) => vm.SidebarRows.Where(r => r.IsFolder).Select(r => r.Label).ToArray();

    [Fact]
    public async Task FolderOrder_IsSavedWithThePlaylists_AndSurvivesARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            // A playlists.json from before folders could be dragged: no folderOrder at all.
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "playlists.json"), """
                [
                  { "id": "00000000-0000-0000-0000-000000000001", "name": "G1", "folder": "Gamma" },
                  { "id": "00000000-0000-0000-0000-000000000002", "name": "A1", "folder": "Alpha" },
                  { "id": "00000000-0000-0000-0000-000000000003", "name": "B1", "folder": "Beta" }
                ]
                """, TestContext.Current.CancellationToken);

            var vm = new SidebarViewModel(new PersistenceService(root), new FakeLibraryService());
            await vm.LoadPlaylistsAsync();
            Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, Headers(vm)); // never dragged: alphabetical, as before

            await vm.MoveFolderAsync("Alpha", vm.SidebarRows.Single(r => r.Label == "Gamma"), placeAfter: true);
            Assert.Equal(new[] { "Beta", "Gamma", "Alpha" }, Headers(vm));

            var restarted = new SidebarViewModel(new PersistenceService(root), new FakeLibraryService());
            await restarted.LoadPlaylistsAsync();
            Assert.Equal(new[] { "Beta", "Gamma", "Alpha" }, Headers(restarted));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public async Task PlaylistsFiledIntoOrderedFolders_KeepEveryFolderInPlace()
    {
        var persistence = new PlaylistPersistence();
        var vm = new SidebarViewModel(persistence, new FakeLibraryService());
        await vm.LoadPlaylistsAsync();
        await vm.MoveFolderAsync("Gamma", vm.SidebarRows.Single(r => r.Label == "Alpha"), placeAfter: false);
        Assert.Equal(new[] { "Gamma", "Alpha", "Beta" }, Headers(vm));

        // Onto a folder header, and next to a playlist inside a folder: the playlist takes
        // that folder's place instead of bringing its old folder's place along (which would
        // tie the two folders and fall back to alphabetical).
        Guid Id(string name) => vm.Playlists.Single(p => p.Name == name).Id;
        PlaylistNavItem Row(string label) => vm.SidebarRows.Single(r => r.Label == label);
        await vm.MovePlaylistAsync(Id("A1"), Row("Gamma"), placeAfter: false);  // Alpha → Gamma
        await vm.MovePlaylistAsync(Id("Loose"), Row("B1"), placeAfter: true);   // loose → Beta
        await vm.MovePlaylistAsync(Id("B1"), Row("G1"), placeAfter: true);      // Beta → Gamma
        Assert.Equal(new[] { "Gamma", "Alpha", "Beta" }, Headers(vm));

        var saved = persistence.Saved.ToDictionary(p => p.Name);
        Assert.Equal(new[] { 1, 1, 1 }, new[] { saved["G1"], saved["A1"], saved["B1"] }.Select(p => p.FolderOrder));
        Assert.Equal(2, saved["A2"].FolderOrder);
        Assert.Equal(3, saved["Loose"].FolderOrder);
    }

    [Theory]
    // A 3-row block at 1..3 dragged down to start at 5: rows 4..6 move up by the block.
    [InlineData(1, 1, 5, 0)]
    [InlineData(3, 1, 5, 0)]        // the block's own (hidden) rows never move
    [InlineData(4, 1, 5, -150)]
    [InlineData(7, 1, 5, -150)]
    [InlineData(8, 1, 5, 0)]
    // Dragged up to start at 0 from 4: rows 0..3 move down by the block.
    [InlineData(0, 4, 0, 150)]
    [InlineData(3, 4, 0, 150)]
    [InlineData(4, 4, 0, 0)]
    [InlineData(7, 4, 0, 0)]
    public void GapOffset_MovesTheRowsABlockPasses_ByTheBlock(int index, int source, int target, double expected)
        => Assert.Equal(expected, LiquidReorder.GapOffset(index, source, target, 150, count: 3));
}
