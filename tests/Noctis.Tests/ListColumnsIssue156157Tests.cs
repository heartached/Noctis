using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;
using static Noctis.Tests.ListColumnsIssue151Tests;

namespace Noctis.Tests;

/// <summary>
/// GitHub #156 (header usability: visible dividers, whole cell clicks and drags, a lifted
/// header and drop bar while moving, a way back to the default sort) and #157 (padding so
/// a column's text never touches the next column's).
/// </summary>
public class ListColumnsIssue156157Tests
{
    // ── #157 padding ──────────────────────────────────────────────────────

    [Fact]
    public void CellInsets_PadBetweenColumns_NotAtTheOuterLeftEdge_NorOnThePinnedDots()
    {
        var l = LibraryColumns.CreateSongs();
        var pad = l.CellPadding;
        Assert.True(pad > 0);
        var cols = l.Visible;
        Assert.Equal((0, pad), l.CellInsets(0));                     // Title keeps its place
        Assert.Equal((pad, pad), l.CellInsets(1));                   // Time
        Assert.Equal((0, 0), l.CellInsets(cols.Count - 1));          // More (pinned)
        Assert.Equal((0, 0), l.CellInsets(-1));
        Assert.Equal((0, 0), l.CellInsets(cols.Count));

        // After a move the new first column loses its left padding, the old one gains it.
        l.Move("Genre", 0);
        Assert.Equal("Genre", l.Visible[0].Key);
        Assert.Equal((0, pad), l.CellInsets(0));
        Assert.Equal((pad, pad), l.CellInsets(1));
    }

    [AvaloniaFact]
    public void SongsView_NeighbouringCells_KeepTwiceThePaddingApart_HeaderLabelsStayOverTheirCells()
    {
        var (_, view, win) = MountSongs(10);
        try
        {
            var header = Header(view);
            var layout = view.Columns;
            var pad = layout.CellPadding;
            var (widths, offsets) = layout.Compute(header.Bounds.Width);
            var row = RowPanels(view)[0];
            for (var i = 0; i < layout.Visible.Count - 1; i++)
            {
                var spec = layout.Visible[i];
                var next = layout.Visible[i + 1];
                if (next.Pinned) break;
                var cell = Cell(row, spec.Key);
                var nextCell = Cell(row, next.Key);
                var boundary = offsets[i] + widths[i];
                // Whatever the text does inside, the cells stop short of the column edge and the
                // next one starts after it: a text that fills its cell cannot touch the next.
                Assert.True(cell.Bounds.Right <= boundary - pad + 0.5, $"{spec.Key} right {cell.Bounds.Right} vs {boundary}");
                Assert.True(nextCell.Bounds.Left >= boundary + pad - 0.5, $"{next.Key} left {nextCell.Bounds.Left} vs {boundary}");
            }

            // Left-aligned columns: header label and row text start at the same x.
            foreach (var key in new[] { "Artist", "Album", "Genre" })
            {
                var h = InWindow(Cell(header, key), win);
                var c = InWindow(Cell(row, key), win);
                Assert.Equal(c.X, h.X, 0.5);
            }
        }
        finally { win.Close(); }
    }

    // ── #156 header ───────────────────────────────────────────────────────

    [AvaloniaFact]
    public void SongsView_Dividers_AreDrawnAtRest_WithABrush_AndSitAboveTheCells()
    {
        var (_, view, win) = MountSongs(10);
        try
        {
            var header = Header(view);
            foreach (var key in new[] { "Title", "Artist", "Album", "Genre" })
            {
                var grip = header.GripFor(key)!;
                var line = (Border)grip.Child!;
                Assert.NotNull(line.Background);                       // label colour
                Assert.Equal(ColumnHeaderPanel.DividerRest, line.Opacity); // drawn without hover
                Assert.True(grip.ZIndex > 0, key);
                // Hit-testing either half of the divider finds the divider, not a label.
                var g = grip.TranslatePoint(default, header)!.Value;
                foreach (var dx in new[] { 1.0, grip.Bounds.Width - 1 })
                {
                    var hit = header.InputHitTest(new Point(g.X + dx, header.Bounds.Height / 2)) as Visual;
                    Assert.Same(grip, hit);
                }
            }

            // Pointer over the header: stronger; over the divider itself: full.
            var title = header.GripFor("Title")!;
            var at = InWindow(title, win).Center;
            win.MouseMove(at);
            Settle(win, 2);
            Assert.Equal(ColumnHeaderPanel.DividerActive, ((Border)title.Child!).Opacity);
            Assert.Equal(ColumnHeaderPanel.DividerHeader, ((Border)header.GripFor("Genre")!.Child!).Opacity);
        }
        finally { win.Close(); }
    }

    /// <summary>A point inside <paramref name="key"/>'s slot but right of its label, clear of the divider.</summary>
    private static Point EmptyPartOfSlot(LibrarySongsView view, Window win, string key)
    {
        var header = Header(view);
        var layout = view.Columns;
        var i = layout.Visible.ToList().FindIndex(s => s.Key == key);
        var (widths, offsets) = layout.Compute(header.Bounds.Width);
        var label = Cell(header, key);
        var x = offsets[i] + widths[i] - 8;
        Assert.True(x > label.Bounds.Right + 2, $"{key}: slot end {x} must be clear of the label {label.Bounds}");
        var origin = header.TranslatePoint(default, win)!.Value;
        return new Point(origin.X + x, origin.Y + header.Bounds.Height / 2);
    }

    [AvaloniaFact]
    public void SongsView_HoverHighlightsTheWholeSlot_AndAClickAnywhereInItSorts()
    {
        var (vm, view, win) = MountSongs(20);
        try
        {
            var header = Header(view);
            var p = EmptyPartOfSlot(view, win, "Genre");
            win.MouseMove(p);
            Settle(win, 2);
            Assert.Equal("Genre", header.HoverKey);
            Assert.NotNull(header.Cursor);
            // The highlight covers the whole slot, label and empty part alike.
            var i = view.Columns.Visible.ToList().FindIndex(s => s.Key == "Genre");
            var (widths, offsets) = view.Columns.Compute(header.Bounds.Width);
            Assert.True(header.HoverBox.IsVisible);
            Assert.NotNull(header.HoverBox.Background);
            Assert.Equal(offsets[i], header.HoverBox.Bounds.X, 0.5);
            Assert.Equal(widths[i], header.HoverBox.Bounds.Width, 0.5);

            win.MouseDown(p, MouseButton.Left);
            win.MouseUp(p, MouseButton.Left);
            Settle(win);
            Assert.Equal("Genre", vm.SortColumn);
            Assert.True(vm.SortAscending);

            // Again: flips, like a click on the label.
            win.MouseDown(p, MouseButton.Left);
            win.MouseUp(p, MouseButton.Left);
            Settle(win);
            Assert.False(vm.SortAscending);

            // A click on the label itself still sorts once (no double run from the panel).
            var label = InWindow(Cell(header, "Album"), win).Center;
            win.MouseDown(label, MouseButton.Left);
            win.MouseUp(label, MouseButton.Left);
            Settle(win);
            Assert.Equal("Album", vm.SortColumn);
            Assert.True(vm.SortAscending);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void SongsView_DraggingFromAnywhereInTheCell_LiftsAGhostAndShowsWhereItLands()
    {
        var (vm, view, win) = MountSongs(20);
        try
        {
            var header = Header(view);
            var genreLabel = Cell(header, "Genre");
            var labelOpacity = genreLabel.Opacity;
            var p = EmptyPartOfSlot(view, win, "Genre");
            var sortBefore = vm.SortColumn;

            win.MouseDown(p, MouseButton.Left);
            win.MouseMove(p + new Point(-20, 0));
            Settle(win, 2);
            var ghost = header.Ghost;
            var marker = header.DropMarker;
            Assert.True(ghost.IsVisible);
            Assert.Equal(view.Columns.Find("Genre")!.Label, ghost.GetVisualDescendants().OfType<TextBlock>().Single().Text);
            Assert.NotNull(ghost.Background);
            Assert.NotNull(ghost.BorderBrush);
            Assert.True(marker.IsVisible);
            Assert.NotNull(marker.Background); // the bar used to resolve no brush and draw nothing
            Assert.True(marker.Bounds.Height > header.Bounds.Height);
            Assert.True(genreLabel.Opacity < labelOpacity);

            // The ghost follows the pointer.
            var x1 = ghost.Bounds.X;
            win.MouseMove(p + new Point(-120, 0));
            Settle(win, 2);
            Assert.Equal(x1 - 100, ghost.Bounds.X, 0.5);

            // Drop on the far left: the column moves, nothing sorts, the extras go away and the
            // label is back to its own (0.7) opacity, not 1.
            var title = InWindow(Cell(header, "Title"), win);
            var end = new Point(title.X + 5, p.Y);
            win.MouseMove(end);
            win.MouseUp(end, MouseButton.Left);
            Settle(win);
            Assert.Equal("Genre", view.Columns.Visible[0].Key);
            Assert.Equal(sortBefore, vm.SortColumn);
            Assert.False(ghost.IsVisible);
            Assert.False(marker.IsVisible);
            Assert.Equal(labelOpacity, genreLabel.Opacity);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void SongsView_SortArrowInANarrowColumn_StaysClearOfTheDivider()
    {
        var (vm, view, win) = MountSongs(10);
        try
        {
            vm.SortColumn = "Plays";
            Settle(win);
            var header = Header(view);
            var i = view.Columns.Visible.ToList().FindIndex(s => s.Key == "Plays");
            var (widths, offsets) = view.Columns.Compute(header.Bounds.Width);
            Assert.True(header.SortArrow.IsVisible);
            Assert.True(header.SortArrow.Bounds.Right <= offsets[i] + widths[i] - 1.5, $"{header.SortArrow.Bounds} vs {offsets[i] + widths[i]}");
        }
        finally { win.Close(); }
    }

    // ── #156 sort reset ───────────────────────────────────────────────────

    [AvaloniaFact]
    public void SelectSort_Reset_GoesBackToDateAddedNewestFirst()
    {
        var (vm, _, win) = MountSongs(5);
        try
        {
            vm.SelectSortCommand.Execute("Artist");
            vm.SelectSortCommand.Execute("Ascending");
            vm.SelectSortCommand.Execute(LibrarySongsViewModel.ResetSortOption);
            Assert.Equal("Date Added", vm.SortColumn);
            Assert.False(vm.SortAscending);
        }
        finally { win.Close(); }
    }

    [Fact]
    public void SortDot_ShowsForAnyNonDefaultOrder_IncludingOldestFirst()
    {
        var bar = new TopBarViewModel();
        var raised = new List<string?>();
        bar.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.False(bar.IsSongsSortActive); // not reported yet
        bar.PageSortColumn = "Date Added";
        bar.PageSortAscending = false;
        Assert.False(bar.IsSongsSortActive); // the default
        bar.PageSortAscending = true;
        Assert.True(bar.IsSongsSortActive);  // oldest first is a change too
        Assert.Contains(nameof(TopBarViewModel.IsSongsSortActive), raised);
        bar.PageSortColumn = "Title";
        Assert.True(bar.IsSongsSortActive);
        bar.PageSortAscending = false;
        Assert.True(bar.IsSongsSortActive);
    }
}
