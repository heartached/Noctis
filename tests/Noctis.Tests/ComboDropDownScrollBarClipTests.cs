using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-24 (phone photos): Share lyrics > Background, wheel-scrolled to the end, the
/// drop-down's scrollbar ran down past the list's rounded bottom corner and was drawn
/// outside the pill shape; the last row (Slate) showed in the corner too. The list's
/// ScrollViewer filled the rounded PopupBorder edge to edge and nothing clipped it to the
/// radius, so the bar's ends (and rows) sat in the corner zones. Every ComboBox drop-down
/// uses the same Fluent PopupBorder, so this is checked on a plain box too.
/// </summary>
public class ComboDropDownScrollBarClipTests
{
    private readonly ITestOutputHelper _o;
    public ComboDropDownScrollBarClipTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static void Pump(int ms) => PumpUntil(() => false, ms);

    /// <summary>True when the capsule <paramref name="r"/> (ends rounded by half its width)
    /// lies inside the rounded rectangle <paramref name="outer"/> of corner radius
    /// <paramref name="radius"/>.</summary>
    internal static bool CapsuleInsideRoundedRect(Rect r, Rect outer, double radius)
    {
        const double eps = 0.5;
        if (r.X < outer.X - eps || r.Y < outer.Y - eps || r.Right > outer.Right + eps || r.Bottom > outer.Bottom + eps)
            return false;
        double cap = Math.Min(r.Width, r.Height) / 2;
        // The capsule is the union of circles of radius cap along its centre segment; its
        // extreme circles are the ones nearest the corners.
        var centres = r.Height >= r.Width
            ? new[] { new Point(r.Center.X, r.Y + cap), new Point(r.Center.X, r.Bottom - cap) }
            : new[] { new Point(r.X + cap, r.Center.Y), new Point(r.Right - cap, r.Center.Y) };
        foreach (var c in centres)
        {
            // Clamp into the rect shrunk by the corner radius; outside it, the point is in a corner zone.
            double cx = Math.Clamp(c.X, outer.X + radius, outer.Right - radius);
            double cy = Math.Clamp(c.Y, outer.Y + radius, outer.Bottom - radius);
            double d = Math.Sqrt((c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy));
            if (d + cap > radius + eps) return false;
        }
        return true;
    }

    /// <summary>Opens <paramref name="box"/>, scrolls its list to both ends and asserts the
    /// vertical bar (track) stays inside the PopupBorder's rounded shape, or that the border
    /// clips its content to that shape.</summary>
    private void AssertBarInsideRoundedList(ComboBox box, string label)
    {
        var popup = box.GetVisualDescendants().OfType<Popup>().Single(p => p.Name == "PART_Popup");
        box.IsDropDownOpen = true;
        Assert.True(PumpUntil(() => popup.IsOpen && popup.Child is Control { Bounds.Height: > 0 }), $"{label}: the list never opened");
        Pump(400); // let the open animation settle
        var border = Assert.IsType<Border>(popup.Child);
        var sv = border.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(sv.Extent.Height > sv.Viewport.Height + 1, $"{label}: the list must scroll");
        var bar = sv.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        Assert.True(bar.IsVisible && bar.Bounds.Height > 0, $"{label}: no vertical bar");

        double radius = border.CornerRadius.BottomRight;
        var inner = new Rect(border.Bounds.Size).Deflate(border.BorderThickness);
        double innerRadius = Math.Max(0, radius - border.BorderThickness.Right);

        foreach (var end in new[] { "top", "bottom" })
        {
            sv.Offset = new Vector(0, end == "top" ? 0 : sv.Extent.Height - sv.Viewport.Height);
            Pump(80);
            var barRect = new Rect(bar.Bounds.Size);
            var barInBorder = bar.TranslatePoint(default, border)!.Value;
            var track = new Rect(barInBorder, barRect.Size);
            var thumb = bar.GetVisualDescendants().OfType<Thumb>().First();
            // Both corners through TranslatePoint, so the thumb's idle scaleX(0.5) is included.
            var thumbInBorder = new Rect(thumb.TranslatePoint(default, border)!.Value,
                thumb.TranslatePoint(new Point(thumb.Bounds.Width, thumb.Bounds.Height), border)!.Value);
            bool clipsRound = border.ClipToBounds;
            _o.WriteLine($"{label} [{end}] border {border.Bounds.Size} r={radius} pad={border.Padding} clip={border.ClipToBounds} " +
                         $"sv={sv.Bounds} bar={track} barMargin={bar.Margin} thumb={thumbInBorder} offset={sv.Offset.Y:0}/{sv.Extent.Height - sv.Viewport.Height:0}");
            Assert.True(CapsuleInsideRoundedRect(track, inner, innerRadius),
                $"{label} [{end}]: the scrollbar track {track} leaves the list's rounded shape ({inner}, r={innerRadius})");
            Assert.True(CapsuleInsideRoundedRect(thumbInBorder, inner, innerRadius),
                $"{label} [{end}]: the thumb {thumbInBorder} leaves the list's rounded shape ({inner}, r={innerRadius})");
            Assert.True(clipsRound, $"{label}: rows are not clipped to the rounded list (PopupBorder.ClipToBounds is off)");
        }
        box.IsDropDownOpen = false;
        Pump(300);
    }

    [AvaloniaFact]
    public void ShareLyricsBackgroundList_ScrollBarStaysInsideRoundedList()
    {
        EnsureAppStyles();
        var vm = new LyricShareViewModel(new Noctis.Models.Track
        {
            Id = Guid.NewGuid(), Title = "TUTU", Artist = "6ix9ine", AlbumArtist = "6ix9ine", Album = "A",
        }, new[] { "one", "two" }, 0);
        var win = new Noctis.Views.LyricShareDialog(vm) { Width = 1280, Height = 860 };
        win.Show();
        try
        {
            AssertBarInsideRoundedList(win.FindControl<ComboBox>("BackgroundBox")!, "Share lyrics Background");
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    [AvaloniaFact]
    public void PlainComboList_ScrollBarStaysInsideRoundedList()
    {
        EnsureAppStyles();
        var box = new ComboBox
        {
            ItemsSource = Enumerable.Range(1, 40).Select(i => $"Language {i}").ToArray(),
            SelectedIndex = 0, Width = 220, MaxDropDownHeight = 240,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        var win = new Window { Width = 500, Height = 600, Content = box };
        win.Show();
        try { AssertBarInsideRoundedList(box, "plain ComboBox"); }
        finally { win.Close(); }
    }
}
