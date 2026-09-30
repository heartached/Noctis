using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Controls;
using Xunit;

namespace Noctis.Tests;

/// <summary>The Studio's progress fill glides to each update on the frame clock, never backwards.</summary>
public class SmoothProgressBarTests
{
    private static (Window win, SmoothProgressBar bar) Mount()
    {
        var bar = new SmoothProgressBar { Width = 200, Height = 4 };
        var win = new Window { Width = 300, Height = 100, Content = bar };
        win.Show();
        return (win, bar);
    }

    [AvaloniaFact]
    public void Fill_Glides_ToTheValue_WithoutOvershootOrStepsBack()
    {
        var (win, bar) = Mount();
        bar.Value = 0.5;

        Assert.True(bar.Step(16));
        Assert.InRange(bar.Shown, 0.01, 0.1); // a glide, not a jump
        var previous = bar.Shown;
        var frames = 1;
        while (bar.Step(16))
        {
            Assert.True(bar.Shown >= previous);
            Assert.True(bar.Shown <= 0.5);
            previous = bar.Shown;
            frames++;
        }
        Assert.Equal(0.5, bar.Shown);
        Assert.InRange(frames, 20, 90); // settles in well under 1.5 s at 60 fps
        win.Close();
    }

    [AvaloniaFact]
    public void AnUpdateMidGlide_ContinuesFromWhereTheFillIs()
    {
        var (win, bar) = Mount();
        bar.Value = 0.3;
        for (var i = 0; i < 5; i++) bar.Step(16);
        var mid = bar.Shown;

        bar.Value = 0.35; // the next throttled update lands mid-glide
        bar.Step(16);

        Assert.True(bar.Shown > mid);
        Assert.True(bar.Shown < 0.35);
        win.Close();
    }

    [AvaloniaFact]
    public void ALowerValue_IsANewRun_AndSnapsDown()
    {
        var (win, bar) = Mount();
        bar.Value = 0.8;
        while (bar.Step(16)) { }

        bar.Value = 0.1;

        Assert.Equal(0.1, bar.Shown);
        Assert.False(bar.Step(16));
        Assert.Equal(0.1, bar.Shown);
        win.Close();
    }

    [AvaloniaFact]
    public void OffScreen_ItLandsOnTheValue_WithNoFrames()
    {
        var bar = new SmoothProgressBar();
        bar.Value = 0.7;
        Assert.Equal(0.7, bar.Shown);
    }

    [AvaloniaFact]
    public void Indeterminate_KeepsAnimating_AndIgnoresTheValue()
    {
        var (win, bar) = Mount();
        bar.IsIndeterminate = true;
        for (var i = 0; i < 200; i++) Assert.True(bar.Step(16));
        Assert.Equal(0, bar.Shown);
        win.Close();
    }
}
