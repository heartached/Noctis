using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;

namespace Noctis.Controls;

/// <summary>
/// A ListBox whose rows can fold open and shut (the sidebar's playlist folders). Rows
/// are ordinary list items, so a folder's playlists appear and vanish as rows being
/// inserted and removed; <see cref="FoldingListBoxItem"/> lets the view ease them in
/// after they are inserted and out before they are removed.
/// </summary>
public class FoldingListBox : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
        => new FoldingListBoxItem();

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
        => NeedsContainer<FoldingListBoxItem>(item, out recycleKey);

    protected override void ClearContainerForItemOverride(Control container)
    {
        base.ClearContainerForItemOverride(container);
        (container as FoldingListBoxItem)?.ResetFold();
    }
}

/// <summary>
/// A list row that folds itself like the Settings sub-menus (<see cref="CollapsibleContent"/>
/// Glide): the same clip-not-relayout fold, early fade and slide, on one symmetric timing.
/// </summary>
/// <remarks>
/// Driven per frame from here rather than by a Transitions entry: the row's style owns
/// Transitions (hover wash, opacity), and a local collection would replace them. The
/// fade and slide go on the presenter for the same reason — LiquidReorder writes the
/// row's own Opacity and RenderTransform while dragging.
/// </remarks>
public class FoldingListBoxItem : ListBoxItem
{
    protected override Type StyleKeyOverride => typeof(ListBoxItem);

    /// <summary>
    /// One timing for the whole folder fold, both directions, shared with the chevron
    /// (SidebarView.axaml) so rows and arrow move as one: quick off the mark, settling
    /// without overshoot.
    /// </summary>
    public static readonly TimeSpan FoldDuration = TimeSpan.FromMilliseconds(280);
    public static readonly Easing FoldEase = new Noctis.Helpers.CubicBezierEase(0.25, 0.8, 0.25, 1.0);

    /// <summary>Pixels the row glides down from as it opens, as the Settings blocks do.</summary>
    private const double Lift = 10;

    /// <summary>0 shut, 1 open.</summary>
    private double _reveal = 1;

    /// <summary>Natural (unfolded) size of the row, from the last measure.</summary>
    private Size _natural;

    /// <summary>The style's margin, scaled with the reveal so a shut row leaves no gap.</summary>
    private Thickness _restMargin;

    private bool _folding;
    private double _from;
    private double _to;
    private TimeSpan _duration;
    private Easing _ease = FoldEase;
    private TimeSpan? _start;
    private int _generation;
    private TaskCompletionSource? _done;

    /// <summary>Shuts the row at once; call before it first lays out, then <see cref="Unfold"/>.</summary>
    public void SnapShut()
    {
        _generation++;
        BeginFold();
        SetReveal(0);
    }

    /// <summary>Eases the row open.</summary>
    public Task Unfold() => Animate(1, FoldDuration, FoldEase);

    /// <summary>Eases the row shut; the task ends when it is, so the row can then be removed.</summary>
    public Task Fold() => Animate(0, FoldDuration, FoldEase);

    /// <summary>Back to a plain row (container recycled, or the fold finished open).</summary>
    public void ResetFold()
    {
        _generation++;
        _done?.TrySetResult();
        _done = null;
        _reveal = 1;
        if (_folding)
        {
            _folding = false;
            ClearValue(MarginProperty);
            ClipToBounds = false;
        }
        if (Presenter is { } presenter)
        {
            presenter.ClearValue(OpacityProperty);
            presenter.ClearValue(RenderTransformProperty);
        }
        InvalidateMeasure();
    }

    private Task Animate(double to, TimeSpan duration, Easing ease)
    {
        _done?.TrySetResult();
        _done = new TaskCompletionSource();
        var done = _done;

        if (TopLevel.GetTopLevel(this) is not { } top)
        {
            SetReveal(to);
            Settle();
            return done.Task;
        }

        BeginFold();
        _from = _reveal;
        _to = to;
        _duration = duration;
        _ease = ease;
        _start = null;
        var generation = ++_generation;
        top.RequestAnimationFrame(t => Tick(t, generation, top));
        return done.Task;
    }

    private void Tick(TimeSpan now, int generation, TopLevel top)
    {
        if (generation != _generation) return;
        _start ??= now;
        var t = _duration <= TimeSpan.Zero ? 1 : Math.Clamp((now - _start.Value) / _duration, 0, 1);
        SetReveal(_from + (_to - _from) * _ease.Ease(t));
        if (t < 1) top.RequestAnimationFrame(n => Tick(n, generation, top));
        else Settle();
    }

    /// <summary>Open rows go back to plain rows; shut ones stay shut until removed.</summary>
    private void Settle()
    {
        var done = _done;
        _done = null;
        if (_to >= 1) ResetFold();
        done?.TrySetResult();
    }

    private void BeginFold()
    {
        if (_folding) return;
        _folding = true;
        _restMargin = Margin;
        ClipToBounds = true;
    }

    private void SetReveal(double value)
    {
        _reveal = Math.Clamp(value, 0, 1);
        Margin = new Thickness(_restMargin.Left, _restMargin.Top * _reveal,
            _restMargin.Right, _restMargin.Bottom * _reveal);

        if (Presenter is { } presenter)
        {
            presenter.Opacity = Noctis.Helpers.Easing.SmootherStep(_reveal / CollapsibleContent.GlideFadeWindow);
            var remaining = 1 - _reveal;
            if (presenter.RenderTransform is TranslateTransform tt) tt.Y = -Lift * remaining * remaining;
            else presenter.RenderTransform = new TranslateTransform(0, -Lift * remaining * remaining);
        }
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!_folding) return base.MeasureOverride(availableSize);

        // Natural height every frame (served from the measure cache), reported folded.
        _natural = base.MeasureOverride(availableSize.WithHeight(double.PositiveInfinity));
        return new Size(_natural.Width, _natural.Height * _reveal);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_folding) return base.ArrangeOverride(finalSize);

        // Full height behind the clip, top-anchored: the row slides, it doesn't squash.
        base.ArrangeOverride(finalSize.WithHeight(Math.Max(finalSize.Height, _natural.Height)));
        return finalSize;
    }
}
