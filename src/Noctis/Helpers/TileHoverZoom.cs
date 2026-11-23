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
/// edge column ran past the scroller's viewport and was clipped (5px of cover at 2400px).</item>
/// <item>Drop the glow where the viewport would cut it (.glow-cut): drawn whole or not at all
/// (Discord 11-21: a clipped glow read as a masked plate behind the tile).</item>
/// </list>
/// </summary>
public sealed class TileHoverZoom
{
    private const double HoverZoomScale = 1.04;
    /// <summary>How far the glow reaches past the cover: AlbumHoverGlow's blur 24 + spread 1
    /// (dark themes; the light themes' 0 8 24 0 shadow reaches 16 up, 24 to the sides).</summary>
    private const double HoverGlowReach = 25;

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
        var growX = w * (HoverZoomScale - 1) / 2;
        var growY = art.Bounds.Height * (HoverZoomScale - 1) / 2;
        var roomLeft = at.X;
        var roomRight = content.Bounds.Width - (at.X + w);
        var left = roomLeft < growX;
        var right = roomRight < growX;
        var x = left == right ? 0.5 : left ? 0 : 1;
        var y = at.Y < growY ? 0 : 0.5;
        art.RenderTransformOrigin = new RelativePoint(x, y, RelativeUnit.Relative);
        tile.Classes.Set("glow-cut", roomLeft < growX + HoverGlowReach || roomRight < growX + HoverGlowReach
            || at.Y < growY + HoverGlowReach);
    }
}
