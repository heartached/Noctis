using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Noctis.Helpers;

/// <summary>
/// GitHub #184 (Settings › Lyrics › Background Behind the Sidebar): stretches a page's
/// full-bleed backdrop layers left to the window's edge, so the sidebar rail, which overlays
/// the window there, sits on (and with Liquid Glass frosts) the same artwork instead of the
/// bare window column the page's 76 / 236 px left margin leaves.
///
/// The layers are the SAME controls the page already draws (no second blur or video): each
/// gets a negative left margin equal to its parent's layout offset from the window's left
/// edge, and a counter-translate that cancels the sidebar hover slide (MainWindow's RootPanel
/// TranslateTransform), so while the rail expands over the page the backdrop stays put
/// against the window rather than leaving a gap at the left. Only the layers move: the page's
/// content keeps its own layout. Disabled, the layers get their default margin and no
/// transform back, exactly as before.
///
/// The offset is in the page's own coordinates: Interface Size (UiScaleHost, a
/// LayoutTransformControl around the window content) scales the margin with everything else,
/// so the walk stops at the scaled content's root.
/// </summary>
internal sealed class BackdropBleed
{
    private readonly Control[] _layers;
    private readonly Control _origin;
    private readonly List<Visual> _watched = new();
    private readonly List<TranslateTransform> _watchedTransforms = new();
    private readonly List<(Visual Visual, bool Local)> _lifted = new();
    private bool _enabled;
    private bool _attached;
    // True once the watched chain reaches the window (or the scaled content's root).
    private bool _chainComplete;

    /// <param name="origin">The control the layers are laid out in (they fill it).</param>
    /// <param name="layers">The backdrop layers to stretch.</param>
    public BackdropBleed(Control origin, params Control[] layers)
    {
        _origin = origin;
        _layers = layers;
    }

    /// <summary>The left extension applied right now (0 when off or nothing to cover).</summary>
    public double AppliedLeft { get; private set; }

    /// <summary>The counter-translate applied right now (minus the ancestors' slide).</summary>
    public double AppliedShift { get; private set; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Rewatch();
            Refresh();
        }
    }

    /// <summary>Call from the host's OnAttachedToVisualTree.</summary>
    public void Attach()
    {
        _attached = true;
        Rewatch();
        Refresh();
    }

    /// <summary>Call from the host's OnDetachedFromVisualTree.</summary>
    public void Detach()
    {
        _attached = false;
        Unwatch();
        RestoreClips();
    }

    /// <summary>
    /// How far <paramref name="origin"/>'s top-left sits from the window's left edge in layout
    /// (sum of each visual's arranged X, render transforms excluded) and how far the ancestors'
    /// TranslateTransforms slide it on top of that. Stops at the root of the window's scaled
    /// content (child of a LayoutTransformControl) or at the TopLevel.
    /// </summary>
    internal static (double Left, double Shift) Measure(Visual origin)
    {
        double left = 0, shift = 0;
        for (Visual? v = origin; v != null && v is not TopLevel; v = v.GetVisualParent())
        {
            if (v.RenderTransform is TranslateTransform t)
                shift += t.X;
            if (v.GetVisualParent() is LayoutTransformControl) break;
            left += v.Bounds.X;
        }
        return (left, shift);
    }

    private void Refresh()
    {
        var left = 0.0;
        var shift = 0.0;
        if (_enabled && _attached)
        {
            (left, shift) = Measure(_origin);
            // Only ever reach left (a page flush with the window needs nothing).
            if (left < 0.5) left = 0;
        }

        // The stretched layers draw outside their host's bounds, and the page's hosts clip:
        // LyricsView (UserControl) and MainWindow's PageHost (ContentControl) are templated
        // controls, which clip to their bounds by default, so without this the layers were
        // laid out to the window edge but never drawn left of the page (#184 live report).
        if (left > 0) LiftClips();
        else RestoreClips();

        if (left == AppliedLeft && shift == AppliedShift) return;
        AppliedLeft = left;
        AppliedShift = left > 0 ? shift : 0;

        foreach (var layer in _layers)
        {
            if (left > 0)
            {
                layer.Margin = new Thickness(-left, 0, 0, 0);
                if (AppliedShift != 0)
                {
                    if (layer.RenderTransform is TranslateTransform own) own.X = -AppliedShift;
                    else layer.RenderTransform = new TranslateTransform(-AppliedShift, 0);
                }
                else
                {
                    layer.ClearValue(Visual.RenderTransformProperty);
                }
            }
            else
            {
                layer.ClearValue(Layoutable.MarginProperty);
                layer.ClearValue(Visual.RenderTransformProperty);
            }
        }
    }

    /// <summary>Turns off ClipToBounds on the clipping visuals between the layers and the
    /// window while the stretch is on, remembering each one's own value for
    /// <see cref="RestoreClips"/>.</summary>
    private void LiftClips()
    {
        foreach (var v in _watched)
        {
            if (!v.ClipToBounds || _lifted.Exists(l => ReferenceEquals(l.Visual, v))) continue;
            var local = v.IsSet(Visual.ClipToBoundsProperty);
            _lifted.Add((v, local));
            v.ClipToBounds = false;
        }
    }

    private void RestoreClips()
    {
        foreach (var (v, local) in _lifted)
        {
            if (local) v.ClipToBounds = true;
            else v.ClearValue(Visual.ClipToBoundsProperty);
        }
        _lifted.Clear();
    }

    private void Rewatch()
    {
        Unwatch();
        if (!_enabled || !_attached) return;
        for (Visual? v = _origin; v != null && v is not TopLevel; v = v.GetVisualParent())
        {
            v.PropertyChanged += OnAncestorPropertyChanged;
            _watched.Add(v);
            if (v.RenderTransform is TranslateTransform t)
            {
                t.Changed += OnTransformChanged;
                _watchedTransforms.Add(t);
            }
            var parent = v.GetVisualParent();
            if (parent is LayoutTransformControl or TopLevel)
            {
                _chainComplete = true;
                break;
            }
        }
    }

    private void Unwatch()
    {
        foreach (var v in _watched) v.PropertyChanged -= OnAncestorPropertyChanged;
        foreach (var t in _watchedTransforms) t.Changed -= OnTransformChanged;
        _watched.Clear();
        _watchedTransforms.Clear();
        _chainComplete = false;
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.BoundsProperty)
        {
            // Attach runs before the host's template is applied, when the layers' parent
            // has no visual parent yet: pick up the rest of the chain at its first arrange.
            if (!_chainComplete) Rewatch();
            Refresh();
        }
        else if (e.Property == Visual.RenderTransformProperty)
        {
            Rewatch();
            Refresh();
        }
    }

    private void OnTransformChanged(object? sender, EventArgs e) => Refresh();
}
