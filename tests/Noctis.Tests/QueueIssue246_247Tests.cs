using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #246: a multi-selection dragged in the Queue panel lifts out as one block (no drop
/// slot inside it), and the drop it previews is the move MoveQueueRows makes.
/// GitHub #247: a queue row line with an extra field ("Title · 4:12") trims the lead field and
/// keeps the extra one visible.
/// </summary>
public class QueueIssue246_247Tests
{
    private const double Pitch = 52;

    private static Track Trk(string name, string? title = null) => new()
    {
        Id = Guid.NewGuid(),
        Title = title ?? name,
        Artist = "A",
        FilePath = $"C:/t/{name}.mp3",
        Duration = TimeSpan.FromSeconds(252),
    };

    // ── #246: the block preview ──

    [Theory]
    [InlineData(new[] { 2, 3, 4 })]
    [InlineData(new[] { 0, 1 })]
    [InlineData(new[] { 5 })]
    public void ContiguousBlock_HeldAtItsOwnPlace_MovesNothing_AndDropsAsANoOp(int[] rows)
    {
        // Every slot the old preview offered between the block's own rows is now one landing
        // spot, the block's own: nothing slides and the insertion index is a no-op.
        for (var i = 0; i < 9; i++)
            Assert.Equal(0, LiquidReorder.BlockGapOffset(i, rows, rows[0], Pitch));

        var (vm, tracks) = Queue(9);
        var before = vm.QueueRows.ToList();
        Assert.Equal(-1, vm.MoveQueueRows(rows, LiquidReorder.InsertIndexFor(rows, rows[0])));
        Assert.Equal(before, vm.QueueRows);
        GC.KeepAlive(tracks);
    }

    [Theory]
    [InlineData(new[] { 1, 4, 6 }, 0)]
    [InlineData(new[] { 1, 4, 6 }, 2)]
    [InlineData(new[] { 1, 4, 6 }, 5)]
    [InlineData(new[] { 1, 4, 6 }, 6)]
    [InlineData(new[] { 2, 3 }, 0)]
    [InlineData(new[] { 2, 3 }, 3)]
    [InlineData(new[] { 2, 3 }, 7)]
    [InlineData(new[] { 0, 8 }, 4)]
    public void BlockPreview_IsTheMoveMoveQueueRowsMakes(int[] rows, int landAt)
    {
        var (vm, _) = Queue(9);
        var before = vm.QueueRows.ToList();

        // Where the preview slides every other row to.
        var predicted = new Track?[before.Count];
        for (var i = 0; i < before.Count; i++)
        {
            if (rows.Contains(i)) continue;
            var slot = i + (int)Math.Round(LiquidReorder.BlockGapOffset(i, rows, landAt, Pitch) / Pitch);
            Assert.InRange(slot, 0, before.Count - 1);
            Assert.Null(predicted[slot]);
            predicted[slot] = before[i];
        }
        // The gap it opens is exactly the block's size, at landAt.
        for (var k = 0; k < before.Count; k++)
            Assert.Equal(k >= landAt && k < landAt + rows.Length, predicted[k] == null);

        var landed = vm.MoveQueueRows(rows, LiquidReorder.InsertIndexFor(rows, landAt));

        var after = vm.QueueRows.ToList();
        var contiguousNoOp = rows[^1] - rows[0] == rows.Length - 1 && landAt == rows[0];
        Assert.Equal(contiguousNoOp ? -1 : landAt, landed);
        for (var k = 0; k < after.Count; k++)
        {
            if (predicted[k] is { } stays) Assert.Same(stays, after[k]);
            else Assert.Same(before[rows[k - landAt]], after[k]);
        }
    }

    [Fact]
    public void InsertIndexFor_MapsTheLandingSlotBackToTheCurrentRows()
    {
        Assert.Equal(0, LiquidReorder.InsertIndexFor(new[] { 2, 3 }, 0));
        Assert.Equal(4, LiquidReorder.InsertIndexFor(new[] { 2, 3 }, 2));
        Assert.Equal(5, LiquidReorder.InsertIndexFor(new[] { 2, 3 }, 3));
        Assert.Equal(9, LiquidReorder.InsertIndexFor(new[] { 1, 4, 6 }, 6));
    }

    [Fact]
    public void BlockGapOffset_NoTarget_IsZero()
        => Assert.Equal(0, LiquidReorder.BlockGapOffset(3, new[] { 1, 2 }, -1, Pitch));

    [AvaloniaFact]
    public void QueuePanel_RowsDragFromTheList_AndTheCardHasACountBadge()
    {
        var main = new MainWindow();
        try
        {
            var badge = main.FindControl<Border>("QueueDragCount");
            Assert.NotNull(badge);
            Assert.False(badge!.IsVisible);
            Assert.IsType<TextBlock>(badge.Child);
            var preview = main.FindControl<Border>("QueueDragPreview")!;
            Assert.Contains(badge, preview.GetLogicalDescendants());
        }
        finally
        {
            main.Close();
        }
    }

    // ── #247: the extra field stays visible ──

    [Fact]
    public void LineParts_SplitTheLeadFromTheExtraField()
    {
        var track = Trk("t", "Fade Into You Remix (Lobsterdust Edit)");
        var layout = QueueRowLayout.Default with { Line1Extra = QueueRowField.Duration, Line2Extra = QueueRowField.Year };

        Assert.Equal(("Fade Into You Remix (Lobsterdust Edit)", " · 4:12"), layout.LineParts(track, 1));
        Assert.Equal("Fade Into You Remix (Lobsterdust Edit) · 4:12", layout.LineText(track, 1));
        // No year: the tail is empty, no stray dot.
        Assert.Equal(("A", ""), layout.LineParts(track, 2));
        // A blank lead: the extra field leads.
        var bare = new Track { Duration = TimeSpan.FromSeconds(189) };
        Assert.Equal(("3:09", ""), (layout with { Line1 = QueueRowField.Composer }).LineParts(bare, 1));

        var c = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(" · 4:12", QueueRowConverters.Line1Tail.Convert(new List<object?> { track, layout }, typeof(string), null, c));
        Assert.Equal("A", QueueRowConverters.Line2Lead.Convert(new List<object?> { track, layout }, typeof(string), null, c));
        Assert.Equal(string.Empty, QueueRowConverters.Line1Tail.Convert(new List<object?> { null, layout }, typeof(string), null, c));
    }

    /// <summary>The real row template (the drawer lifted out of the unshown MainWindow, as in
    /// AccessibleNamesTests): a long lead trims and the tail is laid out in full beside it.</summary>
    [AvaloniaFact]
    public async Task QueueRow_LongTitle_TrimsTheTitle_AndKeepsTheExtraFieldVisible()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var main = new MainWindow();
        Window? window = null;
        try
        {
            var drawer = main.FindControl<Border>("QueuePopupPanel")!;
            var list = main.FindControl<ListBox>("QueuePopupListBox")!;
            ((Panel)drawer.Parent!).Children.Remove(drawer);
            drawer.IsVisible = true;
            drawer.Opacity = 1;
            drawer.Width = QueuePanelMetrics.Default;
            list.ItemsSource = new List<Track> { Trk("long", "Fade Into You Remix (Lobsterdust Edit) [Extended Club Version]") };
            window = new Window { Width = 1280, Height = 900, Content = drawer };
            window.Show();
            await PumpUntil(() => list.GetVisualDescendants().OfType<ListBoxItem>().Any());

            var row = list.GetVisualDescendants().OfType<ListBoxItem>().First();
            var lead = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("one-line-explicit-title"));
            var line = (Noctis.Controls.LeadTailPanel)lead.Parent!;
            var tail = line.Children.OfType<TextBlock>().Last();
            // No MainWindowViewModel here, so the layout binding falls back to title only:
            // put the line the issue shows in by hand.
            lead.Text = "Fade Into You Remix (Lobsterdust Edit) [Extended Club Version]";
            tail.Text = " · 4:12";
            Pump(window);

            var text = (StackPanel)line.Parent!;
            Assert.True(tail.Bounds.Width > 20, $"the tail was squeezed to {tail.Bounds.Width}");
            Assert.True(tail.Bounds.Right <= text.Bounds.Width + 0.5,
                $"the tail ends at {tail.Bounds.Right}, past the text column ({text.Bounds.Width})");
            Assert.True(lead.TextLayout.TextLines[0].HasCollapsed, "the long title is not trimmed");
            Assert.Equal(lead.Bounds.Right, tail.Bounds.Left, 1);

            // A short title: nothing trims and the tail follows it directly.
            lead.Text = "Pyramids";
            Pump(window);
            Assert.False(lead.TextLayout.TextLines[0].HasCollapsed);
            Assert.Equal(lead.Bounds.Right, tail.Bounds.Left, 1);
            Assert.True(tail.Bounds.Width > 20);

            // A long extra field (an album name): it may not push the lead out or run past the row.
            lead.Text = "Taylor Swift";
            tail.Text = " · reputation (Big Machine Radio Release Special) [Deluxe Anniversary Edition]";
            Pump(window);
            Assert.True(lead.Bounds.Width > 40, $"the lead was squeezed to {lead.Bounds.Width}");
            Assert.True(tail.Bounds.Right <= text.Bounds.Width + 0.5,
                $"the long tail ends at {tail.Bounds.Right}, past the text column ({text.Bounds.Width})");
            Assert.True(tail.TextLayout.TextLines[0].HasCollapsed, "the long tail is not trimmed");
        }
        finally
        {
            window?.Close();
            main.Close();
        }
    }

    private static (PlayerViewModel Vm, List<Track> Tracks) Queue(int upNext)
    {
        var vm = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(),
            new FakeAnimatedCoverService());
        var tracks = Enumerable.Range(0, upNext + 1).Select(i => Trk($"t{i}")).ToList();
        vm.ReplaceQueueAndPlay(tracks, 0); // Up Next: t1..tN
        Assert.Equal(upNext, vm.QueueRows.Count);
        return (vm, tracks);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
