using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using ToggleButton = Avalonia.Controls.Primitives.ToggleButton;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord request 11-07 (f7uh): the played songs stay in the Queue page (Settings › Played Songs in
/// Queue, off by default) above the current track, a tap plays one again from its place; and a
/// song's menu can put it just before the playing song (Add Before Current Song).
/// </summary>
public class MobileQueueHistoryTests
{
    private static Track[] Songs(int n) => Enumerable.Range(0, n).Select(i => MobileFixtures.Song($"S{i}")).ToArray();

    private static (MobileFixtures.Rig Rig, Window Window, ShellView View, QueuePage Page, Track[] Songs) OpenQueue(
        bool history, int played = 2, int count = 5, double width = 412, double height = 915)
    {
        var songs = Songs(count);
        var rig = MobileFixtures.MakeRig(songs);
        rig.Shell.ShowQueueHistory = history;
        var window = MobileFixtures.Mount(rig.Shell, out var view, width, height);
        rig.Shell.Player.PlayTracks(songs, 0, "Alpha");
        for (var i = 0; i < played; i++) rig.Shell.Player.NextCommand.Execute(null);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.ToggleQueueCommand.Execute(null);
        window.UpdateLayout();
        var page = MobileFixtures.Find<QueuePage>(view);
        page.CompleteSlide();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (rig, window, view, page, songs);
    }

    private static void Tap(Window window, Control control)
    {
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(centre, MouseButton.Left, RawInputModifiers.None);
        window.UpdateLayout();
    }

    private static Control[] HistoryRows(QueuePage page)
    {
        var list = MobileFixtures.Named<ItemsControl>(page, "HistoryList");
        return Enumerable.Range(0, list.ItemCount).Select(i => list.ContainerFromIndex(i)).OfType<Control>().ToArray();
    }

    private static Control UpNextRow(QueuePage page, int index) =>
        MobileFixtures.Named<ItemsControl>(page, "QueueList").ContainerFromIndex(index)!;

    private static double Top(Control c, Visual to) => c.TranslatePoint(default, to)!.Value.Y;

    /// <summary>Drags <paramref name="row"/> by its handle until its centre is at <paramref name="targetY"/> (window space).</summary>
    private static void DragBy(Window window, Control row, double dy)
    {
        var handle = row.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DragHandle");
        var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(start.X, start.Y + dy / 2), RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(start.X, start.Y + dy), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(start.X, start.Y + dy), MouseButton.Left, RawInputModifiers.None);
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void Off_ByDefault_TheQueuePageIsAsBefore()
    {
        using var fresh = MobileFixtures.MakeRig(Songs(1));
        Assert.False(fresh.Shell.ShowQueueHistory);

        var (rig, window, _, page, _) = OpenQueue(history: false);
        using var _rig = rig;
        Assert.True(rig.Shell.Player.HasPlayedSongs);
        Assert.False(MobileFixtures.Named<StackPanel>(page, "HistorySection").IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<Border>(page, "NowInline").IsEffectivelyVisible);
        Assert.True(MobileFixtures.Named<Grid>(page, "NowRow").IsEffectivelyVisible);
        Assert.Same(MobileFixtures.Named<Grid>(page, "Layout"), MobileFixtures.Named<Grid>(page, "UpNextHeader").Parent);
        window.Close();
    }

    [AvaloniaFact]
    public void On_TheQueueIsOneList_PlayedSongsDimmed_ThenTheCurrentRow_ThenUpNext()
    {
        var (rig, window, _, page, _) = OpenQueue(history: true);
        using var _rig = rig;
        var content = MobileFixtures.Named<StackPanel>(page, "QueueContent");
        var section = MobileFixtures.Named<StackPanel>(page, "HistorySection");
        var now = MobileFixtures.Named<Border>(page, "NowInline");
        var header = MobileFixtures.Named<Grid>(page, "UpNextHeader");

        Assert.True(section.IsEffectivelyVisible);
        Assert.Equal("History", MobileFixtures.Named<TextBlock>(page, "HistoryLabel").Text);
        var rows = HistoryRows(page);
        Assert.Equal(new[] { "S0", "S1" }, rows.Select(r => r.GetVisualDescendants().OfType<TextBlock>().First().Text));
        Assert.All(rows, r => Assert.Contains(r.GetVisualDescendants().OfType<Border>(), b => b.Name == "DragHandle"));
        Assert.True(rows[0].GetVisualDescendants().OfType<StackPanel>().First().Opacity < 1);   // dimmed

        Assert.True(now.IsEffectivelyVisible);
        Assert.Equal("S2", MobileFixtures.Named<TextBlock>(page, "NowInlineTitle").Text);
        Assert.False(MobileFixtures.Named<Grid>(page, "NowRow").IsEffectivelyVisible);   // the list's row takes its place
        Assert.Same(content, header.Parent);

        Assert.True(Top(rows[1], content) < Top(now, content));
        Assert.True(Top(now, content) < Top(header, content));
        Assert.True(Top(header, content) < Top(UpNextRow(page, 0), content));
        Assert.Equal(new[] { "S3", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        window.Close();
    }

    [AvaloniaFact]
    public void TurnedOffAgain_TheHeaderGoesBackAbove()
    {
        var (rig, window, _, page, _) = OpenQueue(history: true);
        using var _rig = rig;

        rig.Shell.ShowQueueHistory = false;
        window.UpdateLayout();

        Assert.Same(MobileFixtures.Named<Grid>(page, "Layout"), MobileFixtures.Named<Grid>(page, "UpNextHeader").Parent);
        Assert.True(MobileFixtures.Named<Grid>(page, "NowRow").IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<Border>(page, "NowInline").IsEffectivelyVisible);
        Assert.False(MobileFixtures.Named<StackPanel>(page, "HistorySection").IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void TappingAPlayedSong_PlaysItAgain_AndTheLaterOnesGoBackToUpNext_WithoutCopies()
    {
        var (rig, window, _, page, songs) = OpenQueue(history: true);
        using var _rig = rig;

        Tap(window, HistoryRows(page)[0]);   // S0

        Assert.Same(songs[0], rig.Shell.Player.CurrentTrack);
        Assert.Equal(new[] { "S1", "S2", "S3", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        Assert.False(rig.Shell.Player.HasPlayedSongs);
        Assert.True(rig.Shell.IsQueueOpen);   // the sheet stays up
        window.Close();
    }

    [AvaloniaFact]
    public void Hide_FoldsThePlayedSongsToTheTitle_AndShowUnfoldsThem()
    {
        var (rig, window, _, page, _) = OpenQueue(history: true, played: 8, count: 20);
        using var _rig = rig;
        var toggle = MobileFixtures.Named<Button>(page, "HistoryToggleButton");
        var list = MobileFixtures.Named<ItemsControl>(page, "HistoryList");
        var scroll = MobileFixtures.Named<ScrollViewer>(page, "QueueScroll");
        Assert.True(scroll.Offset.Y > 0);    // opened on the current row
        scroll.Offset = default;             // up to the title, as a user would scroll
        window.UpdateLayout();
        Assert.Equal("Hide", toggle.Content);

        Tap(window, toggle);
        Assert.False(list.IsEffectivelyVisible);
        Assert.True(MobileFixtures.Named<TextBlock>(page, "HistoryLabel").IsEffectivelyVisible);
        Assert.Equal("Show", toggle.Content);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(0, scroll.Offset.Y);   // Show stays in sight

        Tap(window, toggle);
        Assert.True(list.IsEffectivelyVisible);
        Assert.Equal("Hide", toggle.Content);
        window.Close();
    }

    [AvaloniaFact]
    public void TheList_OpensOnTheCurrentRow_WithTheLastPlayedSongJustAbove()
    {
        var (rig, window, _, page, _) = OpenQueue(history: true, played: 8, count: 20);
        using var _rig = rig;
        var scroll = MobileFixtures.Named<ScrollViewer>(page, "QueueScroll");
        var content = MobileFixtures.Named<StackPanel>(page, "QueueContent");
        var nowTop = Top(MobileFixtures.Named<Border>(page, "NowInline"), content);

        Assert.True(nowTop > 84);
        Assert.Equal(nowTop - 84, scroll.Offset.Y, 1);
        window.Close();
    }

    [AvaloniaFact]
    public void DraggingAnUpNextSong_AboveTheCurrentRow_PutsItBeforeTheCurrentSong()
    {
        var (rig, window, _, page, songs) = OpenQueue(history: true, played: 1);
        using var _rig = rig;
        var content = MobileFixtures.Named<StackPanel>(page, "QueueContent");
        var now = MobileFixtures.Named<Border>(page, "NowInline");
        var row = UpNextRow(page, 1);   // S3 (Up Next: S2 S3 S4)
        var rowMid = Top(row, content) + row.Bounds.Height / 2;
        var nowMid = Top(now, content) + now.Bounds.Height / 2;

        DragBy(window, row, nowMid - rowMid - 10);   // its centre just above the current row's

        Assert.Same(songs[1], rig.Shell.Player.CurrentTrack);                        // keeps playing
        Assert.Equal(new[] { "S0", "S3" }, rig.Shell.Player.PlayedSongs.Select(p => p.Track.Title));
        Assert.Equal(new[] { "S2", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        rig.Shell.Player.PreviousCommand.Execute(null);
        Assert.Same(songs[3], rig.Shell.Player.CurrentTrack);
        window.Close();
    }

    [AvaloniaFact]
    public void DraggingAPlayedSong_BelowTheCurrentRow_PlaysItAgainNext()
    {
        var (rig, window, _, page, songs) = OpenQueue(history: true, played: 2);
        using var _rig = rig;
        // The list opens on the current row with S1 peeking above: scroll up to S0, as a user would.
        MobileFixtures.Named<ScrollViewer>(page, "QueueScroll").Offset = default;
        window.UpdateLayout();
        var content = MobileFixtures.Named<StackPanel>(page, "QueueContent");
        var now = MobileFixtures.Named<Border>(page, "NowInline");
        var row = HistoryRows(page)[0];   // S0
        var rowMid = Top(row, content) + row.Bounds.Height / 2;
        var nowMid = Top(now, content) + now.Bounds.Height / 2;

        DragBy(window, row, nowMid - rowMid + 10);   // just past the current row's centre

        Assert.Same(songs[2], rig.Shell.Player.CurrentTrack);
        Assert.Equal(new[] { "S1" }, rig.Shell.Player.PlayedSongs.Select(p => p.Track.Title));
        Assert.Equal(new[] { "S0", "S3", "S4" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        window.Close();
    }

    [AvaloniaFact]
    public void DraggingWithinUpNext_StillReorders_WithTheHistoryOn()
    {
        var (rig, window, _, page, _) = OpenQueue(history: true, played: 1, count: 6);
        using var _rig = rig;
        var row = UpNextRow(page, 0);   // Up Next: S2 S3 S4 S5

        DragBy(window, row, row.Bounds.Height * 2 + 5);

        Assert.Equal(new[] { "S3", "S4", "S2", "S5" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        Assert.Equal(new[] { "S0" }, rig.Shell.Player.PlayedSongs.Select(p => p.Track.Title));
        window.Close();
    }

    /// <summary>The theme clips an ItemsControl to its bounds: a row dragged out of its list (an
    /// Up Next song above the current row) vanished mid-drag (emulator, 11-07).</summary>
    [AvaloniaFact]
    public void ARowDraggedOutOfItsList_IsNotClipped()
    {
        var (rig, window, _, page, _) = OpenQueue(history: true);
        using var _rig = rig;

        Assert.False(MobileFixtures.Named<ItemsControl>(page, "QueueList").ClipToBounds);
        Assert.False(MobileFixtures.Named<ItemsControl>(page, "HistoryList").ClipToBounds);
        window.Close();
    }

    [AvaloniaFact]
    public void Landscape_KeepsTheHeadersCurrentTrack_AndTheListHasItsOwnRow()
    {
        var (rig, window, _, page, _) = OpenQueue(history: true, width: 915, height: 412);
        using var _rig = rig;

        Assert.True(MobileFixtures.Named<Grid>(page, "NowRow").IsEffectivelyVisible);
        Assert.True(MobileFixtures.Named<Border>(page, "NowInline").IsEffectivelyVisible);
        Assert.Equal(1, Grid.GetColumn(MobileFixtures.Named<ScrollViewer>(page, "QueueScroll")));
        window.Close();
    }

    [Fact]
    public async Task TheSetting_IsSaved_AndReadBack()
    {
        using var rig = MobileFixtures.MakeRig(Songs(1), seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            Assert.False(s.MobileQueueShowHistory);   // the default
            s.MobileQueueShowHistory = true;
            await p.SaveSettingsAsync(s);
        });
        await rig.Shell.InitializeAsync();
        Assert.True(rig.Shell.ShowQueueHistory);

        rig.Shell.ShowQueueHistory = false;
        await rig.Shell.PendingPlayerBarSave;
        Assert.False((await rig.Persistence.LoadSettingsAsync()).MobileQueueShowHistory);
    }

    [AvaloniaFact]
    public async Task TheSettingsSwitch_TurnsItOn()
    {
        using var rig = MobileFixtures.MakeRig(Songs(1));
        var shell = rig.Shell;
        var window = MobileFixtures.Mount(shell, out _);
        try
        {
            shell.OpenSettingsCommand.Execute(null);
            var vm = Assert.IsType<SettingsPageViewModel>(shell.CurrentPage);
            await vm.Loaded;
            MobileFixtures.Land(window);
            var page = MobileFixtures.PageView((ShellView)window.Content!, vm);
            var toggle = MobileFixtures.Named<ToggleButton>(page, "QueueHistorySwitch");
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.Equal("Played Songs in Queue", Avalonia.Automation.AutomationProperties.GetName(toggle));
            Assert.False(toggle.IsChecked);

            toggle.IsChecked = true;
            Assert.True(shell.ShowQueueHistory);
        }
        finally
        {
            window.Close();
        }
    }

    // ── Add Before Current Song ──

    [AvaloniaFact]
    public void AddBeforeCurrent_OnAnotherSongsMenu_PutsItJustBeforeThePlayingSong()
    {
        var songs = Songs(3);
        var extra = MobileFixtures.Song("Extra");
        using var rig = MobileFixtures.MakeRig(songs.Append(extra).ToArray());
        rig.Shell.Player.PlayTracks(songs, 0, "Alpha");

        var menu = ContextSheetViewModel.ForTrack(rig.Shell, extra);
        Assert.True(menu.CanAddBeforeCurrent);
        menu.AddBeforeCurrentCommand.Execute(null);

        Assert.Same(songs[0], rig.Shell.Player.CurrentTrack);
        Assert.Equal(new[] { extra }, rig.Shell.Player.PlayedSongs.Select(p => p.Track));
        Assert.Equal(new[] { "S1", "S2" }, rig.Shell.Player.UpNext.Select(t => t.Title));
        Assert.Equal("Added before the current song", rig.Shell.Player.Notice);
        Assert.False(rig.Shell.IsSheetOpen);

        rig.Shell.Player.PreviousCommand.Execute(null);
        Assert.Same(extra, rig.Shell.Player.CurrentTrack);
    }

    [AvaloniaFact]
    public void AddBeforeCurrent_IsNotOffered_WithNothingPlaying_OrOnThePlayingSongsOwnMenu()
    {
        var songs = Songs(2);
        using var rig = MobileFixtures.MakeRig(songs);
        Assert.False(ContextSheetViewModel.ForTrack(rig.Shell, songs[1]).CanAddBeforeCurrent);

        rig.Shell.Player.PlayTracks(songs, 0);
        Assert.False(ContextSheetViewModel.ForTrack(rig.Shell, songs[0]).CanAddBeforeCurrent);
        Assert.True(ContextSheetViewModel.ForTrack(rig.Shell, songs[1]).CanAddBeforeCurrent);
        Assert.True(ContextSheetViewModel.ForAlbum(rig.Shell, MobileFixtures.MakeAlbum("Record", "Band", songs)).CanAddBeforeCurrent);
    }

    [AvaloniaFact]
    public void TheMenuRow_ShowsUnderAddToQueue_WhileASongPlays()
    {
        var songs = Songs(2);
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(songs, 0);
        rig.Shell.OpenTrackSheetCommand.Execute(songs[1]);
        window.UpdateLayout();

        var row = MobileFixtures.Named<Button>(view, "MenuAddBeforeCurrent");
        Assert.True(row.IsEffectivelyVisible);
        Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Add Before Current");
        var queueRow = MobileFixtures.Named<Button>(view, "MenuAddToQueue");
        Assert.True(row.TranslatePoint(default, view)!.Value.Y > queueRow.TranslatePoint(default, view)!.Value.Y);

        row.Command!.Execute(row.CommandParameter);
        Assert.Equal(new[] { songs[1] }, rig.Shell.Player.PlayedSongs.Select(p => p.Track));
        window.Close();
    }
}
