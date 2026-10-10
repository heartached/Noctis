using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Luwi, 2026-10-05): seeking near the start of a song kept opening the Mini
/// Player — the art sat flush on the seek line and the line's hit strip was 6px tall, so a
/// press a pixel high landed on the art. The seek strip now reaches up over the art's
/// bottom edge; the art keeps its centre.
/// </summary>
public class PlaybackBarSeekArtGapTests
{
    private static PlayerViewModel MakePlayer() => new(
        new FakeAudioPlayer(), new FakeLibraryService(),
        new TestPersistenceService(), new FakeAnimatedCoverService());

    private static Control? HitAt(Window win, Visual relativeTo, Point local)
    {
        var p = relativeTo.TranslatePoint(local, win)!.Value;
        return win.InputHitTest(p) as Control;
    }

    private static bool IsInside(Control? hit, Control target)
        => hit != null && (ReferenceEquals(hit, target) || hit.GetVisualAncestors().Contains(target));

    private static void EnsureAppStyles()
    {
        // Other tests load the app styles into the shared headless app; load them here too so
        // this test sees the same layout whichever order the suite runs in.
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    [AvaloniaFact]
    public void PressesNearTheSeekLine_SeekInsteadOfOpeningTheMiniPlayer()
    {
        EnsureAppStyles();
        var bar = new PlaybackBarView { DataContext = MakePlayer(), CompactWhenLyricsPageActive = false };
        var win = new Window { Width = 1200, Height = 200, Content = bar };
        try
        {
            win.Show();
            bar.FindControl<Grid>("TrackInfoPanel")!.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            win.UpdateLayout();

            var art = bar.FindControl<Border>("AlbumArtThumb")!;
            var slider = bar.FindControl<Slider>("SeekSlider")!;
            var midX = art.Bounds.Width / 2;
            // The placeholder fill is a theme brush the headless app doesn't load; without
            // a fill the art isn't hit-testable at all.
            art.Background = Avalonia.Media.Brushes.Gray;
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            // The art's middle still opens the Mini Player…
            Assert.True(IsInside(HitAt(win, art, new Point(midX, art.Bounds.Height / 2)), art));
            // …but its bottom edge, and the gap under it, belong to the seek strip.
            Assert.True(IsInside(HitAt(win, art, new Point(midX, art.Bounds.Height - 1)), slider));
            Assert.True(IsInside(HitAt(win, art, new Point(midX, art.Bounds.Height + 1)), slider));
        }
        finally
        {
            win.Close();
        }
    }
}
