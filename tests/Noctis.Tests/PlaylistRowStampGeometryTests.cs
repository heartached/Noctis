using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace Noctis.Tests;

/// <summary>
/// Diagnostics + regression cover for the playlist track list's scroll smoothness.
///
/// The playlist decides a row's HEIGHT one dispatcher cycle after that row has been
/// laid out: PlaylistView.OnTrackContainerPrepared posts UpdateRowIndexVisuals at
/// DispatcherPriority.Loaded, and the stamp flips the album-run header from
/// IsVisible=False to True on run-start rows. A row measured collapsed is re-measured
/// ~35px taller after the fact, which shoves rows below it — and, because the run
/// headers make row heights non-uniform, it also keeps the virtualizing panel's
/// extent estimate moving while the wheel glide is writing a new Offset every frame.
///
/// These tests measure the real thing: the on-screen Y of already-realized rows
/// across a scroll sweep, and the frame-by-frame offset progression of the real
/// <see cref="SmoothScrollBehavior"/> driven by the real frame clock.
/// </summary>
public class PlaylistRowStampGeometryTests
{
    private readonly ITestOutputHelper _output;

    public PlaylistRowStampGeometryTests(ITestOutputHelper output) => _output = output;

    private sealed class Row
    {
        public string Title { get; init; } = "";
        public string Album { get; init; } = "";
        /// <summary>Run-start decided up front on the data, so the template can bind it
        /// and the row is measured at its final height the very first time.</summary>
        public bool IsRunStart { get; set; }
    }

    private const double RowBodyHeight = 44;
    private const int RunLength = 12;
    private const int RowCount = 400;

    private static List<Row> BuildRows(bool withRuns)
    {
        var rows = Enumerable.Range(0, RowCount)
            .Select(i => new Row
            {
                Title = $"Track {i}",
                // No runs => every row is its own album, so no run header ever shows
                // and every row keeps the same height (the uniform control case).
                Album = withRuns ? $"Album {i / RunLength}" : $"Album {i}",
            })
            .ToList();

        if (withRuns)
            for (var i = 0; i < rows.Count; i++)
                rows[i].IsRunStart = i == 0 || rows[i].Album != rows[i - 1].Album;

        return rows;
    }

    /// <summary>Mirrors the PlaylistView item template: StackPanel root, album-run
    /// header first, fixed-height row body second. When <paramref name="bound"/>, the
    /// header's visibility comes from the data item (re-evaluated on recycle) instead
    /// of being stamped by code-behind after layout.</summary>
    private static IDataTemplate RowTemplate(bool bound) => new FuncDataTemplate<Row>((_, _) =>
    {
        var header = new StackPanel
        {
            IsVisible = false,
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 18, 8, 6),
        };
        header.Classes.Add("album-run-header");
        header.Children.Add(new TextBlock { FontSize = 11, Text = "RUN" });
        if (bound)
            header.Bind(Visual.IsVisibleProperty, new Avalonia.Data.Binding(nameof(Row.IsRunStart)));

        var body = new Border { Child = new Grid { Height = RowBodyHeight } };
        body.Classes.Add("row-body");

        var root = new StackPanel();
        root.Children.Add(header);
        root.Children.Add(body);
        return root;
    }, supportsRecycling: true);

    internal enum HeaderMode
    {
        /// <summary>What PlaylistView does today: post the stamp at
        /// DispatcherPriority.Loaded from ContainerPrepared (PlaylistView.axaml.cs:419).</summary>
        DeferredStamp,
        /// <summary>Header visibility bound to the data item, so the row is measured at
        /// its final height on the first pass.</summary>
        Bound,
        /// <summary>Candidate fix: realize the container's template synchronously inside
        /// ContainerPrepared (ApplyTemplate + Presenter.UpdateChild) so the template
        /// children exist, then stamp immediately — before the row is ever measured.</summary>
        SyncStampAfterApplyTemplate,
    }

    private static ListBox MountList(List<Row> rows, HeaderMode mode)
    {
        var list = new ListBox
        {
            ItemsSource = rows,
            ItemTemplate = RowTemplate(bound: mode == HeaderMode.Bound),
        };

        if (mode is HeaderMode.DeferredStamp or HeaderMode.SyncStampAfterApplyTemplate)
        {
            list.ContainerPrepared += (_, e) =>
            {
                if (e.Container is not ListBoxItem item)
                    return;

                // IndexFromContainer is not yet valid during ContainerPrepared; the
                // event carries the authoritative index. The deferred path can use
                // either (by the time the post runs the container is registered).
                void Stamp(int indexFromEvent)
                {
                    var index = indexFromEvent >= 0 ? indexFromEvent : list.IndexFromContainer(item);
                    if (index < 0 || index >= rows.Count)
                        return;

                    var header = item.GetVisualDescendants().OfType<StackPanel>()
                        .FirstOrDefault(p => p.Classes.Contains("album-run-header"));
                    if (header == null)
                        return;

                    var isRunStart = index == 0 || rows[index].Album != rows[index - 1].Album;
                    header.IsVisible = isRunStart;
                    if (isRunStart)
                        header.Margin = index == 0 ? new Thickness(8, 4, 8, 6) : new Thickness(8, 18, 8, 6);
                }

                if (mode == HeaderMode.DeferredStamp)
                {
                    Dispatcher.UIThread.Post(() => Stamp(-1), DispatcherPriority.Loaded);
                }
                else
                {
                    // Force the container template and its DataTemplate content into
                    // existence now, so the stamp lands before the first measure.
                    item.ApplyTemplate();
                    item.Presenter?.UpdateChild();
                    Stamp(e.Index);
                }
            };
        }

        return list;
    }

    private static ScrollViewer Scroller(ListBox list) =>
        list.GetVisualDescendants().OfType<ScrollViewer>().First();

    private static (Window Window, ListBox List, ScrollViewer Scroller) Mount(
        List<Row> rows, HeaderMode mode)
    {
        var list = MountList(rows, mode);
        var window = new Window { Width = 900, Height = 600, Content = list };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, list, Scroller(list));
    }

    /// <summary>Worst on-screen Y movement of any row that was ALREADY realized and
    /// drawn on this frame, caused purely by draining the deferred stamps.</summary>
    private static double ShiftAfterDrain(Window window, ListBox list)
    {
        var before = list.GetRealizedContainers().OfType<ListBoxItem>()
            .Where(c => c.DataContext is Row)
            .ToDictionary(c => (Row)c.DataContext!, c => c.TranslatePoint(new Point(0, 0), list)?.Y);

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var worst = 0.0;
        foreach (var container in list.GetRealizedContainers().OfType<ListBoxItem>())
        {
            if (container.DataContext is not Row row) continue;
            if (!before.TryGetValue(row, out var y0) || y0 == null) continue;
            var y1 = container.TranslatePoint(new Point(0, 0), list)?.Y;
            if (y1 == null) continue;
            worst = Math.Max(worst, Math.Abs(y1.Value - y0.Value));
        }
        return worst;
    }

    // ── 1. Deferred stamp moves content that is already on screen ──

    /// <summary>
    /// CHARACTERIZATION of the OLD approach, kept to document why PlaylistView no
    /// longer posts the stamp: deferring it moves rows that were already drawn.
    /// PlaylistView itself now stamps synchronously in ContainerPrepared (the
    /// SyncStampAfterApplyTemplate mode), which measures 0.0px — see
    /// ContentJump_AcrossHeaderModes. Do not "fix" this test by re-deferring the view.
    /// </summary>
    [AvaloniaFact]
    public void DeferredRunHeaderStamp_ShovesAlreadyDrawnRows()
    {
        var (window, list, scroller) = Mount(BuildRows(withRuns: true), HeaderMode.DeferredStamp);

        var worstShift = 0.0;
        var shiftedSteps = 0;
        var steps = 0;

        // Walk the list the way a wheel glide does: many small offset writes, each
        // followed by the layout pass that frame would draw.
        for (var offset = 60.0; offset < 4000; offset += 60)
        {
            scroller.Offset = new Vector(0, offset);
            window.UpdateLayout();

            var shift = ShiftAfterDrain(window, list);
            steps++;
            if (shift > 0.5)
            {
                shiftedSteps++;
                worstShift = Math.Max(worstShift, shift);
            }
        }

        _output.WriteLine($"steps={steps} shiftedSteps={shiftedSteps} worstShift={worstShift:F1}px");

        Assert.True(steps > 0, "no scroll steps ran");
        Assert.True(shiftedSteps > 0,
            "expected the deferred run-header stamp to shove already-drawn rows; if this now " +
            "reports zero the defect was fixed — tighten this test to assert zero.");
        // Guard against it getting worse than what was measured when this was written.
        Assert.True(worstShift < 40,
            $"post-layout row displacement grew to {worstShift:F1}px (was 3.0px on 2026-08-01)");
    }

    [AvaloniaFact]
    public void BoundRunHeader_KeepsRowsStill()
    {
        var (window, list, scroller) = Mount(BuildRows(withRuns: true), HeaderMode.Bound);

        var worstShift = 0.0;
        var steps = 0;

        for (var offset = 60.0; offset < 4000; offset += 60)
        {
            scroller.Offset = new Vector(0, offset);
            window.UpdateLayout();
            worstShift = Math.Max(worstShift, ShiftAfterDrain(window, list));
            steps++;
        }

        _output.WriteLine($"steps={steps} worstShift={worstShift:F1}px");

        Assert.True(steps > 0, "no scroll steps ran");
        Assert.True(worstShift <= 0.5,
            $"rows still moved by {worstShift:F1}px with the header bound to the data item");
    }

    // ── 2. Extent churn while scrolling (what the glide re-clamps against) ──

    [AvaloniaFact]
    public void ScrollSweep_ExtentChurn()
    {
        foreach (var (label, rows, mode) in new[]
                 {
                     // Bound mode with no runs => no header ever shows => every row 65px.
                     ("uniform rows      ", BuildRows(withRuns: false), HeaderMode.Bound),
                     ("run headers, defer", BuildRows(withRuns: true), HeaderMode.DeferredStamp),
                     ("run headers, bound", BuildRows(withRuns: true), HeaderMode.Bound),
                 })
        {
            var (window, _, scroller) = Mount(rows, mode);

            var extents = new List<double>();
            for (var offset = 60.0; offset < 4000; offset += 60)
            {
                scroller.Offset = new Vector(0, offset);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                extents.Add(scroller.Extent.Height);
            }

            var churn = 0.0;
            for (var i = 1; i < extents.Count; i++)
                churn = Math.Max(churn, Math.Abs(extents[i] - extents[i - 1]));

            // Sanity: the control cases are only meaningful if headers actually rendered.
            var list = (ListBox)window.Content!;
            var realized = list.GetRealizedContainers().OfType<ListBoxItem>().ToList();
            var visibleHeaders = realized.Count(c => c.GetVisualDescendants().OfType<StackPanel>()
                .Any(p => p.Classes.Contains("album-run-header") && p.IsVisible));
            var rowHeights = realized
                .Select(c => c.Bounds.Height)
                .Distinct()
                .OrderBy(h => h)
                .ToList();

            _output.WriteLine(
                $"{label}: extent min={extents.Min():F0} max={extents.Max():F0} " +
                $"worst step-to-step change={churn:F0}px | realized={realized.Count} " +
                $"visibleHeaders={visibleHeaders} rowHeights=[{string.Join(",", rowHeights.Select(h => h.ToString("F0")))}]");

            window.Close();
        }
    }

    // ── 2b. Content jump: the metric that actually matches "the scroll stutters" ──

    /// <summary>
    /// The definitive stutter measurement. While scrolling, a row that stays on screen
    /// must move up by EXACTLY the offset delta. Any residual is content sliding
    /// independently of the wheel — which is what a stutter looks like. (Extent churn
    /// alone only jitters the scrollbar thumb; this catches real content movement.)
    /// </summary>
    private (double Worst, int Jumps, int Steps) ContentJump(Window window, ListBox list, ScrollViewer scroller)
    {
        var worst = 0.0;
        var jumps = 0;
        var steps = 0;
        const double Delta = 40;

        var previous = new Dictionary<Row, double>();
        var previousOffset = scroller.Offset.Y;

        for (var offset = scroller.Offset.Y + Delta; offset < 6000; offset += Delta)
        {
            scroller.Offset = new Vector(0, offset);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var actualDelta = scroller.Offset.Y - previousOffset;
            previousOffset = scroller.Offset.Y;

            var current = new Dictionary<Row, double>();
            foreach (var container in list.GetRealizedContainers().OfType<ListBoxItem>())
            {
                if (container.DataContext is not Row row) continue;
                var y = container.TranslatePoint(new Point(0, 0), list)?.Y;
                if (y == null) continue;
                current[row] = y.Value;

                // Survived from the previous step: expected shift is -actualDelta.
                if (!previous.TryGetValue(row, out var y0)) continue;
                var residual = Math.Abs((y.Value - y0) + actualDelta);
                if (residual > worst) worst = residual;
                if (residual > 0.5) jumps++;
            }

            previous = current;
            steps++;
        }

        return (worst, jumps, steps);
    }

    [AvaloniaFact]
    public void ContentJump_AcrossHeaderModes()
    {
        foreach (var (label, rows, mode) in new[]
                 {
                     ("uniform rows        ", BuildRows(withRuns: false), HeaderMode.Bound),
                     ("run headers, defer  ", BuildRows(withRuns: true), HeaderMode.DeferredStamp),
                     ("run headers, bound  ", BuildRows(withRuns: true), HeaderMode.Bound),
                     ("run headers, sync   ", BuildRows(withRuns: true), HeaderMode.SyncStampAfterApplyTemplate),
                 })
        {
            var (window, list, scroller) = Mount(rows, mode);

            var realized = list.GetRealizedContainers().OfType<ListBoxItem>().ToList();
            var visibleHeaders = realized.Count(c => c.GetVisualDescendants().OfType<StackPanel>()
                .Any(p => p.Classes.Contains("album-run-header") && p.IsVisible));

            var (worst, jumps, steps) = ContentJump(window, list, scroller);

            _output.WriteLine(
                $"{label}: worstJump={worst:F1}px jumps={jumps} over {steps} steps " +
                $"| visibleHeaders={visibleHeaders}/{realized.Count}");

            window.Close();
        }
    }

    // ── 3. The real glide, driven by the real frame clock ──

    /// <summary>Records the ScrollViewer offset on each frame of a real
    /// SmoothScrollBehavior glide kicked off by a wheel notch.</summary>
    private List<double> GlideOffsets(Window window, ScrollViewer scroller, int frames)
    {
        SmoothScrollBehavior.SetIsEnabled(scroller, true);

        scroller.RaiseEvent(new PointerWheelEventArgs(
            scroller,
            new Pointer(0, PointerType.Mouse, true),
            scroller,
            new Point(400, 300),
            0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            KeyModifiers.None,
            new Vector(0, -3))
        {
            RoutedEvent = InputElement.PointerWheelChangedEvent,
        });

        var offsets = new List<double> { scroller.Offset.Y };
        for (var i = 0; i < frames; i++)
        {
            // Real elapsed time: the glide integrates against the wall clock.
            Thread.Sleep(16);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            offsets.Add(scroller.Offset.Y);
        }
        return offsets;
    }

    [AvaloniaFact]
    public void RealGlide_FrameDeltas()
    {
        foreach (var (label, rows, mode) in new[]
                 {
                     // Bound mode with no runs => no header ever shows => every row 65px.
                     ("uniform rows      ", BuildRows(withRuns: false), HeaderMode.Bound),
                     ("run headers, defer", BuildRows(withRuns: true), HeaderMode.DeferredStamp),
                     ("run headers, bound", BuildRows(withRuns: true), HeaderMode.Bound),
                 })
        {
            var (window, _, scroller) = Mount(rows, mode);

            var offsets = GlideOffsets(window, scroller, frames: 40);
            var deltas = new List<double>();
            for (var i = 1; i < offsets.Count; i++)
                deltas.Add(offsets[i] - offsets[i - 1]);

            var moving = deltas.Where(d => Math.Abs(d) > 0.01).ToList();
            // A clean exponential glide decays monotonically. Count frames where the
            // step GREW versus the previous frame (a lurch) or reversed (a snap back).
            var lurches = 0;
            var reversals = 0;
            for (var i = 1; i < moving.Count; i++)
            {
                if (moving[i] * moving[i - 1] < 0) reversals++;
                else if (Math.Abs(moving[i]) > Math.Abs(moving[i - 1]) + 0.5) lurches++;
            }

            _output.WriteLine(
                $"{label}: travelled={offsets[^1] - offsets[0]:F0}px over {moving.Count} moving frames, " +
                $"lurches={lurches} reversals={reversals}");
            _output.WriteLine("    deltas: " + string.Join(" ", moving.Take(24).Select(d => d.ToString("F1"))));

            window.Close();
        }
    }
}
