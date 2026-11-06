using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Beta 2 request: as on iOS, press on the tab bar and slide sideways to carry the selection pill
/// to another tab; letting go selects it. Taps stay taps, a vertical move is not taken, and only
/// the release navigates.
/// </summary>
public class MobileTabPillDragTests
{
    private static void Pump(Window window, int ticks = 4)
    {
        for (var i = 0; i < ticks; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    private static Point CentreOf(Control c, Window window) =>
        c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window)!.Value;

    [Fact]
    public void TheTabUnderTheFinger_AndThePillCentredOnIt_InsideTheBar()
    {
        const double bar = 380;
        Assert.Equal(0, ShellView.TabAt(bar, -20));
        Assert.Equal(0, ShellView.TabAt(bar, 75));
        Assert.Equal(1, ShellView.TabAt(bar, 77));
        Assert.Equal(4, ShellView.TabAt(bar, 500));
        // Mid-bar the pill is centred under the finger; at the ends it stops at the end tab's place.
        var x = ShellView.DraggedPillX(bar, 190);
        Assert.Equal(190, x + ShellView.SelectionPillWidth(bar, 2) / 2, 3);
        Assert.Equal(ShellView.SelectionPillX(bar, 0), ShellView.DraggedPillX(bar, 0), 3);
        Assert.Equal(ShellView.SelectionPillX(bar, 4), ShellView.DraggedPillX(bar, bar), 3);
    }

    [AvaloniaFact]
    public void DraggingThePill_FollowsTheFinger_NavigatesOnlyOnRelease()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Pump(window, 10);
        var host = view.FindControl<Panel>("SelectionPillHost")!;
        var home = view.FindControl<Button>("HomeTab")!;
        var library = view.FindControl<Button>("LibraryTab")!;
        var favorites = view.FindControl<Button>("FavoritesTab")!;
        Assert.Equal(MobileTab.Home, rig.Shell.SelectedTab);

        var from = CentreOf(home, window);
        var to = CentreOf(library, window);
        window.MouseDown(from, MouseButton.Left, RawInputModifiers.None);
        var changes = 0;
        rig.Shell.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellViewModel.SelectedTab)) changes++; };
        double lastX = double.NaN, lastFinger = from.X;
        for (var x = from.X + 10; x <= to.X; x += 10)
        {
            window.MouseMove(new Point(x, from.Y), RawInputModifiers.LeftMouseButton);
            Pump(window, 1);
            lastX = host.RenderTransform!.Value.M31;
            lastFinger = x;
        }
        Assert.True(view.IsDraggingTabPill);
        Assert.Equal(0, changes);                                  // passing over Favorites and Playlists: no navigation
        Assert.Equal(MobileTab.Home, rig.Shell.SelectedTab);
        var bar = view.FindControl<Panel>("TabRow")!;
        var fingerInBar = window.TranslatePoint(new Point(lastFinger, to.Y), bar)!.Value.X;
        Assert.Equal(ShellView.DraggedPillX(bar.Bounds.Width, fingerInBar), lastX, 1);   // under the finger
        Assert.True(view.FindControl<GlassDroplet>("SelectionPill")!.Lift > 0);

        window.MouseUp(to, MouseButton.Left, RawInputModifiers.None);
        Pump(window);
        Assert.False(view.IsDraggingTabPill);
        Assert.Equal(1, changes);
        Assert.Equal(MobileTab.Library, rig.Shell.SelectedTab);
        Assert.False(favorites.Classes.Contains("selected"));
        window.Close();
    }

    [AvaloniaFact]
    public void ATap_IsStillATap_AndAVerticalMove_IsNotTaken()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Pump(window, 10);
        var search = CentreOf(view.FindControl<Button>("SearchTab")!, window);
        window.MouseDown(search, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(search + new Point(3, 0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(search + new Point(3, 0), MouseButton.Left, RawInputModifiers.None);
        Pump(window);
        Assert.Equal(MobileTab.Search, rig.Shell.SelectedTab);

        // Up first: not a pill drag, whatever it does sideways after.
        var home = CentreOf(view.FindControl<Button>("HomeTab")!, window);
        window.MouseDown(home, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(home + new Point(0, -15), RawInputModifiers.LeftMouseButton);
        window.MouseMove(home + new Point(150, -15), RawInputModifiers.LeftMouseButton);
        Pump(window, 1);
        Assert.False(view.IsDraggingTabPill);
        window.MouseUp(home + new Point(150, -15), MouseButton.Left, RawInputModifiers.None);
        Pump(window);
        Assert.Equal(MobileTab.Search, rig.Shell.SelectedTab);
        window.Close();
    }

    [AvaloniaFact]
    public void LettingGo_OverTheSelectedTab_OnlySettlesThePill()
    {
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Pump(window, 10);
        var changes = 0;
        rig.Shell.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellViewModel.SelectedTab)) changes++; };
        var home = CentreOf(view.FindControl<Button>("HomeTab")!, window);
        window.MouseDown(home, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(home + new Point(30, 0), RawInputModifiers.LeftMouseButton);
        window.MouseMove(home + new Point(5, 0), RawInputModifiers.LeftMouseButton);
        Assert.True(view.IsDraggingTabPill);
        window.MouseUp(home + new Point(5, 0), MouseButton.Left, RawInputModifiers.None);
        Pump(window);
        Assert.Equal(0, changes);
        Assert.Equal(MobileTab.Home, rig.Shell.SelectedTab);
        window.Close();
    }
}
