using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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
/// A long press on a song row opens its menu on every kind of song row (beta testers 11-22): the
/// Queue's Up Next and played rows and Settings › Listening History's recent songs had only their
/// tap (or nothing). In the Queue the hold must leave the swipe and the handle's drag alone.
/// </summary>
public class MobileRowLongPressTests
{
    /// <summary>As MobileContextSheetTests: HoldingRoutedEventArgs has no public constructor.</summary>
    private static void RaiseHold(Control target)
    {
        var args = (HoldingRoutedEventArgs)Activator.CreateInstance(typeof(HoldingRoutedEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, binder: null,
            args: new object?[] { HoldingState.Started, new Point(10, 10), PointerType.Touch, null }, culture: null)!;
        args.RoutedEvent = InputElement.HoldingEvent;
        target.RaiseEvent(args);
    }

    private static (MobileFixtures.Rig Rig, Window Window, QueuePage Page) OpenQueue(bool history = false)
    {
        var songs = Enumerable.Range(0, 5).Select(i => MobileFixtures.Song($"S{i}")).ToArray();
        var rig = MobileFixtures.MakeRig(songs);
        rig.Shell.ShowQueueHistory = history;
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(songs, 0);
        if (history) rig.Shell.Player.NextCommand.Execute(null);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.ToggleQueueCommand.Execute(null);
        window.UpdateLayout();
        var page = MobileFixtures.Find<QueuePage>(view);
        page.CompleteSlide();
        window.UpdateLayout();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (rig, window, page);
    }

    private static Border UpNextRow(QueuePage page, int index) =>
        MobileFixtures.Named<ItemsControl>(page, "QueueList").ContainerFromIndex(index)!
            .GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("queue-row"));

    [AvaloniaFact]
    public void UpNextRow_LongPress_OpensTheSongsMenu()
    {
        var (rig, window, page) = OpenQueue();
        using var _ = rig;
        var target = rig.Shell.Player.UpNext[1];

        RaiseHold(UpNextRow(page, 1));

        Assert.True(rig.Shell.IsSheetOpen);
        Assert.Same(target, rig.Shell.Sheet!.Track);
        window.Close();
    }

    [AvaloniaFact]
    public void AHoldOnTheDragHandle_IsNoLongPress()
    {
        var (rig, window, page) = OpenQueue();
        using var _ = rig;
        var handle = UpNextRow(page, 1).GetVisualDescendants().OfType<Border>().First(b => b.Name == "DragHandle");

        RaiseHold(handle);

        Assert.False(rig.Shell.IsSheetOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void AfterALongPress_TheFingerSlidingSidewaysRemovesNothing()
    {
        var (rig, window, page) = OpenQueue();
        using var _ = rig;
        var row = UpNextRow(page, 1);
        var y = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), window)!.Value.Y;
        var count = rig.Shell.Player.UpNext.Count;

        window.MouseDown(new Point(250, y), MouseButton.Left, RawInputModifiers.None);
        RaiseHold(row);
        window.MouseMove(new Point(200, y), RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(30, y), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(30, y), MouseButton.Left, RawInputModifiers.None);

        Assert.Equal(count, rig.Shell.Player.UpNext.Count);
        Assert.True(rig.Shell.IsSheetOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void PlayedRow_LongPress_OpensTheSongsMenu_AndDoesNotPlayIt()
    {
        var (rig, window, page) = OpenQueue(history: true);
        using var _ = rig;
        var list = MobileFixtures.Named<ItemsControl>(page, "HistoryList");
        var row = list.ContainerFromIndex(0)!.GetVisualDescendants().OfType<Button>().First();
        var current = rig.Shell.Player.CurrentTrack;

        RaiseHold(row);

        Assert.True(rig.Shell.IsSheetOpen);
        Assert.Equal("S0", rig.Shell.Sheet!.Track!.Title);
        Assert.Same(current, rig.Shell.Player.CurrentTrack);
        window.Close();
    }

    [AvaloniaFact]
    public async Task ListeningHistoryRow_LongPress_OpensTheSongsMenu()
    {
        var song = MobileFixtures.Song("Song", artist: "Band");
        using var rig = MobileFixtures.MakeRig(new[] { song }, log: h => h.Seed(song, DateTime.UtcNow.AddMinutes(-5)));
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSettingsCommand.Execute(null);
        var settings = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await settings.Loaded;
        settings.OpenPaneCommand.Execute(SettingsPane.History);
        MobileFixtures.Land(window);
        var list = MobileFixtures.Named<ItemsControl>(MobileFixtures.Find<ListeningHistoryPane>(view), "RecentList");
        var row = (Control)list.ContainerFromIndex(0)!.GetVisualDescendants().OfType<StackPanel>().First();

        RaiseHold(row);

        Assert.True(rig.Shell.IsSheetOpen);
        Assert.Same(song, rig.Shell.Sheet!.Track);
        window.Close();
    }
}
