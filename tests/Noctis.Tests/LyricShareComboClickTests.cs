using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.ViewModels;
using Xunit;
using static Noctis.Tests.LyricShareDialogTests;

namespace Noctis.Tests;

/// <summary>
/// Bug (11-06, live): in Share Lyrics a click on a drop-down's item closed the list and kept
/// the old value (Layout stayed Card after Poster, Format stayed 1:1 after 9:16). A real
/// press + release on the item's row, the way a mouse does it, must pick it.
/// </summary>
public class LyricShareComboClickTests
{
    private static Noctis.Models.Track Song() => new() { Id = Guid.NewGuid(), Title = "TUTU", Artist = "6ix9ine", FilePath = string.Empty };

    [AvaloniaTheory]
    [InlineData("LayoutBox", 1, false)]
    [InlineData("FormatBox", 1, false)]
    [InlineData("TextColorBox", 2, false)]
    [InlineData("BackgroundBox", 2, false)]
    [InlineData("LayoutBox", 1, true)]
    [InlineData("FormatBox", 1, true)]
    [InlineData("TextColorBox", 2, true)]
    [InlineData("BackgroundBox", 2, true)]
    public void ClickingAnItem_PicksIt(string boxName, int target, bool nativePopups)
    {
        EnsureAppStyles();
        ComboBoxDropDownAnimator.Install();
        var vm = new LyricShareViewModel(Song(), Enumerable.Range(0, 6).Select(i => $"line {i}").ToList(), 0);
        var win = new Noctis.Views.LyricShareDialog(vm) { Width = 1280, Height = 860 };
        win.Show();
        using var popups = nativePopups ? new GlassTooltipInPopupTests.NativePopups(win) : null;
        try
        {
            var host = win.GetVisualDescendants().OfType<Noctis.Controls.PillDialogHost>().Single();
            Assert.True(PumpUntil(() => host.Card is { Opacity: >= 1 } card
                && (card.RenderTransform is null || card.RenderTransform.Value.IsIdentity)), "open animation never settled");
            var box = win.FindControl<ComboBox>(boxName)!;
            Assert.NotEqual(target, box.SelectedIndex);

            ClickAndPick(win, box, target);

            Assert.Equal(target, box.SelectedIndex);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    /// <summary>Opens the list with a real click on the box, then presses and releases on the
    /// row of item <paramref name="target"/> in whichever top level the list opened in.</summary>
    internal static void ClickAndPick(TopLevel win, ComboBox box, int target)
    {
        var boxCentre = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), win)!.Value;
        win.MouseMove(boxCentre);
        win.MouseDown(boxCentre, MouseButton.Left);
        win.MouseUp(boxCentre, MouseButton.Left);
        Assert.True(PumpUntil(() => box.IsDropDownOpen), "the list never opened");

        var popup = box.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "PART_Popup");
        Assert.True(PumpUntil(() => popup.IsOpen && popup.Child is Control { Opacity: >= 1 }), "the list never settled");
        var item = (ComboBoxItem)box.ContainerFromIndex(target)!;
        Assert.True(PumpUntil(() => item.IsEffectivelyVisible && item.Bounds.Height > 0), "item not laid out");
        var root = TopLevel.GetTopLevel(item)!;
        var at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), root)!.Value;
        root.MouseMove(at);
        PumpUntil(() => false, 50);
        Assert.True(popup.IsOpen, "the list closed when the pointer moved onto it");
        root.MouseDown(at, MouseButton.Left);
        PumpUntil(() => false, 300);
        Assert.True(box.IsDropDownOpen, "the list closed on the press, before the release could pick the row");
        root.MouseUp(at, MouseButton.Left);
        PumpUntil(() => !popup.IsOpen, 2000);
    }
}
