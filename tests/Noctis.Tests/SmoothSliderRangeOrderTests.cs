using Avalonia.Headless.XUnit;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// XAML sets attributes in order, so a SmoothSlider whose Minimum is above the stock
/// Maximum (100) briefly has Minimum &gt; Maximum. Snap must not throw there: the Queue
/// Width slider (300-720) threw from Math.Clamp and Settings never opened.
/// </summary>
public class SmoothSliderRangeOrderTests
{
    [AvaloniaFact]
    public void MinimumAboveStockMaximum_SetFirst_DoesNotThrow_AndStepsInTheFinalRange()
    {
        var slider = new SmoothSlider { Step = 1 };
        slider.Minimum = 300;
        slider.Maximum = 720;
        slider.SteppedValue = 512;

        Assert.Equal(512, slider.Value);

        slider.Value = 640.6;
        Assert.Equal(641, slider.SteppedValue);
    }
}
