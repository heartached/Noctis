using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>#179: with Liquid Glass on, a drop-down list further down a scrolled page never
/// showed. The list opens in the main window's overlay layer; ComboBox.PopupOpened brings the
/// selected row into view, the request reached the page's ScrollViewer, the page scrolled, and
/// ComboBoxDropDownAnimator closed the list because its box moved. (Headless has no native
/// popups, so the glass-off case opens in the overlay layer as well.)</summary>
public class GlassComboScroll179Tests
{
    private static void Pump(int n = 3)
    {
        for (var i = 0; i < n; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void DropDown_InScrolledPage_StaysOpen_AndPageKeepsItsOffset(bool glassOn)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        ComboBoxDropDownAnimator.Install();
        var box = new ComboBox
        {
            ItemsSource = new[] { "Title", "Artist", "Album", "Year" }, SelectedIndex = 1, Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var page = new StackPanel
        {
            Children =
            {
                new Border { Height = 900 },
                box,
                new Border { Height = 900 },
            },
        };
        var scroller = new ScrollViewer { Content = page };
        var window = new Window { Width = 800, Height = 600, Content = scroller };
        GlassMenus.SetHost(window, true);
        window.Show();
        Pump();
        try
        {
            if (glassOn) AppGlass.Set(true, Colors.Black, Colors.Black);
            else AppGlass.Clear();
            scroller.Offset = new Vector(0, 700);
            Pump();
            var before = scroller.Offset;

            box.IsDropDownOpen = true;
            Pump(6);

            Assert.True(box.IsDropDownOpen, $"the list closed again (page offset {before} -> {scroller.Offset})");
            Assert.True(GlassMenus.DropDownPopup(box)?.IsOpen == true, "the popup is not open");
            Assert.Equal(before, scroller.Offset);
            box.IsDropDownOpen = false;
            Pump(6);
        }
        finally
        {
            AppGlass.Clear();
            window.Close();
        }
    }
}
