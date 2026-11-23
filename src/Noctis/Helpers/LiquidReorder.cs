using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Noctis.Helpers;

/// <summary>
/// The "liquid" drag-to-reorder look shared by the queue, the playlist page and the
/// sidebar playlists. The caller keeps the pointer handling and the commit; this class
/// owns the visuals, all eased by hand in ONE RequestAnimationFrame loop (a Transition
/// restarted by every pointer move stalls, see the per-frame-binding note):
/// <list type="bullet">
/// <item>the floating card lifts (small overshoot), springs after the pointer and
/// stretches a little with its speed;</item>
/// <item>the dragged row's slot stays empty and the rows between it and the target slide
/// one slot over, so the gap opens where the card will land;</item>
/// <item>on release the card glides into the gap, then the caller's commit runs in the same
/// frame the rows snap home (they already sit where the move puts them), and the card
/// stays over the landed row while its opacity fade finishes.</item>
/// </list>
/// The card must use a TransformGroup { ScaleTransform, TranslateTransform } with a 50%
/// origin; its Y is in the coordinates of the panel it is laid out in (top-aligned).
/// Any ItemsControl works (a ListBox, or the mini player's plain queue list): only its
/// realized row containers are moved.
/// </summary>
public sealed class LiquidReorder
{
    private const double CardFollowRate = 26;   // 1/s, card chasing the pointer
    private const double RowSlideRate = 16;     // 1/s, rows opening / closing the gap
    private const double SettleMs = 190;        // release glide into the gap
    private const double LiftScale = 1.035;
    private const double MaxStretch = 0.05;
    private const double LiftSeconds = 0.16;
    /// <summary>Covers the list rows' 0.15s opacity fade when the landed row reappears.</summary>
    private const int CardHoldMs = 160;

    private readonly Visual _host;
    private readonly ItemsControl _list;
    private readonly Border _card;
    private readonly Action? _cardHidden;
    private readonly Dictionary<Control, double> _rowOffsets = new();
    /// <summary>Containers this drag hid (the lifted rows' empty slots). Kept until each is shown
    /// again: a list that parks scrolled-off containers (RetainingVirtualizingStackPanel) reuses
    /// them as they are, so one parked while hidden came back as an invisible row (11-23).</summary>
    private readonly HashSet<Control> _hiddenRows = new();
    private readonly HashSet<Control> _liveRows = new();
    private readonly List<Control> _staleRows = new();

    private bool _frameQueued;
    private TimeSpan? _lastFrame;
    private double _cardY, _cardTargetY, _cardVelocity, _lift;
    private double _settleFromY, _settleToY, _settleElapsedMs;
    private Action? _onLanded;
    private int _generation;

    /// <param name="host">Any attached visual; supplies the TopLevel frame clock.</param>
    /// <param name="list">The list whose realized rows open the gap.</param>
    /// <param name="card">The floating card (see class remarks for its transform).</param>
    /// <param name="cardHidden">Runs when the card is finally hidden (e.g. clear its DataContext).</param>
    public LiquidReorder(Visual host, ItemsControl list, Border card, Action? cardHidden = null)
    {
        _host = host;
        _list = list;
        _card = card;
        _cardHidden = cardHidden;
    }

    /// <summary>Queue index of the dragged row (its slot stays empty), or -1.</summary>
    public int SourceIndex { get; set; } = -1;

    /// <summary>Rows lifted together from <see cref="SourceIndex"/> (a sidebar folder carries
    /// its open playlists); all their slots stay empty. Back to 1 when the drag ends.</summary>
    public int SourceCount { get; set; } = 1;

    /// <summary>
    /// GitHub #246: the rows of a multi-selection dragged together, ascending, any spacing
    /// (null otherwise). While set they are all lifted out (hidden), <see cref="TargetIndex"/>
    /// is where the first of them lands once they are, and the other rows close the holes and
    /// open one gap the block's size there, so no slot inside the block is offered as a drop.
    /// Back to null when the drag ends.
    /// </summary>
    public IReadOnlyList<int>? SourceRows { get; set; }

    /// <summary>Queue index the dragged row (the first of a block) would take if dropped now, or -1.</summary>
    public int TargetIndex { get; set; } = -1;

    /// <summary>Height of one slot: how far the rows between source and target move.</summary>
    public double Pitch { get; set; }

    public bool IsDragging { get; private set; }
    public bool IsSettling { get; private set; }
    public bool IsBusy => IsDragging || IsSettling;

    /// <summary>Card Y right now (eased), in the card's panel coordinates.</summary>
    public double CardY => _cardY;

    /// <summary>Starts a drag with the card sitting exactly over the grabbed row.</summary>
    public void Begin(double cardY)
    {
        if (IsSettling) FinishSettle();
        _generation++;
        EnsureCardTransform();
        _cardY = _cardTargetY = cardY;
        _cardVelocity = 0;
        _lift = 0;
        _lastFrame = null;
        IsDragging = true;
        ApplyCard();
        _card.IsVisible = true;
        RequestFrame();
    }

    /// <summary>Where the pointer wants the card's top.</summary>
    public void MoveCardTo(double cardY)
    {
        _cardTargetY = cardY;
        RequestFrame();
    }

    /// <summary>Release: glide into <paramref name="landingY"/>, then run <paramref name="onLanded"/>
    /// (the caller's commit) as the rows snap home.</summary>
    public void Settle(double landingY, Action onLanded)
    {
        if (!IsDragging)
        {
            onLanded();
            return;
        }
        IsDragging = false;
        IsSettling = true;
        _settleFromY = _cardY;
        _settleToY = landingY;
        _settleElapsedMs = 0;
        _onLanded = onLanded;
        RequestFrame();
    }

    /// <summary>A settle still in flight lands immediately (a new press arrived).</summary>
    public void FinishNow()
    {
        if (IsSettling) FinishSettle();
    }

    /// <summary>Abort: rows snap back, card hides, nothing is committed.</summary>
    public void Cancel()
    {
        IsDragging = false;
        IsSettling = false;
        _onLanded = null;
        SnapRowsHome();
        HideCard();
    }

    /// <summary>
    /// Gap offset for the row at <paramref name="index"/> while the dragged row (from
    /// <paramref name="source"/>) would land at <paramref name="target"/>: the rows in
    /// between move one slot toward the source, everything else stays. A block of
    /// <paramref name="count"/> rows moves as one; <paramref name="pitch"/> is then its height.
    /// </summary>
    public static double GapOffset(int index, int source, int target, double pitch, int count = 1)
    {
        if (source < 0 || target < 0 || (index >= source && index < source + count)) return 0;
        if (source < target && index >= source + count && index < target + count) return -pitch;
        if (target < source && index >= target && index < source) return pitch;
        return 0;
    }

    /// <summary>
    /// Gap offset for the row at <paramref name="index"/> while the rows <paramref name="rows"/>
    /// (ascending, any spacing) are lifted out and would land as one block starting at
    /// <paramref name="landAt"/> (an index in the list without them): the row slides to the
    /// slot the move gives it. The lifted rows themselves stay put (they are hidden).
    /// </summary>
    public static double BlockGapOffset(int index, IReadOnlyList<int> rows, int landAt, double pitch)
    {
        if (landAt < 0 || rows.Count == 0) return 0;
        var below = CountBelow(rows, index);
        if (below < rows.Count && rows[below] == index) return 0;
        var left = index - below;               // its index once the block is lifted out
        var final = left >= landAt ? left + rows.Count : left;
        return (final - index) * pitch;
    }

    /// <summary>
    /// The insertion index in the CURRENT rows (the MoveQueueRows / MoveBlockInQueue contract)
    /// that lands the block <paramref name="rows"/> (ascending) at <paramref name="landAt"/>:
    /// the current index of the row that is <paramref name="landAt"/>-th once the block is out.
    /// </summary>
    public static int InsertIndexFor(IReadOnlyList<int> rows, int landAt)
    {
        var insert = Math.Max(0, landAt);
        foreach (var row in rows)
        {
            if (row <= insert) insert++;
            else break;
        }
        return insert;
    }

    /// <summary>How many of <paramref name="rows"/> (ascending) are below <paramref name="index"/>.</summary>
    private static int CountBelow(IReadOnlyList<int> rows, int index)
    {
        int lo = 0, hi = rows.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (rows[mid] < index) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private bool IsLifted(int index)
    {
        if (SourceRows is { } rows)
        {
            var below = CountBelow(rows, index);
            return below < rows.Count && rows[below] == index;
        }
        return index >= SourceIndex && index < SourceIndex + SourceCount;
    }

    /// <summary>
    /// The row index whose ORIGINAL slot (layout Bounds, before any gap offset) is
    /// nearest <paramref name="probeY"/>, a point in <paramref name="probeSpace"/>
    /// coordinates. <paramref name="slotOf"/> maps a row container to the part that
    /// counts as its slot (e.g. the row body without an album-run header); null = the
    /// whole container. Reading layout rather than rendered positions keeps the pick
    /// stable while the rows are mid-slide.
    /// </summary>
    public static int NearestSlot(ItemsControl list, Visual probeSpace, double probeY, Func<Control, Rect>? slotOf = null)
    {
        var best = -1;
        var bestDist = double.MaxValue;
        foreach (var container in list.GetRealizedContainers())
        {
            if (container.GetVisualParent() is not Visual panel) continue;
            var index = list.IndexFromContainer(container);
            if (index < 0) continue;
            var probe = probeSpace.TranslatePoint(new Point(0, probeY), panel);
            if (probe == null) continue;
            var slot = slotOf?.Invoke(container) ?? new Rect(0, 0, container.Bounds.Width, container.Bounds.Height);
            var centre = container.Bounds.Y + slot.Y + slot.Height / 2;
            var dist = Math.Abs(centre - probe.Value.Y);
            if (dist < bestDist) { bestDist = dist; best = index; }
        }
        return best;
    }

    /// <summary>Top of row <paramref name="index"/>'s original slot in <paramref name="space"/>
    /// coordinates (null when the row isn't realized).</summary>
    public static double? SlotTop(ItemsControl list, int index, Visual space, Func<Control, Rect>? slotOf = null)
    {
        if (list.ContainerFromIndex(index) is not Control container) return null;
        if (container.GetVisualParent() is not Visual panel) return null;
        var slotY = slotOf?.Invoke(container).Y ?? 0;
        return panel.TranslatePoint(new Point(0, container.Bounds.Y + slotY), space)?.Y;
    }

    private void RequestFrame()
    {
        if (_frameQueued) return;
        if (TopLevel.GetTopLevel(_host) is not { } top) return;
        _frameQueued = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        _frameQueued = false;
        if (!IsBusy && _rowOffsets.Count == 0) return;

        var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.05) : 1 / 60.0;
        _lastFrame = now;
        var moving = false;

        if (IsSettling)
        {
            _settleElapsedMs += dt * 1000;
            var t = Math.Clamp(_settleElapsedMs / SettleMs, 0, 1);
            var eased = 1 - Math.Pow(1 - t, 3);
            var previous = _cardY;
            _cardY = _settleFromY + (_settleToY - _settleFromY) * eased;
            _cardVelocity = dt > 0 ? (_cardY - previous) / dt : 0;
            _lift = 1 - eased;
            moving = t < 1;
        }
        else if (IsDragging)
        {
            var previous = _cardY;
            _cardY += (_cardTargetY - _cardY) * (1 - Math.Exp(-CardFollowRate * dt));
            _cardVelocity = dt > 0 ? (_cardY - previous) / dt : 0;
            _lift = Math.Min(1, _lift + dt / LiftSeconds);
            moving = true; // keep ticking while the drag is live
        }
        ApplyCard();

        var rowStep = 1 - Math.Exp(-RowSlideRate * dt);
        var live = _liveRows;
        live.Clear();
        foreach (var item in _list.GetRealizedContainers())
        {
            live.Add(item);
            var index = _list.IndexFromContainer(item);
            // Recycled containers are re-evaluated every frame, so the hole follows the row.
            SetRowHidden(item, IsBusy && index >= 0 && IsLifted(index));
            var goal = !IsBusy ? 0
                : SourceRows is { } block ? BlockGapOffset(index, block, TargetIndex, Pitch)
                : GapOffset(index, SourceIndex, TargetIndex, Pitch, SourceCount);
            _rowOffsets.TryGetValue(item, out var current);
            var next = current + (goal - current) * rowStep;
            if (Math.Abs(goal - next) < 0.3) next = goal; else moving = true;
            SetRowOffset(item, next);
            if (next == 0) _rowOffsets.Remove(item); else _rowOffsets[item] = next;
        }
        _staleRows.Clear();
        foreach (var key in _rowOffsets.Keys)
            if (!live.Contains(key)) _staleRows.Add(key);
        foreach (var stale in _staleRows)
        {
            SetRowOffset(stale, 0);
            _rowOffsets.Remove(stale);
        }
        // A hidden row scrolled out of the realized set (parked or dropped) is shown again
        // now; if its row is still lifted when it comes back, the next frame hides it again.
        _staleRows.Clear();
        foreach (var row in _hiddenRows)
            if (!live.Contains(row)) _staleRows.Add(row);
        foreach (var stale in _staleRows) SetRowHidden(stale, false);
        _staleRows.Clear();
        live.Clear();

        if (IsSettling && !moving)
        {
            FinishSettle();
            return;
        }
        if (moving || _rowOffsets.Count > 0) RequestFrame();
    }

    private void FinishSettle()
    {
        IsSettling = false;
        var commit = _onLanded;
        _onLanded = null;
        SnapRowsHome();
        commit?.Invoke();

        var generation = _generation;
        DispatcherTimer.RunOnce(() =>
        {
            if (generation == _generation && !IsBusy) HideCard();
        }, TimeSpan.FromMilliseconds(CardHoldMs));
    }

    private void SnapRowsHome()
    {
        foreach (var item in _list.GetRealizedContainers())
        {
            item.Opacity = 1;
            SetRowOffset(item, 0);
        }
        // Parked containers too: they are not realized but are reused as they are.
        foreach (var item in _hiddenRows) item.Opacity = 1;
        _hiddenRows.Clear();
        foreach (var item in _rowOffsets.Keys) SetRowOffset(item, 0);
        _rowOffsets.Clear();
        SourceIndex = TargetIndex = -1;
        SourceCount = 1;
        SourceRows = null;
    }

    private void HideCard()
    {
        _card.IsVisible = false;
        _cardHidden?.Invoke();
    }

    private void SetRowHidden(Control item, bool hidden)
    {
        item.Opacity = hidden ? 0 : 1;
        if (hidden) _hiddenRows.Add(item); else _hiddenRows.Remove(item);
    }

    private static void SetRowOffset(Control item, double y)
    {
        if (y == 0)
        {
            if (item.RenderTransform is TranslateTransform) item.ClearValue(Visual.RenderTransformProperty);
            return;
        }
        if (item.RenderTransform is TranslateTransform tt) tt.Y = y;
        else item.RenderTransform = new TranslateTransform(0, y);
    }

    private void EnsureCardTransform()
    {
        if (_card.RenderTransform is TransformGroup g
            && g.Children.OfType<ScaleTransform>().Any()
            && g.Children.OfType<TranslateTransform>().Any())
            return;
        _card.RenderTransformOrigin = RelativePoint.Center;
        _card.RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } };
    }

    /// <summary>Position, lift, and a small velocity stretch (taller and a touch narrower
    /// while it moves fast, relaxing to round when it stops).</summary>
    private void ApplyCard()
    {
        if (_card.RenderTransform is not TransformGroup group) return;
        var scale = group.Children.OfType<ScaleTransform>().FirstOrDefault();
        var move = group.Children.OfType<TranslateTransform>().FirstOrDefault();
        if (scale == null || move == null) return;

        var lift = 1 + (LiftScale - 1) * EaseOutBack(_lift);
        var stretch = Math.Min(MaxStretch, Math.Abs(_cardVelocity) / 6000);
        scale.ScaleX = lift * (1 - stretch * 0.5);
        scale.ScaleY = lift * (1 + stretch);
        move.Y = _cardY;
    }

    private static double EaseOutBack(double t)
    {
        const double c1 = 1.70158, c3 = c1 + 1;
        t = Math.Clamp(t, 0, 1);
        return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
    }
}
