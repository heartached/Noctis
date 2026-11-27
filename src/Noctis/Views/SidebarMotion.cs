using Avalonia;
using Avalonia.Animation;
using Easing = Avalonia.Animation.Easings.Easing;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Helpers;

namespace Noctis.Views;

/// <summary>
/// One motion for every sidebar width change: hover expand, the hidden sidebar peeking in,
/// and pin / unpin (setting, Ctrl+B, the drag grip). Owner 11-26: the sidebar animations felt
/// "very choppy and laggy". Causes it fixes:
/// <list type="bullet">
/// <item>The rail's Width animated with the whole sidebar (nav rows, playlist list, trimmed
/// labels) measured and arranged again every frame. While it moves, the content is held at
/// the wider of the two widths, so a frame only re-sizes the rail's frame and clip.</item>
/// <item>Collapsing dropped the labels and re-centred the icons on the first frame, so the
/// icons jumped to the middle of the still-wide rail and slid back. The caller now drops the
/// expanded layout once the rail got narrow (<paramref name="settled"/> of MoveTo).</item>
/// <item>Pin / unpin moved the page's left margin in one step while the rail was still
/// growing or shrinking. The page now takes its new margin at once (one layout) and is
/// slid from where it was with a render transform, on the rail's curve.</item>
/// </list>
/// </summary>
internal sealed class SidebarMotion
{
    public static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(240);
    public static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(200);
    /// <summary>Hover intent: crossing the rail on the way to the window edge does not open it.</summary>
    public static readonly TimeSpan HoverOpenDelay = TimeSpan.FromMilliseconds(70);
    /// <summary>Leaving by a few pixels (or jitter on the rail's edge) does not snap it shut.</summary>
    public static readonly TimeSpan HoverCloseDelay = TimeSpan.FromMilliseconds(180);
    /// <summary>Ease-out (quint-like): quick start, soft settle, both ways.</summary>
    public static readonly Easing Ease = new CubicBezierEase(0.22, 1, 0.36, 1);

    private readonly Border _rail;
    private readonly Control? _content;
    private readonly DoubleTransition _width;
    private readonly Transitions _railTransitions;
    private int _epoch;

    public SidebarMotion(Border rail, Control? content)
    {
        _rail = rail;
        _content = content;
        _width = new DoubleTransition { Property = Layoutable.WidthProperty, Duration = OpenDuration, Easing = Ease };
        _railTransitions = new Transitions { _width };
        _rail.Transitions = _railTransitions;
    }

    /// <summary>Whether the next move plays (off when the rail isn't on screen yet, or with
    /// reduced motion), so startup and hidden states land at once.</summary>
    private bool CanAnimate(Visual v) =>
        v.IsEffectivelyVisible && TopLevel.GetTopLevel(v) is { IsVisible: true }
        && !Noctis.Controls.TransportGlyph.GetReduceMotion(v);

    /// <summary>The rail's target width (not the in-flight animated value).</summary>
    public double TargetWidth => _rail.GetBaseValue(Layoutable.WidthProperty) is { HasValue: true } b ? b.Value : _rail.Width;

    /// <summary>Animate the rail to <paramref name="width"/>; <paramref name="settled"/> runs
    /// once it got there, unless a later move or snap replaced this one.</summary>
    public void MoveTo(double width, Action? settled = null)
    {
        var epoch = ++_epoch;
        var current = _rail.Width; // the animated value while a move is in flight
        if (!CanAnimate(_rail) || current.Equals(width) && TargetWidth.Equals(width))
        {
            Snap(width);
            settled?.Invoke();
            return;
        }
        var opening = width > current;
        _width.Duration = opening ? OpenDuration : CloseDuration;
        if (_content != null)
        {
            // Hold the content at the wider width: only the rail's frame and clip change per frame.
            var hold = Math.Max(current, width);
            if (!double.IsNaN(_content.Width)) hold = Math.Max(hold, _content.Width);
            _content.HorizontalAlignment = HorizontalAlignment.Left;
            _content.Width = hold;
        }
        _rail.Width = width;
        DispatcherTimer.RunOnce(() =>
        {
            if (epoch != _epoch) return;
            ReleaseContent();
            settled?.Invoke();
        }, _width.Duration);
    }

    /// <summary>Set the width with no animation (hidden / shown, startup), cancelling any move.</summary>
    public void Snap(double width)
    {
        ++_epoch;
        _rail.Transitions = null; // drops an in-flight move
        _rail.Width = width;
        _rail.Transitions = _railTransitions;
        ReleaseContent();
    }

    private void ReleaseContent()
    {
        if (_content == null) return;
        _content.Width = double.NaN;
        _content.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    /// <summary>Give <paramref name="page"/> its left margin at once, dropping any slide in
    /// flight (the sidebar hidden / shown, or its edge dragged: the page follows the pointer).</summary>
    public static void PlacePage(Layoutable page, double left)
    {
        var m = page.Margin;
        page.Margin = new Thickness(left, m.Top, m.Right, m.Bottom);
        if (page.RenderTransform is not TranslateTransform t) return;
        var slide = t.Transitions;
        t.Transitions = null;
        t.X = 0;
        t.Transitions = slide;
    }

    /// <summary>Give <paramref name="page"/> its new left margin in one layout, and slide it
    /// there from where it is drawn now with its TranslateTransform, on the rail's curve.</summary>
    public static void SlidePage(Layoutable page, double left, bool opening)
    {
        var drawnAt = page.Margin.Left;
        var t = page.RenderTransform as TranslateTransform;
        if (t != null) drawnAt += t.X;
        var m = page.Margin;
        page.Margin = new Thickness(left, m.Top, m.Right, m.Bottom);
        if (t == null) return;
        var slide = t.Transitions?.OfType<DoubleTransition>().FirstOrDefault(x => x.Property == TranslateTransform.XProperty);
        var saved = t.Transitions;
        t.Transitions = null;
        var animate = page is Visual v && v.IsEffectivelyVisible && TopLevel.GetTopLevel(v) is { IsVisible: true }
                      && !Noctis.Controls.TransportGlyph.GetReduceMotion(v) && !drawnAt.Equals(left);
        t.X = animate ? drawnAt - left : 0;
        t.Transitions = saved;
        if (!animate) return;
        if (slide != null)
        {
            slide.Duration = opening ? OpenDuration : CloseDuration;
            slide.Easing = Ease;
        }
        t.X = 0;
    }
}
