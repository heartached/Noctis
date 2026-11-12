using Avalonia;
using Avalonia.Controls;

namespace Noctis.Helpers;

/// <summary>
/// Keeps the lines on screen where they are while a long transcript's window of lines moves
/// (LyricsViewModel.LyricsWindowMoving / LyricsWindowMoved): before, it notes where the
/// preferred line (the active one) and every visible line sit in the viewport; after, it lays
/// the list out at once and sets the offset so the first of them still in the list is back at
/// the same height — lines removed above it move the rest up, and the offset follows, in the
/// same UI job, so no frame is drawn in between. Shared by the lyrics page, the side panel
/// and the mini player.
/// </summary>
internal sealed class LyricsWindowAnchor
{
    private readonly List<(object Item, double Y)> _marks = new();

    /// <summary>True between <see cref="Capture"/> and <see cref="Restore"/>: the views ignore
    /// the active-line index changes the move raises.</summary>
    public bool IsMoving { get; private set; }

    /// <summary>The line the view was following when the move began (its scroll target), or null.</summary>
    public object? Preferred { get; private set; }

    public void Capture(ItemsControl items, ScrollViewer viewer, object? preferred)
    {
        IsMoving = true;
        Preferred = preferred;
        _marks.Clear();
        if (preferred != null && items.ContainerFromItem(preferred) is Control own && Top(own, viewer) is { } ownY)
            _marks.Add((preferred, ownY));
        if (items.ItemsPanelRoot is not { } panel) return;
        var height = viewer.Viewport.Height;
        foreach (var child in panel.Children)
        {
            if (Top(child, viewer) is not { } y || y + child.Bounds.Height < 0 || y > height) continue;
            if (items.ItemFromContainer(child) is { } item) _marks.Add((item, y));
        }
    }

    /// <summary>
    /// Restores the noted positions through <paramref name="apply"/> (the view's own offset
    /// setter, so it does not read as a user scroll). False when none of the noted lines is in
    /// the list any more (a seek far away): the caller jumps to the active line instead — the
    /// layout is already done.
    /// </summary>
    public bool Restore(ItemsControl items, ScrollViewer viewer, Action<double> apply)
    {
        IsMoving = false;
        viewer.UpdateLayout();
        try
        {
            foreach (var (item, y) in _marks)
            {
                if (items.ContainerFromItem(item) is not Control container || Top(container, viewer) is not { } now) continue;
                var max = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
                apply(Math.Clamp(viewer.Offset.Y + now - y, 0, max));
                return true;
            }
            return false;
        }
        finally
        {
            _marks.Clear();
        }
    }

    /// <summary>Where the container's top sits in the viewport, without the transient
    /// translate a glide's cascade puts on it.</summary>
    private static double? Top(Control container, ScrollViewer viewer) =>
        container.TranslatePoint(default, viewer) is { } p
            ? p.Y - (container.RenderTransform is Avalonia.Media.TranslateTransform tt ? tt.Y : 0)
            : null;
}
