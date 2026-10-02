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
}
