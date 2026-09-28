using System;
using Avalonia.Controls;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Noctis.Controls;

namespace Noctis.Helpers;

/// <summary>
/// Open/close motion for the queue drawer (QueuePopupPanel), plus its Liquid Glass frost
/// (GitHub #104).
///
/// Glass off: the drawer paints its own QueueDrawerBackground and fades on Opacity, exactly
/// as before; the frost sibling stays hidden and never renders.
///
/// Glass on: a <see cref="GlassPanel"/> sibling UNDER the drawer (QueueGlass, same geometry
/// and transform) frosts the page beneath and the drawer's own fill steps aside
/// (<see cref="GlassClass"/>). The frost is outside the drawer's rounded clip and outside its
/// fading opacity layer: the GPU backend does not apply an ancestor's opacity to the custom
/// Skia blur, so the frost fades on its own <see cref="GlassPanel.Fade"/> in step with the
/// drawer's Opacity — the Settings sheet pattern.
/// </summary>
public static class QueueDrawerGlass
{
    /// <summary>Class on the drawer while the frost carries its fill (MainWindow styles
    /// clear the drawer's Background for it).</summary>
    public const string GlassClass = "glass";

    private const string OpenTransform = "translateX(0px) scale(1)";
    private const string ClosedTransform = "translateX(16px) scale(0.97)";

    /// <summary>How long the close motion runs before the drawer leaves the tree.</summary>
    public static readonly TimeSpan CloseDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Fade + slide/scale settle on open, the reverse on close, then the closed drawer drops
    /// out of the tree so it stops participating in layout/render. <paramref name="stillClosed"/>
    /// re-checks the state when the close timer fires, so a quick re-open never hides an open
    /// drawer.
    /// </summary>
    public static void Animate(Border? panel, GlassPanel? glass, bool open, bool glassActive, Func<bool> stillClosed)
    {
        if (panel == null) return;
        if (open)
        {
            Apply(panel, glass, glassActive);
            // Show first; the settle runs on the next frame so the transitions animate it.
            panel.IsVisible = true;
            if (glass != null) glass.IsVisible = glassActive;
            Dispatcher.UIThread.Post(() =>
            {
                panel.Opacity = 1;
                panel.RenderTransform = TransformOperations.Parse(OpenTransform);
                if (glass != null)
                {
                    glass.Fade = 1;
                    glass.RenderTransform = TransformOperations.Parse(OpenTransform);
                }
            }, DispatcherPriority.Render);
        }
        else
        {
            panel.Opacity = 0;
            panel.RenderTransform = TransformOperations.Parse(ClosedTransform);
            if (glass != null)
            {
                glass.Fade = 0;
                glass.RenderTransform = TransformOperations.Parse(ClosedTransform);
            }
            DispatcherTimer.RunOnce(() =>
            {
                if (!stillClosed()) return;
                panel.IsVisible = false;
                if (glass != null) glass.IsVisible = false;
            }, CloseDelay);
        }
    }

    /// <summary>
    /// Follows a Liquid Glass switch while the drawer is up: the frost appears under an open
    /// drawer (or goes away) and the drawer's own fill steps aside (or comes back).
    /// </summary>
    public static void Apply(Border? panel, GlassPanel? glass, bool glassActive)
    {
        if (panel == null) return;
        panel.Classes.Set(GlassClass, glassActive);
        if (glass == null) return;
        glass.IsVisible = glassActive && panel.IsVisible;
        glass.Fade = panel.IsVisible && panel.Opacity > 0 ? 1 : 0;
        glass.RenderTransform = panel.RenderTransform;
    }
}
