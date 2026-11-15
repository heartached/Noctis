using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Scrolling the phone's Queue (tester 11-09: Up Next still stuttered when scrolling). As on the
/// Songs list (MobileSongListScrollTests), a row that leaves the screen is reused for the one that
/// comes in, not rebuilt and re-styled. See RowItemsControl.
/// </summary>
public class MobileQueueScrollTests
{
    private static (MobileFixtures.Rig Rig, Window Window, QueuePage Page, Track[] Songs) OpenQueue(int count)
    {
        var songs = Enumerable.Range(0, count).Select(i => MobileFixtures.Song($"Q{i:D3}")).ToArray();
        var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(songs, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.ToggleQueueCommand.Execute(null);
        window.UpdateLayout();
        var page = MobileFixtures.Find<QueuePage>(view);
        page.CompleteSlide();
        window.UpdateLayout();
        return (rig, window, page, songs);
    }

    private static Border RowOf(Control container) =>
        container.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("queue-row"));

    [AvaloniaFact]
    public void ScrollingUpNext_ReusesRows_KeepsThemStyled_AndShowsTheRightSongs()
    {
        var (rig, window, page, _) = OpenQueue(400);
        using var _rig = rig;
        var scroll = MobileFixtures.Named<ScrollViewer>(page, "QueueScroll");
        var list = MobileFixtures.Named<ItemsControl>(page, "QueueList");
        Assert.IsType<RowItemsControl>(list);

        var rows = new HashSet<Border>(ReferenceEqualityComparer.Instance);
        var maxRealized = 0;
        for (var y = 0; y <= 15000; y += 45)
        {
            scroll.Offset = new Vector(0, y);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var realized = list.GetRealizedContainers().ToList();
            maxRealized = System.Math.Max(maxRealized, realized.Count);
            foreach (var container in realized) rows.Add(RowOf(container));
        }
        Assert.True(scroll.Offset.Y > 10000);
        // Every row ever shown was one of the few realized at once; before, each of the ~220 songs
        // scrolled past got a row of its own.
        Assert.InRange(rows.Count, 1, maxRealized + 4);

        var upNext = rig.Shell.Player.UpNext;
        foreach (var container in list.GetRealizedContainers())
        {
            var index = list.IndexFromContainer(container);
            var row = RowOf(container);
            Assert.Same(upNext[index], row.DataContext);
            Assert.Equal(upNext[index].Title, row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("row-title")).Text);
            // Kept under the list in the logical tree, so its styles stay applied between recycles.
            Assert.Same(list, ((ILogical)row).LogicalParent);
            Assert.True(((ILogical)row).IsAttachedToLogicalTree);
        }
        window.Close();
    }

    [AvaloniaFact]
    public void TheNextSong_RebuildsUpNext_OnTheSameRows()
    {
        // Up Next is replaced with one Reset on every change (a song ending included): its rows are
        // handed the new songs, not built again.
        var (rig, window, page, _) = OpenQueue(40);
        using var _rig = rig;
        var list = MobileFixtures.Named<ItemsControl>(page, "QueueList");
        var before = list.GetRealizedContainers().Select(RowOf).ToHashSet(ReferenceEqualityComparer.Instance);

        rig.Shell.Player.NextCommand.Execute(null);
        window.UpdateLayout();

        var after = list.GetRealizedContainers().Select(RowOf).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, r => Assert.Contains(r, before));
        Assert.Equal(rig.Shell.Player.UpNext[0].Title,
            RowOf(list.ContainerFromIndex(0)!).GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("row-title")).Text);
        window.Close();
    }

    [AvaloniaFact]
    public void ADraggedRow_IsLiftedOnTheSheetColour_AndLetGo_StillSwipes()
    {
        var (rig, window, page, _) = OpenQueue(6);
        using var _rig = rig;
        var list = MobileFixtures.Named<ItemsControl>(page, "QueueList");
        var container = list.ContainerFromIndex(0)!;
        var row = RowOf(container);
        var handle = row.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DragHandle");
        var start = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;

        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(start.X, start.Y + 20), RawInputModifiers.LeftMouseButton);
        Assert.True(page.TryFindResource("QueueSheetBase", page.ActualThemeVariant, out var sheet));
        Assert.Same(sheet, row.Background);
        Assert.Equal(1, container.ZIndex);
        window.MouseUp(new Point(start.X, start.Y + 20), MouseButton.Left, RawInputModifiers.None);

        // Back to see-through, and still hit-testable across the row for the swipe.
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(row.Background).Color);
        Assert.Equal(0, container.ZIndex);
        Assert.Null(container.RenderTransform);

        var y = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), window)!.Value.Y;
        window.MouseDown(new Point(250, y), MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(new Point(200, y), RawInputModifiers.LeftMouseButton);
        window.MouseMove(new Point(30, y), RawInputModifiers.LeftMouseButton);
        window.MouseUp(new Point(30, y), MouseButton.Left, RawInputModifiers.None);
        Assert.Equal(4, rig.Shell.Player.UpNext.Count);
        Assert.DoesNotContain(rig.Shell.Player.UpNext, t => t.Title == "Q001");
        window.Close();
    }
}
