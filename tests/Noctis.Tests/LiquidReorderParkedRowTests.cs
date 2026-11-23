using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord 11-23 (playlist page): a song dragged from far down to the top, when the top was
/// not on screen at the start, landed as an invisible row until clicked. The lifted row's
/// container is hidden (Opacity 0) every frame; the playlist list parks scrolled-off
/// containers (RetainingVirtualizingStackPanel) and reuses them as they are, and only the
/// realized ones were shown again, so a parked one kept Opacity 0 into its next use.
/// </summary>
public class LiquidReorderParkedRowTests
{
    private const double RowHeight = 40;

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

    private static (Window win, ListBox list, LiquidReorder liquid) Build()
    {
        var list = new ListBox
        {
            VerticalAlignment = VerticalAlignment.Top,
            ItemsSource = Enumerable.Range(0, 300).Select(i => $"Row {i}").ToList(),
            ItemsPanel = new FuncTemplate<Panel?>(() => new RetainingVirtualizingStackPanel()),
            // The top rows are taller (an album-run header on the playlist page): fewer of them
            // fit, so a jump to the top leaves some recycled containers parked, unused.
            ItemTemplate = new FuncDataTemplate<string>((s, _) => new TextBlock
            {
                Text = s,
                Height = int.Parse(s.AsSpan(4)) < 100 ? RowHeight * 3 : RowHeight,
            }),
        };
        var card = new Border { Height = RowHeight, IsVisible = false, VerticalAlignment = VerticalAlignment.Top };
        var host = new Panel { Children = { list, card } };
        var win = new Window { Width = 400, Height = 400, Content = host };
        win.Show();
        Pump(2);
        return (win, list, new LiquidReorder(host, list, card) { Pitch = RowHeight });
    }

    private static void ScrollTo(Window win, ListBox list, int index)
    {
        list.GetVisualDescendants().OfType<ScrollViewer>().First().Offset = new Vector(0, index * RowHeight);
        win.UpdateLayout();
        Pump(2);
    }

    /// <summary>The highest realized row: recycled first, so it is the last a jump hands
    /// a new item and stays parked when fewer rows are realized.</summary>
    private static int TopRow(ListBox list) =>
        list.GetRealizedContainers().Min(c => list.IndexFromContainer(c));

    private static List<ListBoxItem> AllContainers(ListBox list) =>
        list.GetVisualDescendants().OfType<ListBoxItem>().ToList();

    [AvaloniaFact]
    public void LiftedRow_ScrolledOffAndParked_IsShownAgainWhenTheDragEnds()
    {
        var (win, list, liquid) = Build();
        try
        {
            ScrollTo(win, list, 250);
            var top = TopRow(list);
            var source = list.ContainerFromIndex(top);
            Assert.NotNull(source);

            liquid.SourceIndex = liquid.TargetIndex = top;
            liquid.Begin(0);
            Pump(3);
            Assert.Equal(0, source!.Opacity);   // the lifted row's slot is empty

            // Drag to the very top: the list jumps there and the source row's container is parked.
            ScrollTo(win, list, 0);
            liquid.TargetIndex = 0;
            Pump(3);
            Assert.True(!list.GetRealizedContainers().Contains(source),
                string.Join(",", list.GetRealizedContainers().Select(c => list.IndexFromContainer(c))) + " top " + top);

            liquid.Settle(0, () => { });
            Pump(30);

            Assert.Empty(AllContainers(list).Where(c => c.Opacity < 1));
            // the parked containers come back into use for other rows: every one shows its row
            ScrollTo(win, list, 150);
            Assert.All(list.GetRealizedContainers(), c => Assert.Equal(1, c.Opacity));
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void LiftedRow_ScrolledOffAndParked_IsShownAgainWhenTheDragIsCancelled()
    {
        var (win, list, liquid) = Build();
        try
        {
            ScrollTo(win, list, 250);
            var top = TopRow(list);
            liquid.SourceIndex = liquid.TargetIndex = top;
            liquid.Begin(0);
            Pump(3);
            ScrollTo(win, list, 0);
            Pump(3);
            Assert.Null(list.ContainerFromIndex(top));

            liquid.Cancel();
            Pump(2);

            Assert.Empty(AllContainers(list).Where(c => c.Opacity < 1));
            // and every container reused for a row afterwards shows it
            ScrollTo(win, list, 150);
            Assert.All(list.GetRealizedContainers(), c => Assert.Equal(1, c.Opacity));
        }
        finally { win.Close(); }
    }
}
