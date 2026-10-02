using System.Reflection;
using Avalonia;
using Avalonia.Rendering;

namespace Noctis.Helpers;

/// <summary>
/// Names the render timer behind Avalonia's frame clock, for the session log: on Windows'
/// default WinUI composition path it is <c>WinUiCompositorConnection</c> (display-synced —
/// the UI already runs at the monitor's refresh rate), on X11 a <c>SleepLoopRenderTimer</c>
/// whose <c>DesiredFps</c> Avalonia sets from the fastest screen. "Choppy UI" reports then
/// say which clock the machine got. Everything here is [PrivateApi]: present at runtime but
/// stripped from the reference assembly, hence reflection, and every failure degrades to
/// an "unknown" string rather than an exception.
/// </summary>
internal static class RenderClockInfo
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static string Describe()
    {
        try
        {
            var locator = typeof(AvaloniaLocator).GetProperty("Current", Any)?.GetValue(null);
            // GetService(Type) may be an explicit interface implementation: match by suffix.
            var getService = locator?.GetType().GetMethods(Any).FirstOrDefault(m =>
                m.Name.EndsWith("GetService", StringComparison.Ordinal)
                && !m.IsGenericMethod
                && m.GetParameters() is { Length: 1 } ps
                && ps[0].ParameterType == typeof(Type));
            var loop = getService?.Invoke(locator, new object[] { typeof(IRenderLoop) });
            var timer = loop?.GetType().GetProperty("Timer", Any)?.GetValue(loop);
            if (timer == null) return "unknown (no render loop timer)";

            var name = timer.GetType().Name;
            return timer.GetType().GetProperty("DesiredFps", Any)?.GetValue(timer) is int fps
                ? $"{name} {fps} Hz"
                : $"{name} (display-synced)";
        }
        catch (Exception ex)
        {
            return $"unknown ({ex.GetType().Name})";
        }
    }
}
