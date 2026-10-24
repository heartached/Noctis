using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Noctis.Helpers;

/// <summary>
/// Marks a ScrollBar with the <c>thumb-dragging</c> class while its thumb is being dragged,
/// so the app-wide bar style (Styles.axaml) can keep the track shown for the whole drag.
/// The track is revealed by <c>ScrollBar:pointerover</c>, and :pointerover follows the
/// pointer even while the thumb holds the capture: drifting sideways off the thin bar
/// mid-drag hid the track under a thumb that was still moving (owner 10-24). Styles cannot
/// reach the track from the thumb (they are siblings in the template), hence the class.
/// One class handler pair covers every ScrollBar, including ones templated in later.
/// </summary>
public static class ScrollBarThumbDrag
{
    public const string DraggingClass = "thumb-dragging";
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        // handledEventsToo: the Track under the bar may mark the thumb's drag events handled.
        Thumb.DragStartedEvent.AddClassHandler<ScrollBar>(
            static (bar, _) => bar.Classes.Set(DraggingClass, true),
            RoutingStrategies.Bubble, handledEventsToo: true);
        // A lost capture (Alt+Tab, a popup) also ends the drag: Thumb raises DragCompleted.
        Thumb.DragCompletedEvent.AddClassHandler<ScrollBar>(
            static (bar, _) => bar.Classes.Set(DraggingClass, false),
            RoutingStrategies.Bubble, handledEventsToo: true);
    }
}
