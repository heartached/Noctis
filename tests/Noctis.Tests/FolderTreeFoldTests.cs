using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Views;
using Xunit;
using Path = Avalonia.Controls.Shapes.Path;

namespace Noctis.Tests;

/// <summary>
/// The Folders page tree folds its folders open and shut (FoldingTreeView) on the sidebar
/// folders' timing instead of snapping: the child rows unfold under a clip, fade and settle,
/// and the chevron turns with them. Restored expansion (the page is rebuilt on every visit)
/// must land at rest, not replay the fold.
/// </summary>
public class FolderTreeFoldTests
{
    private const double Row = 32; // Fluent TreeViewItem MinHeight; the rows here are that tall.

    private readonly ITestOutputHelper _out;
    public FolderTreeFoldTests(ITestOutputHelper output) => _out = output;

    /// <summary>The fold runs on the wall clock, so frames have to be spaced in real time.</summary>
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

    private static FolderNode Folder(string path, int children = 0, bool expanded = false)
    {
        var node = new FolderNode { FullPath = path, DisplayName = System.IO.Path.GetFileName(path), IsExpanded = expanded };
        for (var i = 0; i < children; i++)
            node.Children.Add(new FolderNode { FullPath = $"{path}/Artist {i:D3}", DisplayName = $"Artist {i:D3}" });
        return node;
    }

    private sealed record Page(Window Win, LibraryFoldersView View, FoldingTreeView Tree);

    /// <summary>The real page, tree fed directly: its view model isn't needed for the tree.</summary>
    private static Page Show(IReadOnlyList<FolderNode> roots, double height = 700)
    {
        var view = new LibraryFoldersView();
        var tree = view.FindControl<FoldingTreeView>("FolderTree")!;
        tree.ItemsSource = roots;
        var win = new Window { Width = 900, Height = height, Content = view };
        win.Show();
        Pump(4);
        return new Page(win, view, tree);
    }

    private static FoldingTreeViewItem Item(FoldingTreeView tree, FolderNode node)
        => (FoldingTreeViewItem)tree.ContainerFromItem(node)!;

    private static FoldingTreeViewItem Item(TreeViewItem parent, FolderNode node)
        => (FoldingTreeViewItem)parent.ContainerFromItem(node)!;

    private static ItemsPresenter Children(TreeViewItem item)
        => item.GetVisualDescendants().OfType<ItemsPresenter>().First(p => p.Name == "PART_ItemsPresenter");

    private static ToggleButton Chevron(TreeViewItem item)
        => item.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Name == "PART_ExpandCollapseChevron");

    private static Point Centre(Visual v, Visual space)
        => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), space)!.Value;

    private static void Click(Window win, Control target)
    {
        var p = Centre(target, win);
        win.MouseDown(p, MouseButton.Left);
        win.MouseUp(p, MouseButton.Left);
    }

    /// <summary>Pumps one frame at a time until the row rests, recording each frame.</summary>
    private static List<(double Height, double Angle, bool Folding)> Record(FoldingTreeViewItem item, int maxFrames = 40)
    {
        var frames = new List<(double, double, bool)>();
        for (var i = 0; i < maxFrames; i++)
        {
            Pump(1);
            frames.Add((item.Bounds.Height, item.ChevronAngle, item.IsFolding));
            if (!item.IsFolding && i > 0) break;
        }
        return frames;
    }

    [AvaloniaFact]
    public void ClickingTheChevron_UnfoldsTheChildRows_AndTurnsTheChevronWithThem()
    {
        var root = Folder("C:/musictesting", children: 6);
        var page = Show(new[] { root });
        var item = Item(page.Tree, root);
        Assert.Equal(Row, item.Bounds.Height, 1);
        Assert.Equal(0, item.ChevronAngle, 3);
        var restingClip = item.ClipToBounds;

        Click(page.Win, Chevron(item));
        Assert.True(root.IsExpanded, "the chevron click reaches the model");
        var frames = Record(item);

        var full = Row * 7;
        Assert.Contains(frames, f => f.Height > Row + 1 && f.Height < full - 1);
        Assert.Contains(frames, f => f.Angle > 1 && f.Angle < 89);
        for (var i = 1; i < frames.Count; i++)
            Assert.True(frames[i].Height >= frames[i - 1].Height - 0.01,
                $"opening went backwards at frame {i}: {frames[i - 1].Height:F1} -> {frames[i].Height:F1}");

        Assert.False(item.IsFolding);
        Assert.Equal(full, item.Bounds.Height, 1);
        Assert.Equal(90, item.ChevronAngle, 3);

        // At rest the rows are plain again: no fade, slide or clip left behind.
        var children = Children(item);
        Assert.Equal(1, children.Opacity, 3);
        Assert.Null(children.Clip);
        Assert.Null(children.RenderTransform);
        Assert.Equal(restingClip, item.ClipToBounds);
        _out.WriteLine($"resting ClipToBounds {restingClip}");
    }

    [AvaloniaFact]
    public void Collapsing_KeepsTheChildRowsRealizedAndVisible_UntilTheFoldLands()
    {
        var root = Folder("C:/musictesting", children: 6, expanded: true);
        var page = Show(new[] { root });
        var item = Item(page.Tree, root);
        var children = Children(item);
        Assert.Equal(Row * 7, item.Bounds.Height, 1);

        root.IsExpanded = false;
        Pump(4);

        // Mid-fold: the rows are still there, still drawn, folding behind the clip.
        Assert.True(item.IsFolding);
        Assert.True(children.IsVisible, "the stock template hides the rows the instant IsExpanded drops");
        Assert.Equal(6, item.GetRealizedContainers().Count());
        Assert.InRange(item.Bounds.Height, Row + 1, Row * 7 - 1);
        Assert.True(item.ClipToBounds);

        Record(item);
        Assert.False(item.IsFolding);
        Assert.False(children.IsVisible);
        Assert.Equal(Row, item.Bounds.Height, 1);
        Assert.Equal(0, item.ChevronAngle, 3);
    }

    [AvaloniaFact]
    public void NavigatingBack_RestoresTheExpansionAtRest_WithoutReplayingTheFold()
    {
        var album = Folder("C:/musictesting/Bad Bunny/YHLQMDLG", children: 3, expanded: true);
        var artist = Folder("C:/musictesting/Bad Bunny", children: 2, expanded: true);
        artist.Children.Add(album);
        var root = Folder("C:/musictesting", children: 4, expanded: true);
        root.Children.Add(artist);
        var page = Show(new[] { root });
        Pump(20);

        // Leave the page and come back: the page (and every tree container) is rebuilt and
        // the expansion comes back from the FolderNode models.
        page.Win.Content = null;
        Pump(2);
        var back = new LibraryFoldersView();
        var tree = back.FindControl<FoldingTreeView>("FolderTree")!;
        tree.ItemsSource = new[] { root };
        page.Win.Content = back;

        for (var frame = 0; frame < 6; frame++)
        {
            Pump(1);
            var rootItem = Item(tree, root);
            var artistItem = Item(rootItem, artist);
            var albumItem = Item(artistItem, album);
            foreach (var row in new[] { rootItem, artistItem, albumItem })
            {
                Assert.False(row.IsFolding, $"frame {frame}: a restored folder replayed its fold");
                Assert.Equal(90, row.ChevronAngle, 3);
            }
            Assert.Equal(Row * 4, albumItem.Bounds.Height, 1);
            Assert.Equal(Row * (1 + 2 + 4), artistItem.Bounds.Height, 1);
            Assert.Equal(Row * (1 + 4 + 7), rootItem.Bounds.Height, 1);
        }
    }

    [AvaloniaFact]
    public void ClickingAgainMidFold_TurnsTheFoldAroundFromWhereItIs()
    {
        var root = Folder("C:/musictesting", children: 10);
        var page = Show(new[] { root });
        var item = Item(page.Tree, root);
        var full = Row * 11;

        root.IsExpanded = true;
        Pump(3);
        var before = item.Bounds.Height;
        Assert.InRange(before, Row + 1, full - 1);

        // Turned around mid-fold, the next frame carries on from the current height: one
        // ordinary frame of closing, never a snap open or shut. (A 20 ms frame of the fold
        // curve takes ~23% of the way; even the 50 ms frame cap stays under 60%.)
        root.IsExpanded = false;
        Pump(1);
        var after = item.Bounds.Height;
        Assert.True(after <= before + 0.5 && after >= Row + (before - Row) * 0.3,
            $"reversing went {before:F1} -> {after:F1}px");

        var closing = Record(item);
        for (var i = 1; i < closing.Count; i++)
            Assert.True(closing[i].Height <= closing[i - 1].Height + 0.01,
                $"closing went backwards at frame {i}: {closing[i - 1].Height:F1} -> {closing[i].Height:F1}");
        Assert.Equal(Row, item.Bounds.Height, 1);

        // A burst of clicks a frame apart never strands the row half open.
        for (var i = 0; i < 5; i++)
        {
            root.IsExpanded = !root.IsExpanded;
            Pump(1);
        }
        Assert.True(root.IsExpanded);
        Record(item);
        Assert.False(item.IsFolding);
        Assert.Equal(full, item.Bounds.Height, 1);
        Assert.Equal(90, item.ChevronAngle, 3);
    }

    [AvaloniaFact]
    public void NestedFolders_FoldAtEveryDepth()
    {
        var album = Folder("C:/musictesting/Bad Bunny/YHLQMDLG", children: 3);
        var artist = Folder("C:/musictesting/Bad Bunny", children: 2);
        artist.Children.Add(album);
        var root = Folder("C:/musictesting", children: 2, expanded: true);
        root.Children.Add(artist);
        var page = Show(new[] { root });
        var rootItem = Item(page.Tree, root);
        var artistItem = Item(rootItem, artist);

        artist.IsExpanded = true;
        Pump(4);
        Assert.True(artistItem.IsFolding);
        // The parent grows with it, frame by frame (rows below just move).
        Assert.InRange(rootItem.Bounds.Height, Row * 4 + 1, Row * 7 - 1);
        Record(artistItem);
        Assert.Equal(Row * 7, rootItem.Bounds.Height, 1);

        var albumItem = Item(artistItem, album);
        album.IsExpanded = true;
        Pump(4);
        Assert.True(albumItem.IsFolding);
        Assert.InRange(albumItem.Bounds.Height, Row + 1, Row * 4 - 1);
        Record(albumItem);
        Assert.Equal(Row * 10, rootItem.Bounds.Height, 1);
    }

    [AvaloniaFact]
    public void KeyboardAndDoubleClick_ToggleThroughTheFold()
    {
        var root = Folder("C:/musictesting", children: 5);
        var page = Show(new[] { root });
        var item = Item(page.Tree, root);
        item.Focus();
        Pump(1);

        void Key(Key key, PhysicalKey physical)
        {
            page.Win.KeyPress(key, RawInputModifiers.None, physical, null);
            page.Win.KeyRelease(key, RawInputModifiers.None, physical, null);
        }

        Key(Avalonia.Input.Key.Right, PhysicalKey.ArrowRight);
        Pump(3);
        Assert.True(root.IsExpanded);
        Assert.True(item.IsFolding, "Right arrow should fold open, not snap");
        Record(item);
        Assert.Equal(Row * 6, item.Bounds.Height, 1);

        Key(Avalonia.Input.Key.Left, PhysicalKey.ArrowLeft);
        Pump(3);
        Assert.False(root.IsExpanded);
        Assert.True(item.IsFolding, "Left arrow should fold shut, not snap");
        Record(item);
        Assert.Equal(Row, item.Bounds.Height, 1);

        Key(Avalonia.Input.Key.Add, PhysicalKey.NumPadAdd);
        Pump(3);
        Assert.True(root.IsExpanded);
        Record(item);
        Key(Avalonia.Input.Key.Subtract, PhysicalKey.NumPadSubtract);
        Pump(3);
        Assert.False(root.IsExpanded);
        Record(item);
        Assert.Equal(Row, item.Bounds.Height, 1);

        // Double-click on the folder name.
        var header = item.GetVisualDescendants().OfType<ContentPresenter>().First(c => c.Name == "PART_HeaderPresenter");
        var p = Centre(header, page.Win);
        page.Win.MouseDown(p, MouseButton.Left);
        page.Win.MouseUp(p, MouseButton.Left);
        page.Win.MouseDown(p, MouseButton.Left);
        page.Win.MouseUp(p, MouseButton.Left);
        Pump(3);
        Assert.True(root.IsExpanded);
        Assert.True(item.IsFolding, "double-click should fold open, not snap");
        Record(item);
        Assert.Equal(Row * 6, item.Bounds.Height, 1);
        // Clicking the row still selects it.
        Assert.Same(root, page.Tree.SelectedItem);
    }

    /// <summary>
    /// The sidebar's chevron fix found the stock PathIcon sat ~1.5px off its button. This one
    /// is drawn symmetric about its box's centre, so it must sit on the row text's centre
    /// line and turn about its own middle.
    /// </summary>
    [AvaloniaFact]
    public void Chevron_IsCentredOnTheFolderName_AndTurnsInPlace()
    {
        var root = Folder("C:/musictesting", children: 2);
        var page = Show(new[] { root });
        var item = Item(page.Tree, root);
        var chevron = Chevron(item);
        var glyph = chevron.GetVisualDescendants().OfType<Path>().Single();
        var header = item.GetVisualDescendants().OfType<ContentPresenter>().First(c => c.Name == "PART_HeaderPresenter");
        var name = header.GetVisualDescendants().OfType<TextBlock>().First();

        var geometry = glyph.Data!.Bounds;
        var glyphCentre = glyph.TranslatePoint(geometry.Center, item)!.Value;
        var nameCentre = Centre(name, item);
        var boxCentre = Centre(chevron, item);
        _out.WriteLine($"glyph centre {glyphCentre}, box centre {boxCentre}, name centre {nameCentre}");

        Assert.Equal(boxCentre.X, glyphCentre.X, 1);
        Assert.Equal(boxCentre.Y, glyphCentre.Y, 1);
        Assert.True(Math.Abs(glyphCentre.Y - nameCentre.Y) <= 0.5,
            $"chevron centre {glyphCentre.Y:F2} vs folder name centre {nameCentre.Y:F2}");

        // Turns about the box centre (the default 50%/50% origin), so it pivots in place.
        Assert.IsType<RotateTransform>(chevron.RenderTransform);
        Assert.Equal(RelativePoint.Center, chevron.RenderTransformOrigin);
        Assert.NotNull(glyph.Stroke);
        Assert.Equal(1.8, glyph.StrokeThickness, 3);
    }

    /// <summary>
    /// A big artist folder (hundreds of rows) must fold as smoothly as a small one: only the
    /// part that can be on screen is animated, and the fold never re-measures a row.
    /// </summary>
    /// <summary>A folder name that counts its own measure passes.</summary>
    private sealed class CountingName : TextBlock
    {
        public static int Measures;
        protected override Type StyleKeyOverride => typeof(TextBlock);
        protected override Size MeasureOverride(Size availableSize)
        {
            Measures++;
            return base.MeasureOverride(availableSize);
        }
    }

    private static T BareTree<T>(FolderNode root, out Window win) where T : TreeView, new()
    {
        var tree = new T
        {
            ItemsSource = new[] { root },
            ItemTemplate = new Avalonia.Controls.Templates.FuncTreeDataTemplate<FolderNode>(
                (n, _) => new CountingName { Text = n.DisplayName }, n => n.Children),
        };
        win = new Window { Width = 300, Height = 700, Content = tree };
        win.Show();
        Pump(4);
        return tree;
    }

    /// <summary>One frame, timed without the real-time spacing.</summary>
    private static double TimedFrame()
    {
        var sw = Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return sw.Elapsed.TotalMilliseconds;
    }

    [AvaloniaFact]
    public void LargeFolder_AnimatesOnlyTheVisiblePart_WithoutRemeasuringRows()
    {
        const int Count = 600;

        // Baseline: the stock tree's instant expand realizes every row in one frame too.
        var stockRoot = Folder("C:/stock", children: Count);
        var stock = BareTree<TreeView>(stockRoot, out var stockWin);
        ((TreeViewItem)stock.ContainerFromItem(stockRoot)!).IsExpanded = true;
        var stockFrame = TimedFrame();
        stockWin.Close();

        var root = Folder("C:/musictesting", children: Count);
        var tree = BareTree<FoldingTreeView>(root, out _);
        var item = Item(tree, root);
        var viewer = tree.GetVisualDescendants().OfType<ScrollViewer>().First();
        var full = Row * (Count + 1);

        item.IsExpanded = true; // bare tree: no model binding
        var foldingAtToggle = item.IsFolding;
        var firstFrame = TimedFrame();
        Assert.True(item.IsFolding, $"folding at toggle {foldingAtToggle}, loaded {item.IsLoaded}, first frame {firstFrame:F0} ms, reveal {item.Reveal:F2}");
        Assert.Equal(Count, item.GetRealizedContainers().Count());
        var measuresBefore = CountingName.Measures;

        var frameTimes = new List<double>();
        var heights = new List<double>();
        for (var i = 0; i < 40 && item.IsFolding; i++)
        {
            Thread.Sleep(16);
            frameTimes.Add(TimedFrame());
            heights.Add(item.Bounds.Height);
        }
        var foldMeasures = CountingName.Measures - measuresBefore;
        Pump(2);

        frameTimes.Sort();
        _out.WriteLine($"{Count} rows: stock instant expand frame {stockFrame:F1} ms; fold first frame (realize) {firstFrame:F1} ms; " +
                       $"fold frames n={frameTimes.Count} median {frameTimes[frameTimes.Count / 2]:F2} ms, max {frameTimes[^1]:F2} ms; " +
                       $"viewport {viewer.Bounds.Height:F0}px, full height {full:F0}px, " +
                       $"tallest mid-fold {heights.Take(heights.Count - 1).DefaultIfEmpty().Max():F0}px, row measures during fold {foldMeasures}");

        // Mid-fold the subtree never grows past the viewport (plus a row of slack): the
        // visible rows get the whole 280 ms instead of sweeping by in a frame or two.
        var midFold = heights.Take(heights.Count - 1).ToList();
        Assert.NotEmpty(midFold);
        Assert.All(midFold, h => Assert.True(h <= viewer.Bounds.Height + Row * 2,
            $"mid-fold height {h:F0}px ran past the viewport ({viewer.Bounds.Height:F0}px)"));
        Assert.Contains(midFold, h => h > Row + 1);
        Assert.Equal(full, item.Bounds.Height, 1);

        // The fold is a clip: no row was re-measured while it ran.
        Assert.Equal(0, foldMeasures);
    }
}
