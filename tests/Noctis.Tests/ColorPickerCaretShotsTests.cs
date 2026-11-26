using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26: the colour picker's hex field caret touched the digits (Avalonia's caret stands
/// at the next glyph's origin, and at the line end 1px back inside the last glyph).
/// CenteredCaret draws our own caret in the middle of the inter-glyph gap. The real HexInput
/// (taken out of a ColorPickerFlyout) with a magenta caret: the native caret is hidden, ours
/// is in the adorner layer, and with real Skia (NOCTIS_TEST_SKIA=1) at caret index 0, 4 and 7
/// of "#E74856" the caret column is at least 1px clear of glyph ink on both sides; 8x PNGs to
/// NOCTIS_CARET_SHOTS_DIR.
/// </summary>
public class ColorPickerCaretShotsTests
{
    private readonly ITestOutputHelper _o;
    public ColorPickerCaretShotsTests(ITestOutputHelper o) => _o = o;

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static (Window win, TextBox hex) Host()
    {
        var picker = new ColorPickerFlyout();
        var hex = picker.FindControl<TextBox>("HexInput")!;
        ((Panel)hex.Parent!).Children.Remove(hex);
        // The picker's own styles (the field draws no border) come along.
        var styles = picker.Styles.ToList();
        picker.Styles.Clear();
        CenteredCaret.SetBrush(hex, new SolidColorBrush(Color.FromRgb(255, 0, 255)));
        var win = new Window
        {
            Width = 220, Height = 80,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = Brushes.Black,
            FontFamily = new FontFamily("avares://Noctis.UI/Assets/Fonts/Inter-SemiBold.ttf#Inter"),
            Content = new Border { Width = 120, Height = 30, Margin = new Thickness(20), HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Child = hex },
        };
        win.Styles.AddRange(styles);
        win.Show();
        hex.Text = "#E74856";
        Pump(100);
        hex.Focus();
        Pump(100);
        return (win, hex);
    }

    [AvaloniaFact]
    public void HexInput_NativeCaretHidden_OwnCaretInTheAdornerLayer()
    {
        var (win, hex) = Host();
        try
        {
            Assert.True(CenteredCaret.GetIsEnabled(hex));
            Assert.True(hex.CaretBrush is ISolidColorBrush { Color.A: 0 }, $"native caret brush {hex.CaretBrush}");
            var layer = AdornerLayer.GetAdornerLayer(hex)!;
            Assert.Single(layer.Children, c => ReferenceEquals(AdornerLayer.GetAdornedElement((Visual)c), hex));
            Assert.Equal(4, hex.LetterSpacing);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public async Task HexInput_Caret_SitsClearOfTheDigits_AtEveryIndex()
    {
        if (!HeadlessTestApp.RealRendering) return;
        var (win, hex) = Host();
        try
        {
            var dir = Environment.GetEnvironmentVariable("NOCTIS_CARET_SHOTS_DIR");
            var box = new Rect(hex.TranslatePoint(default, win)!.Value, hex.Bounds.Size);
            var presenter = hex.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().Single();
            var at = presenter.TranslatePoint(default, win)!.Value;
            for (var i = 0; i < 7; i++)
                _o.WriteLine($"glyph {i} origin {at.X + presenter.TextLayout.HitTestTextPosition(i).X:F2} ink {(CenteredCaret.InkSpan(presenter.TextLayout, i) is { } s ? $"{at.X + s.Left:F2}..{at.X + s.Right:F2}" : "-")} centre {at.X + CenteredCaret.CaretCentreX(presenter.TextLayout, i, 7, presenter.LetterSpacing, 1):F2}");
            foreach (var index in Enumerable.Range(0, 8))
            {
                hex.SelectionStart = hex.SelectionEnd = index;
                hex.CaretIndex = index;
                Pump(60);
                using var ms = new MemoryStream();
                win.CaptureRenderedFrame()!.Save(ms);
                using var shot = SKBitmap.Decode(ms.ToArray());
                int x0 = (int)box.X, x1 = (int)Math.Ceiling(box.Right), y0 = (int)box.Y, y1 = (int)Math.Ceiling(box.Bottom);
                if (!string.IsNullOrEmpty(dir) && index is 0 or 4 or 7)
                {
                    Directory.CreateDirectory(dir);
                    const int zoom = 8;
                    int w = x1 - x0 + 8, h = y1 - y0 + 8;
                    using var big = new SKBitmap(w * zoom, h * zoom);
                    for (var y = 0; y < h; y++)
                        for (var x = 0; x < w; x++)
                        {
                            var c = shot.GetPixel(x0 - 4 + x, y0 - 4 + y);
                            for (var yy = 0; yy < zoom; yy++)
                                for (var xx = 0; xx < zoom; xx++)
                                    big.SetPixel(x * zoom + xx, y * zoom + yy, c);
                        }
                    var file = Path.Combine(dir, $"hex-caret-{index}.png");
                    using var fs = File.Create(file);
                    big.Encode(fs, SKEncodedImageFormat.Png, 100);
                    _o.WriteLine(file);
                }
                static bool IsCaret(SKColor c) => c.Red > 150 && c.Blue > 150 && c.Green < 70 && Math.Abs(c.Red - c.Blue) < 50;
                static bool IsInk(SKColor c) => !IsCaret(c) && c.Red + c.Green + c.Blue > 3 * 24;
                var caretCols = Enumerable.Range(x0, x1 - x0).Where(x => Enumerable.Range(y0, y1 - y0).Any(y => IsCaret(shot.GetPixel(x, y)))).ToList();
                Assert.True(caretCols.Count > 0, $"index {index}: no caret drawn");
                var caretRows = Enumerable.Range(y0, y1 - y0).Where(y => caretCols.Any(x => IsCaret(shot.GetPixel(x, y)))).ToList();
                var inkCols = Enumerable.Range(x0, x1 - x0).Where(x => caretRows.Any(y => IsInk(shot.GetPixel(x, y)))).ToList();
                int cl = caretCols.Min(), cr = caretCols.Max();
                var left = inkCols.Where(x => x < cl).DefaultIfEmpty(-100000).Max();
                var right = inkCols.Where(x => x > cr).DefaultIfEmpty(100000).Min();
                _o.WriteLine($"index {index}: caret cols {cl}..{cr} rows {caretRows.Min()}..{caretRows.Max()} ink left {left} right {right} (box x {box.X})");
                Assert.True(cr - cl <= 1, $"index {index}: caret {cr - cl + 1}px wide");
                Assert.True(cl - left >= 2, $"index {index}: caret column {cl} touches the ink at {left}");
                Assert.True(right - cr >= 2, $"index {index}: caret column {cr} touches the ink at {right}");
                Assert.True(cl >= x0, $"index {index}: caret clipped at the field's left edge");
            }

            // No caret over a selection, nor once the field loses focus.
            bool CaretDrawn()
            {
                Pump(60);
                using var ms = new MemoryStream();
                win.CaptureRenderedFrame()!.Save(ms);
                using var shot = SKBitmap.Decode(ms.ToArray());
                for (var y = (int)box.Y; y < box.Bottom; y++)
                    for (var x = (int)box.X; x < box.Right; x++)
                    {
                        var c = shot.GetPixel(x, y);
                        if (c.Red > 150 && c.Blue > 150 && c.Green < 70) return true;
                    }
                return false;
            }
            hex.SelectionStart = 1;
            hex.SelectionEnd = 4;
            Assert.False(CaretDrawn(), "caret drawn over a selection");
            hex.SelectionStart = hex.SelectionEnd = 3;
            Assert.True(CaretDrawn());
            // Blinks at the TextBox's interval; a caret move shows it solid again at once.
            hex.CaretBlinkInterval = TimeSpan.FromMilliseconds(400);
            hex.CaretIndex = 2;
            Assert.True(CaretDrawn());
            // Yielding pump: headless DispatcherTimers only fire across awaits.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 450)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                await Task.Delay(8);
            }
            Assert.False(CaretDrawn(), "caret did not blink off");
            hex.CaretIndex = 5;
            Assert.True(CaretDrawn(), "a caret move did not restart the blink");

            win.Focus();
            hex.IsEnabled = false;
            Assert.False(hex.IsFocused);
            Assert.False(CaretDrawn(), "caret drawn without focus");
        }
        finally { win.Close(); }
    }
}
