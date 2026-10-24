using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// GitHub #134: per-row state of the Queue panel's list (<see cref="PlayerViewModel.QueueRows"/>).
/// Rows are virtualized and recycled, so nothing per row can be bound: each container is
/// stamped with its position label and a played / playing class when it is prepared, and
/// the realized ones are re-stamped after the rows change.
/// </summary>
internal static class QueuePanelRows
{
    public const string PlayedClass = "queue-played";
    public const string CurrentClass = "queue-current";
    private const string NumberName = "QueueRowNumber";

    /// <summary>Stamps one (possibly recycled) row container.</summary>
    public static void Stamp(ListBox listBox, Control container, int row, PlayerViewModel player)
    {
        var kind = row >= 0 && row < player.QueueRows.Count ? player.GetQueueRowKind(row) : QueueRowKind.Upcoming;
        container.Classes.Set(PlayedClass, kind == QueueRowKind.Played);
        container.Classes.Set(CurrentClass, kind == QueueRowKind.Current);
        var label = row >= 0 ? player.QueueRowNumber(row) : string.Empty;
        if (!SetNumber(container, label))
        {
            // The row's template may not be applied yet when the container is prepared.
            Dispatcher.UIThread.Post(() =>
            {
                if (listBox.IndexFromContainer(container) == row)
                    SetNumber(container, label);
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>Re-stamps the realized rows after the queue changed.</summary>
    public static void StampRealized(ListBox listBox, PlayerViewModel player)
    {
        foreach (var container in listBox.GetRealizedContainers())
        {
            var row = listBox.IndexFromContainer(container);
            if (row >= 0) Stamp(listBox, container, row, player);
        }
    }

    private static bool SetNumber(Control container, string label)
    {
        var tb = container.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == NumberName);
        if (tb == null) return false;
        tb.Text = label;
        return true;
    }

    /// <summary>Scrolls <paramref name="listBox"/> back to its first row (Keep Played Songs
    /// turned off: the rows above Up Next are gone, so the old offset points into Up Next).
    /// Returns false when the list has no scroller yet.</summary>
    public static bool ScrollToTop(ListBox listBox)
    {
        if (listBox.FindDescendantOfType<ScrollViewer>() is not { } scroller) return false;
        if (scroller.Offset.Y > 0)
        {
            scroller.Offset = scroller.Offset.WithY(0);
            listBox.UpdateLayout();
        }
        return true;
    }

    /// <summary>Scrolls <paramref name="listBox"/> so <paramref name="row"/> sits second from
    /// the top (one played row stays in sight above the playing one), or as close as the
    /// list's ends allow. Returns false when the row could not be laid out.</summary>
    public static bool ScrollRowNearTop(ListBox listBox, int row)
    {
        if (row < 0 || row >= listBox.ItemCount) return false;
        listBox.ScrollIntoView(row);
        listBox.UpdateLayout();
        if (listBox.ContainerFromIndex(row) is not Control container
            || listBox.FindDescendantOfType<ScrollViewer>() is not { } scroller
            || container.TranslatePoint(new Point(0, 0), scroller) is not { } top)
            return false;
        var pitch = container.Bounds.Height + container.Margin.Top + container.Margin.Bottom;
        var max = Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height);
        var target = Math.Clamp(scroller.Offset.Y + top.Y - container.Margin.Top - pitch, 0, max);
        if (Math.Abs(target - scroller.Offset.Y) > 0.5)
        {
            scroller.Offset = scroller.Offset.WithY(target);
            listBox.UpdateLayout();
        }
        return true;
    }
}
