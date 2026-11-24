using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Noctis.Helpers;

/// <summary>
/// Shows the user a row a navigation was made for (the album page opened from the playing
/// song's title): glides the page so the row sits near the middle, then lays a soft wash
/// over it that fades in and out. The wash is one transient adorner, removed when it is done,
/// so nothing stays on the row afterwards.
/// </summary>
public static class RowReveal
{
    internal static readonly TimeSpan ScrollDuration = TimeSpan.FromMilliseconds(420);
    internal static readonly TimeSpan WashIn = TimeSpan.FromMilliseconds(160);
    internal static readonly TimeSpan WashHold = TimeSpan.FromMilliseconds(650);
    internal static readonly TimeSpan WashOut = TimeSpan.FromMilliseconds(900);

    /// <summary>The app's reveal curve (SendToFolderDialog, volume flyout): fast start, soft landing.</summary>
    private static readonly CubicBezierEase Ease = new(0.16, 1.0, 0.3, 1.0);

    /// <summary>The offset that puts a row of <paramref name="rowHeight"/> at
    /// <paramref name="rowTop"/> (content coordinates) a little above the viewport's middle,
    /// clamped to the scrollable range; the current offset when the row is already fully in
    /// view above <paramref name="bottomInset"/> (the floating player).</summary>
    public static double TargetOffset(double rowTop, double rowHeight, double current, double viewport,
        double extent, double bottomInset)
    {
        if (viewport <= 0) return current;
        var visibleBottom = current + viewport - bottomInset;
        if (rowTop >= current && rowTop + rowHeight <= visibleBottom) return current;
        var target = rowTop + rowHeight / 2 - (viewport - bottomInset) * 0.45;
        return Math.Clamp(target, 0, Math.Max(0, extent - viewport));
    }

    /// <summary>Glides <paramref name="viewer"/> to <paramref name="target"/>, then runs
    /// <paramref name="done"/>. A scroll by the user meanwhile (the offset is no longer the one
    /// last set) stops the glide where it is; <paramref name="done"/> still runs.</summary>
    public static void GlideTo(ScrollViewer viewer, double target, Action? done = null)
    {
        var start = viewer.Offset.Y;
        if (Math.Abs(target - start) < 1 || TopLevel.GetTopLevel(viewer) is not { } top)
        {
            viewer.Offset = new Vector(viewer.Offset.X, target);
            done?.Invoke();
            return;
        }

        TimeSpan? t0 = null;
        var last = start;
        void Frame(TimeSpan now)
        {
            t0 ??= now;
            if (Math.Abs(viewer.Offset.Y - last) > 1 || TopLevel.GetTopLevel(viewer) == null)
            {
                done?.Invoke();
                return;
            }
            var p = Math.Clamp((now - t0.Value).TotalMilliseconds / ScrollDuration.TotalMilliseconds, 0, 1);
            last = start + (target - start) * Ease.Ease(p);
            viewer.Offset = new Vector(viewer.Offset.X, last);
            last = viewer.Offset.Y; // the viewer may clamp it
            if (p < 1) top.RequestAnimationFrame(Frame);
            else done?.Invoke();
        }
        top.RequestAnimationFrame(Frame);
    }

    /// <summary>
    /// The same reveal for a virtualized song list (Songs, a playlist; Discord 11-24, sina: the
    /// "Playing from …" link). Waits for a layout pass that lists the song (the page may still be
    /// filling), brings its row into view, glides it near the middle and washes it. Returns an
    /// action that cancels a reveal still waiting.
    /// </summary>
    public static Action RevealInList(ListBox list, Guid trackId, double bottomInset)
    {
        var attempts = 0;
        var done = false;
        EventHandler? handler = null;
        void Stop()
        {
            done = true;
            if (handler != null) list.LayoutUpdated -= handler;
            handler = null;
        }
        handler = (_, _) =>
        {
            if (done) return;
            attempts++;
            var index = IndexOf(list, trackId);
            if (index < 0)
            {
                if (attempts >= 30) Stop();
                return;
            }
            Stop();
            list.ScrollIntoView(index);
            // After the scroll's layout pass, so the row exists and its place is final.
            Dispatcher.UIThread.Post(() =>
            {
                if (list.ContainerFromIndex(index) is not Control row
                    || list.FindDescendantOfType<ScrollViewer>() is not { } sv
                    || sv.Content is not Visual content
                    || row.TranslatePoint(default, content) is not { } top) return;
                var target = TargetOffset(top.Y, row.Bounds.Height, sv.Offset.Y, sv.Viewport.Height,
                    sv.Extent.Height, bottomInset);
                var body = row.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("row-body"));
                GlideTo(sv, target, () =>
                {
                    if (body != null) Flash(body, body.CornerRadius);
                });
            }, DispatcherPriority.Background);
        };
        list.LayoutUpdated += handler;
        list.InvalidateMeasure(); // a list already laid out still gets its pass
        return Stop;
    }

    private static int IndexOf(ListBox list, Guid trackId)
    {
        if (list.ItemsSource is not System.Collections.IEnumerable items) return -1;
        var i = 0;
        foreach (var item in items)
        {
            if (item is Noctis.Models.Track t && t.Id == trackId) return i;
            i++;
        }
        return -1;
    }

    /// <summary>Lays the fading wash over <paramref name="row"/> (its corners follow
    /// <paramref name="cornerRadius"/>). The wash is the theme's text colour, faint, so it
    /// reads on a plain row and on the accent-filled now-playing row alike.</summary>
    public static void Flash(Control row, CornerRadius cornerRadius)
    {
        if (AdornerLayer.GetAdornerLayer(row) == null) return;
        var color = row.TryFindResource("SystemBaseHighColor", row.ActualThemeVariant, out var res) && res is Color c
            ? c
            : Colors.White;
        var wash = new Border
        {
            Background = new SolidColorBrush(color, 0.16),
            CornerRadius = cornerRadius,
            IsHitTestVisible = false,
            Opacity = 0,
            Transitions = new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = WashIn, Easing = Ease },
            },
        };
        AdornerLayer.SetAdorner(row, wash);

        // Next frame, so the transition sees 0 -> 1.
        Dispatcher.UIThread.Post(() => wash.Opacity = 1, DispatcherPriority.Background);
        DispatcherTimer.RunOnce(() =>
        {
            wash.Transitions = new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = WashOut, Easing = Ease },
            };
            wash.Opacity = 0;
            DispatcherTimer.RunOnce(() =>
            {
                if (ReferenceEquals(AdornerLayer.GetAdorner(row), wash))
                    AdornerLayer.SetAdorner(row, null);
            }, WashOut + TimeSpan.FromMilliseconds(50));
        }, WashIn + WashHold);
    }
}
