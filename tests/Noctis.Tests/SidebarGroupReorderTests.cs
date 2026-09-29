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
        public List<Playlist> Initial { get; init; } = new()
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

    private static async Task<Sidebar> ShowSidebarAsync(PlaylistPersistence? persistence = null)
    {
        EnsureAppStyles();
        persistence ??= new PlaylistPersistence();
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
        Assert.Contains("A1", Labels(s.Vm)); // the rows fold shut before they are removed
        Assert.False(s.Vm.SidebarRows[0].IsExpanded); // while the chevron already turns
        Pump(30);
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
    public async Task ClickingAFolderHeader_FoldsItsRowsShutAndOpen_InsteadOfSnapping()
    {
        var s = await ShowSidebarAsync();
        var at = Centre(Row(s.List, 0), s.Win);
        var a1 = Row(s.List, 1);
        var full = a1.Bounds.Height;
        Assert.True(full > 20);

        // Closing: the row shrinks over several frames, then is removed.
        s.Win.MouseDown(at, MouseButton.Left);
        s.Win.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
        Pump(6);
        var mid = a1.Bounds.Height;
        Assert.InRange(mid, 0.5, full - 0.5);
        Assert.InRange(a1.Presenter!.Opacity, 0, 0.999);
        Pump(30);
        Assert.DoesNotContain("A1", Labels(s.Vm));

        // Opening: the inserted row starts shut and grows back to full height.
        s.Win.MouseDown(at, MouseButton.Left);
        s.Win.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
        Pump(6);
        var opening = Row(s.List, 1);
        Assert.InRange(opening.Bounds.Height, 0.5, full - 0.5);
        Pump(40);
        Assert.Equal(full, Row(s.List, 1).Bounds.Height, 1);
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

    [AvaloniaFact]
    public async Task DraggingTheOnlyFolder_WithNothingButPinnedPlaylists_StartsNoDrag_AndStaysAClick()
    {
        // Discord Luwi (1.5.5): with nowhere to move it, the card lifted, left an empty gap at
        // its slot and floated over the other playlists. The pinned ones are no slot for it.
        var s = await ShowSidebarAsync(new PlaylistPersistence
        {
            Initial = new()
            {
                new Playlist { Id = Guid.NewGuid(), Name = "Pinned", IsPinned = true },
                new Playlist { Id = Guid.NewGuid(), Name = "Pinned 2", IsPinned = true },
                new Playlist { Id = Guid.NewGuid(), Name = "A1", Folder = "Alpha" },
                new Playlist { Id = Guid.NewGuid(), Name = "A2", Folder = "Alpha" },
            },
        });
        Assert.Equal(new[] { "Pinned", "Pinned 2", "Alpha", "A1", "A2" }, Labels(s.Vm));
        var header = Row(s.List, 2);
        var pitch = header.Bounds.Height + header.Margin.Top + header.Margin.Bottom;
        var card = s.View.FindControl<Border>("PlaylistDragCard")!;
        var start = Centre(header, s.Win);
        var end = new Point(start.X, start.Y - 2 * pitch);
        DragTo(s.Win, start, end);

        Assert.False(card.IsVisible, "a lone folder has nowhere to go");
        foreach (var c in s.List.GetRealizedContainers())
        {
            Assert.Equal(1, c.Opacity); // no hole where the folder was
            Assert.Equal(0, OffsetY(c)); // and no gap opening in the pinned ones
        }
        s.Win.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        Pump(40);
        // The press moved like a drag, so its release is no click either: the folder stays open.
        Assert.Equal(new[] { "Pinned", "Pinned 2", "Alpha", "A1", "A2" }, Labels(s.Vm));

        // A plain click still folds, and the next one unfolds.
        s.Win.MouseDown(start, MouseButton.Left);
        s.Win.MouseUp(start, MouseButton.Left, RawInputModifiers.None);
        Pump(30); // the rows fold shut before they are removed
        Assert.Equal(new[] { "Pinned", "Pinned 2", "Alpha" }, Labels(s.Vm));
        s.Win.MouseDown(start, MouseButton.Left);
        s.Win.MouseUp(start, MouseButton.Left, RawInputModifiers.None);
        Pump(4);
        Assert.Equal(new[] { "Pinned", "Pinned 2", "Alpha", "A1", "A2" }, Labels(s.Vm));

        // A hand that wobbles under the drag threshold is still a click.
        var wobble = new Point(start.X + 3, start.Y + 3);
        s.Win.MouseDown(start, MouseButton.Left);
        s.Win.MouseMove(wobble, RawInputModifiers.LeftMouseButton);
        Pump(2);
        s.Win.MouseUp(wobble, MouseButton.Left, RawInputModifiers.None);
        Pump(30);
        Assert.Equal(new[] { "Pinned", "Pinned 2", "Alpha" }, Labels(s.Vm));
        Assert.False(card.IsVisible);
        Assert.Empty(s.Persistence.Saved);
        Assert.Null(s.Vm.SelectedNavItem);
    }

    [AvaloniaFact]
    public async Task DraggingTheOnlyFolder_MovesItAmongTheLoosePlaylists_NeverIntoThePinnedOnes()
    {
        // Owner (09-27): "Just like how I can reorder the playlists I should be able to reorder
        // folders." One folder above two loose playlists used to be stuck on top.
        var s = await ShowSidebarAsync(new PlaylistPersistence
        {
            Initial = new()
            {
                new Playlist { Id = Guid.NewGuid(), Name = "Pinned", IsPinned = true },
                new Playlist { Id = Guid.NewGuid(), Name = "A1", Folder = "Alpha" },
                new Playlist { Id = Guid.NewGuid(), Name = "A2", Folder = "Alpha" },
                new Playlist { Id = Guid.NewGuid(), Name = "L1" },
                new Playlist { Id = Guid.NewGuid(), Name = "L2" },
            },
        });
        Assert.Equal(new[] { "Pinned", "Alpha", "A1", "A2", "L1", "L2" }, Labels(s.Vm));
        var header = Row(s.List, 1);
        var pitch = header.Bounds.Height + header.Margin.Top + header.Margin.Bottom;
        var host = s.View.FindControl<Panel>("PlaylistListHost")!;
        var headerTop = header.TranslatePoint(new Point(0, 0), host)!.Value.Y;
        var card = s.View.FindControl<Border>("PlaylistDragCard")!;
        double CardY() => ((TransformGroup)card.RenderTransform!).Children.OfType<TranslateTransform>().Single().Y;

        // Up past the pinned playlist: the card stays on its own slot, nothing opens above.
        // (One quick stroke up and straight back down: with the card held perfectly still the
        // headless frame clock stopped delivering frames here, so the stroke never rests.)
        var start = Centre(header, s.Win);
        s.Win.MouseDown(start, MouseButton.Left);
        for (var k = 1; k <= 2; k++)
        {
            s.Win.MouseMove(new Point(start.X, start.Y - pitch * k), RawInputModifiers.LeftMouseButton);
            Pump(1);
        }
        Assert.True(card.IsVisible, "with loose playlists around, a lone folder lifts");
        Assert.Equal(headerTop, CardY(), 1.0);
        Assert.Equal(0, OffsetY(Row(s.List, 0))); // Pinned stays put

        // Down one row: the whole open block (3 rows) swaps with L1.
        var down = new Point(start.X, start.Y + pitch);
        for (var k = 1; k <= 8; k++)
        {
            s.Win.MouseMove(new Point(start.X, start.Y + pitch * k / 8), RawInputModifiers.LeftMouseButton);
            Pump(2);
        }
        Pump(20);
        Assert.Equal(headerTop + pitch, CardY(), 1.0);
        Assert.Equal(3 * pitch - header.Margin.Top - header.Margin.Bottom, card.Height, 0.5);
        Assert.Equal(-3 * pitch, OffsetY(Row(s.List, 4)), 1.0); // L1 slides up by the block
        Assert.Equal(0, OffsetY(Row(s.List, 5)));               // L2 stays
        Assert.Equal(0, OffsetY(Row(s.List, 0)));               // Pinned stays

        s.Win.MouseUp(down, MouseButton.Left, RawInputModifiers.None);
        Pump(40);
        Assert.Equal(new[] { "Pinned", "L1", "Alpha", "A1", "A2", "L2" }, Labels(s.Vm));
        Assert.True(s.Vm.SidebarRows.Single(r => r.Label == "Alpha").IsExpanded, "a moved press never folds it");
        Assert.True(s.Persistence.Saved.Single(p => p.Name == "Pinned").IsPinned);
        Assert.Null(s.Vm.SelectedNavItem);
    }

    [AvaloniaFact]
    public async Task DraggingAFolderBelowTheLoosePlaylists_LandsAfterThem()
    {
        var s = await ShowSidebarAsync();
        var header = Row(s.List, 0);
        var pitch = header.Bounds.Height + header.Margin.Top + header.Margin.Bottom;
        var host = s.View.FindControl<Panel>("PlaylistListHost")!;
        var headerTop = header.TranslatePoint(new Point(0, 0), host)!.Value.Y;

        // Far below the last row: the card stops where the Alpha block (3 rows) would sit
        // after Loose, the last row, like a playlist dragged past the end.
        var start = Centre(header, s.Win);
        var end = new Point(start.X, start.Y + 10 * pitch);
        DragTo(s.Win, start, end);

        var card = s.View.FindControl<Border>("PlaylistDragCard")!;
        Assert.True(card.IsVisible, "drag card should be showing");
        var cardY = ((TransformGroup)card.RenderTransform!).Children.OfType<TranslateTransform>().Single().Y;
        Assert.Equal(headerTop + 5 * pitch, cardY, 1.0);
        for (var i = 3; i < 8; i++)
            Assert.Equal(-3 * pitch, OffsetY(Row(s.List, i)), 1.0); // Beta, B1, Gamma, G1, Loose

        s.Win.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        Pump(40);
        Assert.Equal(new[] { "Beta", "B1", "Gamma", "G1", "Loose", "Alpha", "A1", "A2" }, Labels(s.Vm));
        var saved = s.Persistence.Saved.ToDictionary(p => p.Name);
        Assert.Equal(new[] { 1, 2, 3, 4, 4 }, new[] { "B1", "G1", "Loose", "A1", "A2" }.Select(n => saved[n].SidebarOrder));
        Assert.Equal(new[] { 1, 2, 3 }, new[] { "B1", "G1", "A1" }.Select(n => saved[n].FolderOrder));
        // Only positions changed: the saved playlist order itself is untouched.
        Assert.Equal(new[] { "A1", "A2", "B1", "G1", "Loose" }, s.Persistence.Saved.Select(p => p.Name));
    }

    [AvaloniaFact]
    public async Task DraggingALoosePlaylistToTheTopEdgeOfAFolder_LandsAboveIt_NotInside()
    {
        var s = await ShowSidebarAsync();
        var loose = Row(s.List, 7);
        var beta = Row(s.List, 3);
        var pitch = beta.Bounds.Height + beta.Margin.Top + beta.Margin.Bottom;

        var start = Centre(loose, s.Win);
        var betaCentre = Centre(beta, s.Win);
        var end = new Point(start.X, betaCentre.Y - 0.4 * pitch);
        DragTo(s.Win, start, end);

        Assert.DoesNotContain("drop-target", beta.Classes); // not filed into Beta
        Assert.Equal(0, OffsetY(Row(s.List, 2)));             // A2 stays
        for (var i = 3; i < 7; i++)
            Assert.Equal(pitch, OffsetY(Row(s.List, i)), 1.0); // Beta's block and Gamma's open the gap

        s.Win.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        Pump(40);
        Assert.Equal(new[] { "Alpha", "A1", "A2", "Loose", "Beta", "B1", "Gamma", "G1" }, Labels(s.Vm));
        var saved = s.Persistence.Saved.Single(p => p.Name == "Loose");
        Assert.Equal(string.Empty, saved.Folder);
        Assert.Equal(2, saved.SidebarOrder);
    }

    [AvaloniaFact]
    public async Task DraggingAPlaylistOffTheBottomOfAnOpenFolder_LandsBelowIt_OutsideTheFolder()
    {
        var s = await ShowSidebarAsync();
        var a1 = Row(s.List, 1);
        var a2 = Row(s.List, 2);
        var pitch = a2.Bounds.Height + a2.Margin.Top + a2.Margin.Bottom;

        var start = Centre(a1, s.Win);
        var end = new Point(start.X, Centre(a2, s.Win).Y + 0.4 * pitch);
        DragTo(s.Win, start, end);
        Assert.Equal(-pitch, OffsetY(a2), 1.0); // A2 slides up; the gap sits under the folder
        Assert.Equal(0, OffsetY(Row(s.List, 3)));

        s.Win.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        Pump(40);
        Assert.Equal(new[] { "Alpha", "A2", "A1", "Beta", "B1", "Gamma", "G1", "Loose" }, Labels(s.Vm));
        Assert.False(s.Vm.SidebarRows.Single(r => r.Label == "A1").IsInFolder);
        Assert.Equal(string.Empty, s.Persistence.Saved.Single(p => p.Name == "A1").Folder);
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

    [Fact]
    public async Task ASidebarFromBeforeTheSharedOrder_LooksExactlyTheSame()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            // Saved by 1.5.5: folders dragged into order (folderOrder), one never dragged,
            // loose playlists before folder playlists in the file, and a loose playlist still
            // carrying the folderOrder of a folder it was dissolved out of.
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "playlists.json"), """
                [
                  { "id": "00000000-0000-0000-0000-000000000001", "name": "L1" },
                  { "id": "00000000-0000-0000-0000-000000000002", "name": "G1", "folder": "Gamma", "folderOrder": 2 },
                  { "id": "00000000-0000-0000-0000-000000000003", "name": "L2", "folderOrder": 5 },
                  { "id": "00000000-0000-0000-0000-000000000004", "name": "A1", "folder": "Alpha", "folderOrder": 1 },
                  { "id": "00000000-0000-0000-0000-000000000005", "name": "B1", "folder": "Beta" },
                  { "id": "00000000-0000-0000-0000-000000000006", "name": "P", "isPinned": true, "folderOrder": 1 }
                ]
                """, TestContext.Current.CancellationToken);

            var vm = new SidebarViewModel(new PersistenceService(root), new FakeLibraryService());
            await vm.LoadPlaylistsAsync();
            Assert.Equal(new[] { "P", "Alpha", "A1", "Gamma", "G1", "Beta", "B1", "L1", "L2" }, Labels(vm));

            // The first move anywhere writes the order as shown, so everything else stays put.
            Guid Id(string name) => vm.Playlists.Single(p => p.Name == name).Id;
            await vm.MovePlaylistAsync(Id("L2"), vm.SidebarRows.Single(r => r.Label == "L1"), placeAfter: false);
            Assert.Equal(new[] { "P", "Alpha", "A1", "Gamma", "G1", "Beta", "B1", "L2", "L1" }, Labels(vm));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public async Task TheSharedOrder_IsSavedWithThePlaylists_AndSurvivesARestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(Path.Combine(root, "playlists.json"), """
                [
                  { "id": "00000000-0000-0000-0000-000000000001", "name": "A1", "folder": "Alpha" },
                  { "id": "00000000-0000-0000-0000-000000000002", "name": "B1", "folder": "Beta" },
                  { "id": "00000000-0000-0000-0000-000000000003", "name": "L1" },
                  { "id": "00000000-0000-0000-0000-000000000004", "name": "L2" },
                  { "id": "00000000-0000-0000-0000-000000000005", "name": "P", "isPinned": true }
                ]
                """, TestContext.Current.CancellationToken);

            var vm = new SidebarViewModel(new PersistenceService(root), new FakeLibraryService());
            await vm.LoadPlaylistsAsync();
            PlaylistNavItem Row(string label) => vm.SidebarRows.Single(r => r.Label == label);
            Guid Id(string name) => vm.Playlists.Single(p => p.Name == name).Id;

            await vm.MoveFolderAsync("Alpha", Row("L2"), placeAfter: true);          // folder below the loose ones
            await vm.MovePlaylistNextToFolderAsync(Id("L2"), Row("Beta"), placeAfter: false); // loose above a folder
            await vm.MoveFolderAsync("Beta", Row("L1"), placeAfter: true);           // folder between loose ones
            var expected = new[] { "P", "L2", "L1", "Beta", "B1", "Alpha", "A1" };
            Assert.Equal(expected, Labels(vm));

            // Filing into / out of a folder keeps the rest in place.
            await vm.MovePlaylistAsync(Id("L1"), Row("Alpha"), placeAfter: false);
            Assert.Equal(new[] { "P", "L2", "Beta", "B1", "Alpha", "A1", "L1" }, Labels(vm));
            await vm.MovePlaylistNextToFolderAsync(Id("L1"), Row("Beta"), placeAfter: true);
            Assert.Equal(new[] { "P", "L2", "Beta", "B1", "L1", "Alpha", "A1" }, Labels(vm));

            // A new playlist still shows up at the bottom.
            await vm.CreatePlaylistFromTracksAsync("New", Array.Empty<Track>());
            var final = new[] { "P", "L2", "Beta", "B1", "L1", "Alpha", "A1", "New" };
            Assert.Equal(final, Labels(vm));
            Assert.Contains("\"sidebarOrder\"", await File.ReadAllTextAsync(Path.Combine(root, "playlists.json"), TestContext.Current.CancellationToken));

            var restarted = new SidebarViewModel(new PersistenceService(root), new FakeLibraryService());
            await restarted.LoadPlaylistsAsync();
            Assert.Equal(final, Labels(restarted));
            Assert.True(restarted.Playlists.Single(p => p.Name == "P").IsPinned);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* temp dir */ }
        }
    }

    [Fact]
    public async Task FolderAndPinnedMoves_KeepThePinnedSection()
    {
        var persistence = new PlaylistPersistence
        {
            Initial = new()
            {
                new Playlist { Id = Guid.NewGuid(), Name = "P1", IsPinned = true },
                new Playlist { Id = Guid.NewGuid(), Name = "A1", Folder = "Alpha" },
                new Playlist { Id = Guid.NewGuid(), Name = "L1" },
            },
        };
        var vm = new SidebarViewModel(persistence, new FakeLibraryService());
        await vm.LoadPlaylistsAsync();
        PlaylistNavItem Row(string label) => vm.SidebarRows.Single(r => r.Label == label);
        Guid Id(string name) => vm.Playlists.Single(p => p.Name == name).Id;

        await vm.MoveFolderAsync("Alpha", Row("P1"), placeAfter: false); // not into the pinned ones
        Assert.Equal(new[] { "P1", "Alpha", "A1", "L1" }, Labels(vm));
        Assert.Empty(persistence.Saved);

        // A playlist dropped next to a pinned one still becomes pinned, as before.
        await vm.MovePlaylistAsync(Id("L1"), Row("P1"), placeAfter: true);
        Assert.Equal(new[] { "P1", "L1", "Alpha", "A1" }, Labels(vm));
        Assert.True(persistence.Saved.Single(p => p.Name == "L1").IsPinned);
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
