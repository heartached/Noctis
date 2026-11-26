using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctis.Controls;

namespace Noctis.Helpers;

/// <summary>
/// Album Hover Zoom (GitHub #218, Settings › Appearance) for a tile grid: the Albums page and
/// Favorites (Discord 11-23). The hovered tile's cover grow + glow is pure style (Styles.axaml,
/// .hover-zoom, on the tile's Panel.tile-art); no other tile moves (GitHub #250). What the
/// style cannot do is done here when a tile is entered:
/// <list type="bullet">
/// <item>Lift it above its neighbours. Its .hover-zoom ZIndex sits on the Button, but in the row
/// grid each Button is the only child of a TileRow ContentPresenter, so that ZIndex sorted
/// nothing: the tiles to its right and the row below drew over its grown edge and glow. The
/// presenter and the row's ListBoxItem are lifted instead (left up until the next hover, so
/// the shrink back also plays on top).</item>
/// <item>Grow away from the grid's edges. Grown from its centre, a cover in the first row or an
/// edge column ran past the scroller's viewport and was clipped (5px of cover at 2400px). It also
/// grows away from an edge where its shadow would not fit otherwise.</item>
/// <item>Show the shadow (.glow-fits) only where the viewport would not cut it: drawn whole or not
/// at all (Discord 11-21: a clipped glow read as a masked plate behind the tile). The tag is
/// positive on purpose (Discord 11-25): a .glow-cut tag set here landed after :pointerover had
/// already started the glow, so an edge tile flashed it on its first hover and, the tag staying
/// on, never showed it again. The shadow (AlbumHoverGlow) is small enough to fit the grid's edge
/// room, so edge tiles show it like any other.</item>
/// </list>
/// </summary>
public sealed class TileHoverZoom
{
    private const double HoverZoomScale = 1.04;

    private readonly Func<bool> _isOn;
    private readonly Func<ScrollViewer?> _scroller;
    private readonly List<Visual> _lifted = new();

    /// <param name="isOn">Whether the setting is on.</param>
    /// <param name="scroller">The grid's scroller (its content is what the cover must stay inside).</param>
    public TileHoverZoom(Func<bool> isOn, Func<ScrollViewer?> scroller)
    {
        _isOn = isOn;
        _scroller = scroller;
    }

    /// <summary>Hooks a tile; safe to call again (Loaded can fire again on re-attach).</summary>
    public void Attach(Button tile)
    {
        tile.PointerEntered -= OnEntered;
        tile.PointerEntered += OnEntered;
    }

    private void OnEntered(object? sender, PointerEventArgs e)
    {
        if (!_isOn() || sender is not Button tile) return;
        Lift(tile);
        Anchor(tile);
    }

    private void Lift(Button tile)
    {
        foreach (var v in _lifted) v.ZIndex = 0;
        _lifted.Clear();
        if (tile.GetVisualParent() is ContentPresenter { Parent: TileRow } presenter)
            _lifted.Add(presenter);
        if (tile.FindAncestorOfType<ListBoxItem>() is { } row)
            _lifted.Add(row);
        foreach (var v in _lifted) v.ZIndex = 1;
    }

    private void Anchor(Button tile)
    {
        if (_scroller()?.Content is not Visual content) return;
        if (tile.GetVisualDescendants().OfType<Panel>().FirstOrDefault(p => p.Classes.Contains("tile-art")) is not { } art
            || art.GetVisualParent() is not Visual parent) return;
        // From the parent, so a cover still easing back from a grow is measured untransformed.
        if (parent.TranslatePoint(art.Bounds.Position, content) is not { } at) return;
        var w = art.Bounds.Width;
        var h = art.Bounds.Height;
        var growX = w * (HoverZoomScale - 1) / 2;
        var growY = h * (HoverZoomScale - 1) / 2;
        // How far the shadow reaches past the cover on each side, grown with it.
        double reachLeft = 0, reachRight = 0, reachTop = 0;
        if (art.Children.OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tile-glow")) is { } glow)
            foreach (var s in glow.BoxShadow)
            {
                if (s.IsInset) continue;
                var r = Math.Max(0, s.Blur + s.Spread);
                reachLeft = Math.Max(reachLeft, (r - s.OffsetX) * HoverZoomScale);
                reachRight = Math.Max(reachRight, (r + s.OffsetX) * HoverZoomScale);
                reachTop = Math.Max(reachTop, (r - s.OffsetY) * HoverZoomScale);
            }
        var roomLeft = at.X;
        var roomRight = content.Bounds.Width - (at.X + w);
        var left = roomLeft < growX + reachLeft;
        var right = roomRight < growX + reachRight;
        var x = left == right ? 0.5 : left ? 0 : 1;
        var y = at.Y < growY + reachTop ? 0 : 0.5;
        art.RenderTransformOrigin = new RelativePoint(x, y, RelativeUnit.Relative);
        // How far the grown cover moves past each side with that origin.
        var outLeft = 2 * growX * x;
        var outRight = 2 * growX * (1 - x);
        var outTop = 2 * growY * y;
        const double eps = 0.01;
        tile.Classes.Set("glow-fits", roomLeft + eps >= outLeft + reachLeft && roomRight + eps >= outRight + reachRight
            && at.Y + eps >= outTop + reachTop);
    }
}
