using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Beta 6: pressing an artist circle let the initials under the photo show through it ("Bs"
/// through Blake Shelton), an album tile its placeholder glyph through the cover, everywhere a
/// picture lies over a placeholder. The press styles dim the button's Opacity, which Avalonia's
/// Skia backend applies to each element inside; PressOpacity has the button dimmed as one picture
/// while it is pressed and while it eases back.
/// </summary>
public class MobilePressDimTests(ITestOutputHelper output)
{
    private static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);   // the transitions run on the wall clock
        }
    }

    /// <summary>A tile as the artist list builds it: the placeholder (red) under the picture (blue),
    /// in a phone row button with MobileStyles' press, among the row's other tiles.</summary>
    private static (Window Window, StackPanel Row, Button Tile) Mount()
    {
        PressOpacity.Install();
        var tile = new Button
        {
            Classes = { "row" },
            Width = 100,
            Height = 100,
            Content = new Panel
            {
                Children =
                {
                    new Border { Background = Brushes.Red },
                    new Border { Background = Brushes.Blue },
                },
            },
        };
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { tile, new Button { Classes = { "row" }, Width = 50 } } };
        var window = new Window { Width = 200, Height = 100, Background = Brushes.White, Content = row };
        window.Styles.Add(new StyleInclude(new Uri("avares://Noctis.Tests")) { Source = new Uri("avares://Noctis.Mobile/Views/MobileStyles.axaml") });
        window.Show();
        Pump(2);
        return (window, row, tile);
    }

    private static Color Centre(Window window)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var ms = new MemoryStream();
        frame.Save(ms);
        ms.Position = 0;
        using var bmp = WriteableBitmap.Decode(ms);
        using var l = bmp.Lock();
        var px = new byte[4];
        System.Runtime.InteropServices.Marshal.Copy(l.Address + 50 * l.RowBytes + 50 * 4, px, 0, 4);
        return Color.FromRgb(px[2], px[1], px[0]);   // BGRA
    }

    [AvaloniaFact]
    public void APressedTile_GroupsItsOpacityOnItsParent_UntilItHasEasedBack()
    {
        var (window, row, tile) = Mount();
        Assert.Null(RenderOptions.GetRequiresFullOpacityHandling(row));

        window.MouseDown(new Point(50, 50), MouseButton.Left);
        Assert.True(tile.IsPressed);
        Assert.True(PressOpacity.IsGrouped(tile));
        Assert.True(RenderOptions.GetRequiresFullOpacityHandling(row));
        Pump(15);
        Assert.Equal(0.6, tile.Opacity, 3);                               // MobileStyles' press dim

        window.MouseUp(new Point(50, 50), MouseButton.Left);
        Assert.False(tile.IsPressed);
        Assert.True(tile.Opacity < 1);                                      // easing back: still grouped
        Assert.True(RenderOptions.GetRequiresFullOpacityHandling(row));
        Pump(20);
        Assert.Equal(1, tile.Opacity, 3);
        Assert.False(PressOpacity.IsGrouped(tile));
        Assert.Null(RenderOptions.GetRequiresFullOpacityHandling(row));    // the parent's own setting back
        window.Close();
    }

    [AvaloniaFact]
    public void TheParentsOwnSetting_IsPutBack_AndALeavingButtonLetsGo()
    {
        var (window, row, tile) = Mount();
        RenderOptions.SetRequiresFullOpacityHandling(row, false);
        window.MouseDown(new Point(50, 50), MouseButton.Left);
        Assert.True(RenderOptions.GetRequiresFullOpacityHandling(row));
        row.Children.Remove(tile);                                          // gone mid-press (the page left)
        Assert.False(PressOpacity.IsGrouped(tile));
        Assert.False(RenderOptions.GetRequiresFullOpacityHandling(row));
        window.Close();
    }

    /// <summary>The pixels (real rendering only: NOCTIS_TEST_SKIA=1): at the press's 60 % the
    /// tile's centre is the picture alone at 60 % over the page, not the picture over a 60 %
    /// placeholder. Blue over red over white per element leaves green at 41; as one picture, 102.</summary>
    [AvaloniaFact]
    public void APressedTile_DimsAsOnePicture_ThePlaceholderNeverShowsThrough()
    {
        if (!HeadlessTestApp.RealRendering) return;
        var (window, _, _) = Mount();
        window.MouseDown(new Point(50, 50), MouseButton.Left);
        Pump(15);
        var pressed = Centre(window);
        output.WriteLine($"pressed centre {pressed}");
        Assert.InRange(pressed.R, 92, 112);
        Assert.InRange(pressed.G, 92, 112);
        Assert.InRange(pressed.B, 245, 255);
        window.MouseUp(new Point(50, 50), MouseButton.Left);
        Pump(20);
        Assert.Equal(Colors.Blue, Centre(window));
        window.Close();
    }
}
