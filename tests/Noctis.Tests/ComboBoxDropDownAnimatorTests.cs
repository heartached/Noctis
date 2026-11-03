using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The drop-down animator looks for the Fluent template's PART_Popup and writes the
/// open animation's start state (the menus' pop) onto its child. If the template ever renames the popup
/// the animation silently disappears, so pin the lookup and the start → rest write.
/// </summary>
public class ComboBoxDropDownAnimatorTests
{
    [AvaloniaFact]
    public void Opening_FindsPopupBody_AndAnimatesItIn()
    {
        ComboBoxDropDownAnimator.Install();
        var box = new ComboBox
        {
            ItemsSource = new[] { "Off", "Drift", "Kawarp" }, SelectedIndex = 0,
            Width = 200, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        var win = new Window { Width = 400, Height = 300, Content = box };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();

            var popup = box.GetVisualDescendants().OfType<Popup>().FirstOrDefault(p => p.Name == "PART_Popup");
            Assert.NotNull(popup);
            Assert.NotNull(popup!.Child);

            var closed = 0;
            popup.Closed += (_, _) => closed++;
            box.IsDropDownOpen = true;
            var body = (Control)popup.Child!;
            // The menus' pop (owner 10-29): the start state (clear, 90 %) is written synchronously,
            // the transitions that carry it to rest a frame later.
            Assert.Equal(0, body.Opacity);
            Assert.Equal(0.9, ((Avalonia.Media.Transformation.TransformOperations)body.RenderTransform!).Value.M11, 3);

            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(body.Transitions);
            Assert.Equal(2, body.Transitions!.Count);
            // In flight: the posted rest state is being transitioned to, not snapped.
            Assert.True(body.Opacity < 1, $"opacity {body.Opacity} should still be fading in");

            // The open lands on the resting values. The fade is timed to finish a touch before
            // the transform (so no hard edge shows mid-motion), so wait for both.
            Tick(() => body.Opacity >= 1, TimeSpan.FromSeconds(2));
            Assert.Equal(1, body.Opacity);
            var rest = Avalonia.Media.Transformation.TransformOperations.Parse("scale(1)");
            static bool AtRest(Control c, Avalonia.Matrix target)
            {
                var m = ((Avalonia.Media.Transformation.TransformOperations)c.RenderTransform!).Value;
                return Math.Abs(m.M11 - target.M11) < 1e-3 && Math.Abs(m.M22 - target.M22) < 1e-3
                    && Math.Abs(m.M31 - target.M31) < 1e-3 && Math.Abs(m.M32 - target.M32) < 1e-3;
            }
            Tick(() => AtRest(body, rest.Value), TimeSpan.FromSeconds(2));
            Assert.True(AtRest(body, rest.Value), $"transform still in flight: {((Avalonia.Media.Transformation.TransformOperations)body.RenderTransform!).Value}");

            // Closing via the box (Escape / item click) is held open for the fade-out.
            box.IsDropDownOpen = false;
            AssertFadesOutThenCloses(box, popup, 0, () => closed);

            // Light dismiss (a press on the window outside the box) fades the same way:
            // the popup's own light dismiss is replaced by one routed through the box.
            box.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            Tick(() => ((Control)popup.Child!).Opacity >= 1, TimeSpan.FromSeconds(2));
            Assert.False(popup.IsLightDismissEnabled);
            win.MouseDown(new Point(350, 250), MouseButton.Left);
            AssertFadesOutThenCloses(box, popup, 1, () => closed);

            // A close that comes from the popup itself is left alone: no hold, no re-open.
            box.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            popup.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.False(box.IsDropDownOpen);
            Assert.False(popup.IsOpen);
            Assert.Equal(3, closed);
        }
        finally
        {
            win.Close();
        }
    }

    private static void AssertFadesOutThenCloses(ComboBox box, Popup popup, int closedBefore, Func<int> closedCount)
    {
        Dispatcher.UIThread.RunJobs();
        Assert.True(box.IsDropDownOpen, "close should be held open while the popup fades");
        Assert.True(popup.IsOpen);
        var body = (Control)popup.Child!;
        // Mid-fade: eased, not snapped.
        Tick(() => body.Opacity < 1, TimeSpan.FromSeconds(2));
        Assert.True(body.Opacity > 0 && body.Opacity < 1, $"opacity {body.Opacity} should be mid-fade");
        // The popup host must survive the fade: a close-then-reopen destroys the native
        // popup window and recreates it, which the user sees as a blink (09-08 report).
        Assert.Equal(closedBefore, closedCount());
        Tick(() => !box.IsDropDownOpen, TimeSpan.FromSeconds(2));
        Assert.False(box.IsDropDownOpen);
        Assert.False(popup.IsOpen);
        Assert.Equal(closedBefore + 1, closedCount());
    }

    /// <summary>Headless has no free-running render timer: tick it by hand while real time passes.</summary>
    private static void Tick(Func<bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!done() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(20);
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
