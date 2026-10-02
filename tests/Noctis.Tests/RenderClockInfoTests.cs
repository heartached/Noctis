using Avalonia.Headless.XUnit;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The startup log names the render timer Avalonia picked (WinUiCompositorConnection on
/// Windows' composition path, SleepLoopRenderTimer + Hz on X11, ...). The lookup goes
/// through reflection because IRenderLoop's Timer and AvaloniaLocator.Current are
/// [PrivateApi] — stripped from the reference assembly, present at runtime — so this
/// test pins that the path still resolves against the pinned Avalonia.
/// </summary>
public class RenderClockInfoTests
{
    [AvaloniaFact]
    public void Describe_DoesNotThrowAndNamesATimer()
    {
        var text = RenderClockInfo.Describe();
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain("unknown", text);
        Assert.Contains("Timer", text); // headless registers a *RenderTimer
    }

    /// <summary>
    /// Headless registers HeadlessRenderTimer : DefaultRenderTimer, which — like the
    /// UiThreadRenderTimer that Avalonia.Win32 and Avalonia.X11 fall back to without
    /// composition — exposes its fixed rate as FramesPerSecond, not DesiredFps. A fixed-rate
    /// timer must be logged with its rate, never as "(display-synced)".
    /// </summary>
    [AvaloniaFact]
    public void Describe_NamesTheRateOfAFramesPerSecondTimer()
    {
        var text = RenderClockInfo.Describe();
        Assert.DoesNotContain("unknown", text);
        Assert.Contains("HeadlessRenderTimer", text);
        Assert.EndsWith(" Hz", text);
    }
}
