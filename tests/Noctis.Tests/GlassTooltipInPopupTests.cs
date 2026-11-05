using System;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-04 (Edit Info › Artwork / Animated Artwork search): tooltips of controls inside a
/// pop-up must open on top of it, whole, glass on or off. The headless platform hosts every
/// popup in the window's overlay layer, so these tests stand in a native popup (a PopupRoot
/// whose platform popup can open popups of its own, as on Windows) for the chooser flyout.
/// </summary>
public class GlassTooltipInPopupTests
{
    /// <summary>Native popups, as on Windows: the headless platform's OverlayPopups switched off
    /// for the duration (it is the one options object every headless window shares).</summary>
    internal sealed class NativePopups : IDisposable
    {
        private readonly Avalonia.Headless.AvaloniaHeadlessPlatformOptions _options;
        private readonly bool _was;

        public NativePopups(Window win)
        {
            var impl = win.PlatformImpl!;
            _options = (Avalonia.Headless.AvaloniaHeadlessPlatformOptions)impl.GetType()
                .GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(impl)!;
            _was = _options.OverlayPopups;
            _options.OverlayPopups = false;
        }

        public void Dispose() => _options.OverlayPopups = _was;
    }

    private static PopupRoot NativePopup(Window win, Control content, out Flyout flyout)
    {
        var anchor = (Control)win.Content!;
        flyout = new Flyout { Content = content, Placement = PlacementMode.Center };
        flyout.ShowAt(anchor);
        Dispatcher.UIThread.RunJobs();
        return Assert.IsType<PopupRoot>(TopLevel.GetTopLevel(content));
    }

    private sealed record Rig(Window Win, NativePopups Native, Flyout Flyout, PopupRoot Root, Button Owner, ToolTip Tip) : IDisposable
    {
        public void Dispose()
        {
            ToolTip.SetIsOpen(Owner, false);
            AppGlass.Clear();
            Flyout.Hide();
            Dispatcher.UIThread.RunJobs();
            Native.Dispose();
            Win.Close();
        }
    }

    private static Rig Setup(bool host)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var win = new Window { Width = 1000, Height = 800, Content = new Border { Background = Brushes.Gray } };
        if (host) GlassMenus.SetHost(win, true);
        win.Show();
        Dispatcher.UIThread.RunJobs();
        var native = new NativePopups(win);
        var tip = new ToolTip { Content = "Click to play the animation" };
        var owner = new Button { Content = "poster", Width = 120, Height = 120 };
        ToolTip.SetTip(owner, tip);
        var root = NativePopup(win, new Border { Width = 300, Height = 300, Child = owner }, out var flyout);
        return new Rig(win, native, flyout, root, owner, tip);
    }

    [AvaloniaTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void TooltipInsideANativePopup_OpensInItsOwnPopupAboveIt(bool host, bool glass)
    {
        using var rig = Setup(host);
        if (glass) AppGlass.Set(true, Colors.Black, Colors.Black); else AppGlass.Clear();
        // Was (main window, glass on): forced into the overlay layer, which a PopupRoot does not
        // have: InvalidOperationException "no overlay layer is found" and no tooltip; and the
        // window's own overlay layer lies under the popup window anyway.
        ToolTip.SetIsOpen(rig.Owner, true);
        Dispatcher.UIThread.RunJobs();

        var tipRoot = Assert.IsType<PopupRoot>(TopLevel.GetTopLevel(rig.Tip));
        Assert.NotSame(rig.Root, tipRoot);
        Assert.Same(rig.Root, tipRoot.ParentTopLevel);
        Assert.False(ToolTip.GetShouldUseOverlayLayer(rig.Owner));
        Assert.Equal(glass, rig.Tip.Classes.Contains(GlassMenus.GlassClass));
        Assert.DoesNotContain(GlassMenus.OverlayClass, rig.Tip.Classes);
    }

    [AvaloniaFact]
    public void GlassTooltipOverAPopup_DoesNotFrostTheWindowBehindThatPopup()
    {
        using var rig = Setup(host: false);
        AppGlass.Set(true, Colors.Black, Colors.Black);
        ToolTip.SetIsOpen(rig.Owner, true);
        Dispatcher.UIThread.RunJobs();

        var backdrop = rig.Tip.GetVisualDescendants().OfType<GlassMenuBackdrop>().Single();
        Assert.Same(rig.Win, backdrop.Window);
        // The chooser card lies between the tip and the editor: the editor's copy is masked
        // there (the tip reads as the plain card) instead of frosted through the chooser.
        var covered = Assert.Single(backdrop.ParentPopupRects());
        Assert.Equal(rig.Root.ClientSize.Width, covered.Width, 1);
        Assert.Equal(rig.Root.ClientSize.Height, covered.Height, 1);
    }

    [AvaloniaFact]
    public void GlassTooltipStraightOverTheWindow_StillFrostsTheWindow()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var tip = new ToolTip { Content = "Search" };
        var owner = new Button { Content = "x", Width = 40, Height = 40 };
        ToolTip.SetTip(owner, tip);
        var win = new Window { Width = 600, Height = 400, Content = owner };
        win.Show();
        Dispatcher.UIThread.RunJobs();
        using var native = new NativePopups(win);
        try
        {
            AppGlass.Set(true, Colors.Black, Colors.Black);
            ToolTip.SetIsOpen(owner, true);
            Dispatcher.UIThread.RunJobs();
            Assert.IsType<PopupRoot>(TopLevel.GetTopLevel(tip));
            var backdrop = tip.GetVisualDescendants().OfType<GlassMenuBackdrop>().Single();
            Assert.Same(win, backdrop.Window);
            Assert.Empty(backdrop.ParentPopupRects());
        }
        finally
        {
            ToolTip.SetIsOpen(owner, false);
            AppGlass.Clear();
            win.Close();
        }
    }
}
