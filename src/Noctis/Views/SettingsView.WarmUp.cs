using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Noctis.Views;

/// <summary>
/// Page warm-up (owner 11-15): a page's first visit used to style, template and lay out every
/// control on the click (Audio ~230-290 ms, Appearance ~130-160 ms headless; best of three).
/// While Settings is open and nobody is touching it, the pages not visited yet are built
/// ahead of the click, one small slice at a time, nearest pages on the rail first.
///
/// Never in one go: building all thirteen pages at once was the ~35-48 MB burst whose
/// gen1/gen2 collections paused the audio thread (GitHub #139, Linux), which is why pages
/// join the tree only when needed (DeferTabPages). Here a slice runs a few ms of work at
/// Background priority at most once a frame, and none runs within <see cref="WarmUpIdle"/>
/// of any input, scroll or page switch, or while Settings is closed.
///
/// A slice is made of steps, each the same work a first visit does, in the same order:
/// <list type="number">
/// <item>attach: the page (emptied) goes back into its slot, then its containers and leaves
/// rejoin it one by one (a leaf is a subtree of at most <see cref="WarmUpLeafSize"/> logical
/// controls), so the same objects end up in the same order under the same parents;</item>
/// <item>templates: the visible controls get their templates, one control per step;</item>
/// <item>layout: each card is measured and arranged with the width the shown page has, so the
/// click's layout pass finds them valid and skips them.</item>
/// </list>
/// The page stays hidden throughout (its IsVisible binding), so the layout manager never
/// touches it afterwards until it is shown. A click (or search) on a page part-way through
/// finishes its attach steps at once and leaves the rest to the normal layout pass.
/// </summary>
public partial class SettingsView
{
    /// <summary>Tests switch it off where the warm-up would change what they measure.</summary>
    internal static bool WarmUpEnabled = true;

    /// <summary>Quiet time after Settings opens (its entrance and blur settle first).</summary>
    internal static readonly TimeSpan WarmUpStartDelay = TimeSpan.FromMilliseconds(600);
    /// <summary>Quiet time after any input, scroll or page switch before the next slice.</summary>
    internal static readonly TimeSpan WarmUpIdle = TimeSpan.FromMilliseconds(250);
    /// <summary>Gap between slices: at most one per frame.</summary>
    internal static readonly TimeSpan WarmUpGap = TimeSpan.FromMilliseconds(16);
    /// <summary>A slice stops taking steps once it has run this long.</summary>
    internal const double WarmUpSliceBudgetMs = 3;
    /// <summary>Largest subtree (logical controls) attached in one step.</summary>
    internal const int WarmUpLeafSize = 8;

    private sealed class WarmPage(string tab, Control page)
    {
        public string Tab { get; } = tab;
        public Control Page { get; } = page;
        public Queue<(Action Run, string Label)> Attach { get; } = new();
        public Queue<Visual>? Templates;
        public List<Control>? Cards;
        public List<(Layoutable Control, double Width, bool Card)>? Measures;
        public int NextMeasure;
    }

    private readonly Dictionary<string, WarmPage> _partialPages = new();
    private WarmPage? _warming;
    private DispatcherTimer? _warmTimer;
    private long _warmNotBefore;

    /// <summary>Tests: record every slice in <see cref="WarmSlicesForTests"/> (off in the app).</summary>
    internal static bool RecordWarmSlices;

    /// <summary>Test hook: one entry per slice (wall ms, UI-thread bytes, GC pause inside it, page,
    /// its longest step).</summary>
    internal List<(double Ms, long Bytes, double GcPauseMs, string Tab, string Step)> WarmSlicesForTests { get; } = new();

    /// <summary>Test hook: pages built so far but not finished.</summary>
    internal IReadOnlyCollection<string> PartialTabsForTests => _partialPages.Keys;

    /// <summary>Test hook: true while pages are still waiting for (part of) their warm-up.</summary>
    internal bool HasWarmUpWorkForTests => _deferredTabPages.Count > 0 || _warming is not null;

    private void InitWarmUp()
    {
        void Note(object? s, RoutedEventArgs e) => NoteWarmUpActivity(WarmUpIdle);
        foreach (var routed in new RoutedEvent[]
                 {
                     PointerPressedEvent, PointerReleasedEvent, PointerMovedEvent, PointerWheelChangedEvent,
                     KeyDownEvent, KeyUpEvent, TextInputEvent,
                 })
            AddHandler(routed, Note, RoutingStrategies.Tunnel, handledEventsToo: true);
        SettingsScrollViewer.ScrollChanged += (_, _) => NoteWarmUpActivity(WarmUpIdle);
    }

    /// <summary>
    /// MainWindow: the Settings sheet is opening. (Closing needs no call: the next slice finds
    /// the view hidden and the warm-up stops until the next open.)
    /// </summary>
    public void OnSheetOpened() => StartWarmUp();

    /// <summary>Holds the next slice back for <paramref name="quiet"/> from now.</summary>
    private void NoteWarmUpActivity(TimeSpan quiet)
    {
        var until = Stopwatch.GetTimestamp() + (long)(quiet.TotalSeconds * Stopwatch.Frequency);
        if (until > _warmNotBefore) _warmNotBefore = until;
    }

    /// <summary>Settings is on screen: the warm-up starts after the open settles.</summary>
    private void StartWarmUp()
    {
        if (!WarmUpEnabled || !HasWarmUpWork()) return;
        NoteWarmUpActivity(WarmUpStartDelay);
        _warmTimer ??= CreateWarmTimer();
        _warmTimer.Interval = WarmUpStartDelay;
        _warmTimer.Start();
    }

    private DispatcherTimer CreateWarmTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background);
        timer.Tick += (_, _) => OnWarmTick();
        return timer;
    }

    private bool HasWarmUpWork() => _deferredTabPages.Count > 0 || _warming is not null;

    private bool CanWarmUp() =>
        WarmUpEnabled && _tabHost is not null && _trackedViewModel is not null
        && IsEffectivelyVisible && TopLevel.GetTopLevel(this) is not null;

    private void OnWarmTick()
    {
        var timer = _warmTimer!;
        if (!CanWarmUp() || !HasWarmUpWork())
        {
            timer.Stop();
            return;
        }
        var wait = _warmNotBefore - Stopwatch.GetTimestamp();
        if (wait > 0)
        {
            timer.Interval = TimeSpan.FromSeconds(Math.Max(wait / (double)Stopwatch.Frequency, WarmUpGap.TotalSeconds));
            return;
        }
        timer.Interval = WarmUpGap;
        if (!RunWarmSlice()) timer.Stop();
    }

    /// <summary>Test hook: one slice now, whatever the idle state. False when nothing is left.</summary>
    internal bool RunWarmSliceForTests() => RunWarmSlice();

    /// <summary>Runs steps for up to <see cref="WarmUpSliceBudgetMs"/>. False when nothing is left.</summary>
    private bool RunWarmSlice()
    {
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        var pause = GC.GetTotalPauseDuration();
        var sw = Stopwatch.StartNew();
        var longest = "";
        var longestMs = -1.0;
        string? tab = null;
        do
        {
            if (_warming is null)
            {
                _warming = BeginNextWarmPage();
                if (_warming is null) break;
            }
            tab = _warming.Tab;
            var before = sw.Elapsed.TotalMilliseconds;
            var step = WarmStep(_warming);
            if (sw.Elapsed.TotalMilliseconds - before > longestMs)
            {
                longestMs = sw.Elapsed.TotalMilliseconds - before;
                longest = step;
            }
            if (step.Length == 0)
            {
                _partialPages.Remove(_warming.Tab);
                _warming = null;
            }
        }
        while (sw.Elapsed.TotalMilliseconds < WarmUpSliceBudgetMs);
        sw.Stop();
        if (RecordWarmSlices && tab is not null)
            WarmSlicesForTests.Add((sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - bytes,
                (GC.GetTotalPauseDuration() - pause).TotalMilliseconds, tab, longest));
        return HasWarmUpWork();
    }

    /// <summary>The unvisited page nearest the shown one on the rail (the next one down on a tie).</summary>
    private WarmPage? BeginNextWarmPage()
    {
        if (_deferredTabPages.Count == 0 || _trackedViewModel is not { } vm) return null;
        var keys = vm.Sections.Select(s => s.Key).ToList();
        var at = Math.Max(0, keys.IndexOf(_shownTab ?? vm.SelectedSettingsTab));
        var tab = _deferredTabPages.Keys
            .OrderBy(k => keys.IndexOf(k) < 0 ? int.MaxValue : Math.Abs(keys.IndexOf(k) - at) * 2 - (keys.IndexOf(k) > at ? 1 : 0))
            .First();
        _deferredTabPages.Remove(tab, out var page);

        var warm = new WarmPage(tab, page!);
        warm.Attach.Enqueue((() => InsertTabPage(warm.Page), "attach"));
        PlanAttach(warm.Page, warm.Attach);
        _partialPages[tab] = warm;
        return warm;
    }

    /// <summary>
    /// Empties <paramref name="container"/> (still out of the tree) and queues putting each child
    /// back, in order; a child too big for one step is emptied the same way and refilled after it
    /// rejoins. Only plain panels and decorators are split: an items control's rows are its own.
    /// </summary>
    private static void PlanAttach(Control container, Queue<(Action Run, string Label)> steps)
    {
        switch (container)
        {
            case Panel panel when panel.Children.Count > 0 && !panel.IsItemsHost:
            {
                var children = panel.Children.ToList();
                panel.Children.Clear();
                foreach (var child in children)
                {
                    steps.Enqueue((() => panel.Children.Add(child), Label("attach", child)));
                    if (IsSplittable(child)) PlanAttach(child, steps);
                }
                break;
            }
            case Decorator { Child: { } child } decorator:
            {
                decorator.Child = null;
                steps.Enqueue((() => decorator.Child = child, Label("attach", child)));
                if (IsSplittable(child)) PlanAttach(child, steps);
                break;
            }
        }
    }

    private static string Label(string step, Visual v) =>
        !RecordWarmSlices ? step
        : v is Control { Name: { Length: > 0 } name } ? $"{step} {v.GetType().Name}#{name}" : $"{step} {v.GetType().Name}";

    private static bool IsSplittable(Control c) =>
        c is Panel { IsItemsHost: false } or Decorator
        && c.GetLogicalDescendants().Take(WarmUpLeafSize + 1).Count() > WarmUpLeafSize;

    /// <summary>One step of <paramref name="warm"/>; its name, or empty once the page is done.</summary>
    private string WarmStep(WarmPage warm)
    {
        if (warm.Attach.TryDequeue(out var attach))
        {
            attach.Run();
            return attach.Label;
        }

        if (warm.Templates is null)
        {
            warm.Cards = warm.Page.GetVisualChildren().OfType<Control>().Where(c => c.IsVisible).ToList();
            warm.Templates = new Queue<Visual>(warm.Cards);
        }
        if (warm.Templates.TryDequeue(out var visual))
        {
            if (visual is Layoutable layoutable) layoutable.ApplyTemplate();
            foreach (var child in visual.GetVisualChildren())
                if (child.IsVisible) warm.Templates.Enqueue(child);
            return Label("template", visual);
        }

        // Each card gets the constraint the page's StackPanel would pass it: the width the
        // shown page was measured with (all pages share the host), unbounded height. Inside a
        // card, the rows of its borders and vertical stacks get theirs the same way and go
        // first (deepest first), so measuring their parent finds them done.
        if (warm.Measures is null && warm.Cards is { } cards
            && TabPageFor(_shownTab) is { } shown && !ReferenceEquals(shown, warm.Page)
            && LayoutInformation.GetPreviousMeasureConstraint(shown) is { } constraint
            && !double.IsInfinity(constraint.Width))
        {
            var width = Math.Max(0, constraint.Width - warm.Page.Margin.Left - warm.Page.Margin.Right);
            warm.Measures = new();
            foreach (var card in cards) PlanMeasure(card, width, warm.Measures, card: true);
        }
        if (warm.Measures is { } measures && warm.NextMeasure < measures.Count)
        {
            var (control, width, isCard) = measures[warm.NextMeasure++];
            control.Measure(new Size(width, double.PositiveInfinity));
            if (isCard) control.Arrange(new Rect(0, 0, width, control.DesiredSize.Height));
            return Label("layout", control);
        }
        return "";
    }

    /// <summary>
    /// Queues <paramref name="control"/>'s measure after its rows', for the width it will be
    /// given. Only borders, decorators, presenters, plain items controls and vertical stacks are
    /// opened up: what they hand a child is known from their own width (margin, size limits,
    /// padding, border); anything else is measured whole.
    /// </summary>
    private static void PlanMeasure(Layoutable control, double width, List<(Layoutable, double, bool)> into, bool card = false)
    {
        if (!control.IsVisible) return;
        var inner = width - control.Margin.Left - control.Margin.Right;
        if (!double.IsNaN(control.Width)) inner = control.Width;
        inner = Math.Clamp(inner, control.MinWidth, Math.Max(control.MinWidth, control.MaxWidth));
        IEnumerable<Layoutable>? rows = control switch
        {
            Border border => Pad(border.Padding + border.BorderThickness, border.Child),
            Noctis.Controls.CollapsibleContent collapsible => collapsible.Child is { } c ? new[] { c } : null,
            Decorator decorator when decorator.GetType() == typeof(Decorator) => Pad(decorator.Padding, decorator.Child),
            StackPanel { Orientation: Orientation.Vertical } stack => stack.Children,
            // A plain items control measures its template (border > presenter > panel) with its
            // own width, and each row's presenter its row less padding.
            ItemsControl items when items.GetType() == typeof(ItemsControl) => items.GetVisualChildren().OfType<Layoutable>(),
            ItemsPresenter presenter => presenter.GetVisualChildren().OfType<Layoutable>(),
            ContentPresenter content => Pad(content.Padding + content.BorderThickness, content.Child),
            _ => null,
        };
        if (rows is not null)
            foreach (var row in rows)
                PlanMeasure(row, Math.Max(0, inner), into);
        into.Add((control, width, card));

        IEnumerable<Layoutable>? Pad(Thickness t, Control? child)
        {
            inner -= t.Left + t.Right;
            return child is null ? null : new[] { child };
        }
    }

    /// <summary>A click or search needs <paramref name="tab"/> now: finish putting it together.</summary>
    private bool FinishPartialPage(string tab)
    {
        if (!_partialPages.Remove(tab, out var warm)) return false;
        while (warm.Attach.TryDequeue(out var attach)) attach.Run();
        if (ReferenceEquals(_warming, warm)) _warming = null;
        return true;
    }

    private void StopWarmUp()
    {
        _warmTimer?.Stop();
    }
}
