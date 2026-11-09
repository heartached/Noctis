using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Noctis.Mobile.Views;

/// <summary>
/// Android scroll indicators: a list's bar shows while it moves and fades out a moment after it
/// stops. Fluent's auto-hide bar only shrinks to a thin line and never leaves (store shots,
/// 11-06: the line sat on every page for 20+ s). Every ScrollViewer gets the "scrolling" class
/// while its offset changes; MobileStyles fades the template's bars on that class.
/// </summary>
public static class ScrollBarFade
{
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(1);
    private static readonly Dictionary<ScrollViewer, DispatcherTimer> Timers = new();
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        ScrollViewer.ScrollChangedEvent.AddClassHandler<ScrollViewer>(OnScrollChanged, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static void OnScrollChanged(ScrollViewer viewer, ScrollChangedEventArgs e)
    {
        // Only the viewer's own scrolling (a nested list's event bubbles through its parents),
        // and only a real move: extent or viewport changes while content loads don't flash it.
        if (!ReferenceEquals(e.Source, viewer)) return;
        if (e.OffsetDelta.X == 0 && e.OffsetDelta.Y == 0) return;
        viewer.Classes.Set("scrolling", true);
        if (!Timers.TryGetValue(viewer, out var timer))
        {
            timer = new DispatcherTimer { Interval = Linger };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Timers.Remove(viewer);
                viewer.Classes.Set("scrolling", false);
            };
            Timers[viewer] = timer;
        }
        timer.Stop();
        timer.Start();
    }
}
