using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The rail search capsule (owner 10-08 redesign): no outline at rest, an accent ring only
/// while the field has focus, a disc-to-pill morph whose close is quicker than its open, and
/// the open/close races that used to drop or snap it (fast clicks, Ctrl+F mid-collapse,
/// Esc from the cap/Clear, Clear stealing focus, a view-model close skipping the animation).
/// </summary>
public class SearchCapsuleMorphTests
{
    private sealed record Rig(Window Window, SidebarView Sidebar, TopBarViewModel TopBar)
    {
        public Popup Popup => Sidebar.FindControl<Popup>("SearchPopup")!;
        public Border Capsule => Sidebar.FindControl<Border>("SearchPopupContent")!;
        public Panel Ring => Sidebar.FindControl<Panel>("SearchCapsuleRing")!;
        public Border Stroke => Sidebar.FindControl<Border>("SearchCapsuleRingStroke")!;
        public TextBox Box => Sidebar.FindControl<TextBox>("SearchBox")!;
        public Button Clear => Sidebar.FindControl<Button>("SearchClearButton")!;
        public Button RailButton => Sidebar.FindControl<Button>("SearchButton")!;
    }

    private static readonly Color TestAccent = Color.FromRgb(0xE7, 0x48, 0x56);

    private static Rig Build(bool pump = true)
    {
        var topBar = new TopBarViewModel { CurrentTabName = "Songs" };
        var vm = new SidebarViewModel(new TestPersistenceService(), new FakeLibraryService()) { TopBar = topBar };
        var sidebar = new SidebarView { DataContext = vm, Width = 60, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var outside = new TextBox { Name = "OutsideBox", Width = 200, Height = 30 };
        var root = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { sidebar, outside } };
        var window = new Window { Width = 800, Height = 600, Content = root };
        // The headless app carries only Fluent; the accent the ring resolves comes from here.
        window.Resources["AccentColorBrush"] = new SolidColorBrush(TestAccent);
        window.Show();
        if (pump)
            for (int i = 0; i < 5; i++) { AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Thread.Sleep(10); }
        return new Rig(window, sidebar, topBar);
    }

    /// <summary>Real-time pump: transitions and the close timer run on wall-clock time, and
    /// the dispatcher's timers only fire while the test yields (await, not Thread.Sleep).</summary>
    private static async Task Pump(Window window, int ms)
    {
        var end = Environment.TickCount64 + ms;
        do
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Task.Delay(8);
        } while (Environment.TickCount64 < end);
    }

    private static async Task<Rig> OpenSettled()
    {
        var rig = Build();
        rig.TopBar.OpenSearchCommand.Execute(null);
        await Pump(rig.Window, 600);
        Assert.True(rig.Popup.IsOpen);
        return rig;
    }

    private static Point Center(Visual v, Visual relativeTo)
        => v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), relativeTo)!.Value;

    [AvaloniaFact]
    public async Task OpenCapsule_IsFilled_NoOutline_RingAccentOnlyWhileFocused()
    {
        var rig = await OpenSettled();

        // No border anywhere in the capsule except the focus ring.
        foreach (var b in rig.Capsule.GetSelfAndVisualDescendants().OfType<Border>())
        {
            if (ReferenceEquals(b, rig.Stroke) || b.TemplatedParent != null) continue;
            Assert.True(b.BorderThickness == default(Thickness),
                $"{b.Name ?? b.GetType().Name} draws a {b.BorderThickness} outline");
        }
        Assert.Equal(0, rig.Capsule.BorderThickness.Left);

        // Focused on open: accent ring.
        Assert.True(rig.Box.IsFocused, "the field did not take focus on open");
        var ring = Assert.IsAssignableFrom<ISolidColorBrush>(rig.Stroke.BorderBrush);
        Assert.Equal(TestAccent, ring.Color);
        Assert.Equal(1, rig.Ring.Opacity);
        Assert.Equal(1, rig.Stroke.Opacity);

        // Focus elsewhere: the ring goes clear, nothing outlines the pill.
        ((StackPanel)rig.Window.Content!).Children.OfType<TextBox>().Single().Focus();
        Assert.False(rig.Capsule.IsKeyboardFocusWithin);
        await Pump(rig.Window, 300);
        Assert.Equal(0, rig.Stroke.Opacity);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task Morph_DiscToPill_AndBack_CloseFasterThanOpen()
    {
        var rig = Build();
        var railGlyph = Center(rig.RailButton.GetVisualDescendants().OfType<PathIcon>().First(), rig.Window);

        rig.TopBar.OpenSearchCommand.Execute(null);
        Assert.Equal(SidebarView.SearchCapsuleClosedSize, rig.Capsule.Width);   // the snap pose
        Assert.Equal(SidebarView.SearchCapsuleClosedSize, rig.Capsule.Height);
        Dispatcher.UIThread.RunJobs();   // positions the overlay (and starts the grow)
        rig.Window.UpdateLayout();
        // Hand-off frame: the rail button's own disc, glyph exactly over the button's.
        Assert.InRange(rig.Capsule.Bounds.Width, SidebarView.SearchCapsuleClosedSize, SidebarView.SearchCapsuleClosedSize + 4);
        var capGlyph = rig.Capsule.GetVisualDescendants().OfType<PathIcon>().First();
        Assert.Equal(railGlyph, Center(capGlyph, rig.Window));

        await Pump(rig.Window, 600);
        Assert.Equal(SidebarView.SearchCapsuleOpenWidth, rig.Capsule.Bounds.Width);
        Assert.Equal(SidebarView.SearchCapsuleOpenHeight, rig.Capsule.Bounds.Height);
        Assert.Equal(railGlyph, Center(capGlyph, rig.Window)); // glyph anchored through the morph

        // Staging: the open is the long one and the text trails the shape; close is quicker.
        var openWidth = rig.Capsule.Transitions!.OfType<DoubleTransition>().First(t => t.Property == Avalonia.Layout.Layoutable.WidthProperty);
        var field = rig.Sidebar.FindControl<Grid>("SearchFieldArea")!;
        var fieldFade = field.Transitions!.OfType<DoubleTransition>().First(t => t.Property == Visual.OpacityProperty);
        Assert.True(fieldFade.Delay > TimeSpan.Zero, "the text should fade in after the width starts");
        Assert.Equal(TimeSpan.FromMilliseconds(SidebarView.SearchOpenMs), openWidth.Duration);

        rig.Box.Focus();
        rig.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(rig.TopBar.IsSearchOpen);
        Assert.True(rig.Popup.IsOpen, "Esc snapped the pill shut instead of collapsing it");
        var closeWidth = rig.Capsule.Transitions!.OfType<DoubleTransition>().First(t => t.Property == Avalonia.Layout.Layoutable.WidthProperty);
        Assert.True(closeWidth.Delay + closeWidth.Duration < openWidth.Duration, "close should be quicker than open");

        await Pump(rig.Window, 500);
        Assert.False(rig.Popup.IsOpen);
        Assert.Equal(SidebarView.SearchCapsuleClosedSize, rig.Capsule.Width);
        Assert.Equal(1, rig.RailButton.Opacity);
        rig.Window.Close();
    }

    /// <summary>Bug (old code): during the collapse IsSearchOpen was still true, so a second
    /// click or Ctrl+F hit CloseSearchPopup's "already animating" early-return and was
    /// dropped — the pill closed under the user. Now it turns around from where it is.</summary>
    [AvaloniaFact]
    public async Task OpenRequest_MidCollapse_TurnsAround_FromTheCurrentWidth()
    {
        var rig = await OpenSettled();
        rig.TopBar.ToggleSearchCommand.Execute(null);   // Ctrl+F: close
        await Pump(rig.Window, 120);
        var midWidth = rig.Capsule.Bounds.Width;
        Assert.InRange(midWidth, SidebarView.SearchCapsuleClosedSize + 1, SidebarView.SearchCapsuleOpenWidth - 1);

        rig.TopBar.ToggleSearchCommand.Execute(null);   // Ctrl+F again, mid-collapse: open
        await Pump(rig.Window, 16);
        Assert.True(rig.Capsule.Bounds.Width >= midWidth - 12,
            $"reopen jumped from {midWidth:F1} back to {rig.Capsule.Bounds.Width:F1}");

        await Pump(rig.Window, 700);
        Assert.True(rig.Popup.IsOpen, "the reopen was dropped and the pill closed");
        Assert.True(rig.TopBar.IsSearchOpen);
        Assert.Equal(SidebarView.SearchCapsuleOpenWidth, rig.Capsule.Bounds.Width);
        Assert.True(rig.Box.IsFocused);
        rig.Window.Close();
    }

    /// <summary>Bug (old code): navigating away mid-collapse snapped the popup shut, and an
    /// open landing before the stale close timer fired was shut by it. A view-model close
    /// now plays the same collapse, and any reopen stops the timer.</summary>
    [AvaloniaFact]
    public async Task ViewModelClose_Animates_AndAStaleTimerNeverClosesAReopenedPill()
    {
        var rig = await OpenSettled();
        rig.TopBar.IsSearchOpen = false;                 // what a navigation does
        await Pump(rig.Window, 30);
        Assert.True(rig.Popup.IsOpen, "a view-model close snapped the pill shut");

        rig.Popup.IsOpen = false;                        // a close that skips the collapse
        await Pump(rig.Window, 30);
        Assert.Equal(1, rig.RailButton.Opacity);
        rig.TopBar.OpenSearchCommand.Execute(null);      // reopened inside the old timer window
        await Pump(rig.Window, 600);
        Assert.True(rig.Popup.IsOpen, "the earlier collapse's timer closed the reopened pill");
        Assert.True(rig.TopBar.IsSearchOpen);
        rig.Window.Close();
    }

    /// <summary>Bug (old code): Esc was handled only on the text box. With focus on the
    /// Clear button it reached the window, which cleared the query and left an empty pill up.</summary>
    [AvaloniaFact]
    public async Task Escape_FromClearButton_ClosesThePill_AndKeepsTheQuery()
    {
        var rig = await OpenSettled();
        rig.TopBar.SearchText = "queen";
        await Pump(rig.Window, 30);
        Assert.True(rig.Clear.IsVisible);
        rig.Clear.Focus();
        rig.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(rig.TopBar.IsSearchOpen);
        Assert.Equal("queen", rig.TopBar.SearchText);
        await Pump(rig.Window, 500);
        Assert.False(rig.Popup.IsOpen);
        rig.Window.Close();
    }

    /// <summary>Bug (old code): clicking Clear focuses it, and clearing hides it, so focus
    /// fell out of the pill and the next query typed went nowhere.</summary>
    [AvaloniaFact]
    public async Task Clear_KeepsTypingInTheField()
    {
        var rig = await OpenSettled();
        rig.TopBar.SearchText = "queen";
        await Pump(rig.Window, 30);
        var at = Center(rig.Clear, rig.Window);
        rig.Window.MouseDown(at, MouseButton.Left);
        rig.Window.MouseUp(at, MouseButton.Left);
        await Pump(rig.Window, 60);
        Assert.Equal(string.Empty, rig.TopBar.SearchText);
        Assert.True(rig.Popup.IsOpen);
        Assert.True(rig.Box.IsFocused, "focus left the field after Clear");
        rig.Window.Close();
    }

    /// <summary>The 40px pill sits in the 48px button row: a click in the strip above it must
    /// not reach the hidden button beneath and toggle a typed (sticky) pill shut.</summary>
    [AvaloniaFact]
    public async Task ClickInTheStripAboveThePill_DoesNotToggleIt()
    {
        var rig = await OpenSettled();
        rig.TopBar.SearchText = "queen";
        await Pump(rig.Window, 30);
        var top = rig.RailButton.TranslatePoint(new Point(24, 1.5), rig.Window)!.Value;
        rig.Window.MouseDown(top, MouseButton.Left);
        rig.Window.MouseUp(top, MouseButton.Left);
        await Pump(rig.Window, 400);
        Assert.True(rig.TopBar.IsSearchOpen);
        Assert.True(rig.Popup.IsOpen);
        rig.Window.Close();
    }

    /// <summary>Esc during the open (a fast double action) collapses from where the pill is;
    /// it used to jump to full width first, the transition restarting from its base value.</summary>
    [AvaloniaFact]
    public async Task CloseDuringTheOpen_ShrinksFromTheCurrentWidth()
    {
        var rig = Build();
        rig.TopBar.OpenSearchCommand.Execute(null);
        await Pump(rig.Window, 60);
        var midWidth = rig.Capsule.Bounds.Width;
        Assert.InRange(midWidth, SidebarView.SearchCapsuleClosedSize + 1, SidebarView.SearchCapsuleOpenWidth - 1);
        rig.TopBar.ToggleSearchCommand.Execute(null);
        var samples = new List<double>();
        for (int i = 0; i < 20; i++) { await Pump(rig.Window, 1); samples.Add(rig.Capsule.Bounds.Width); }
        Assert.True(samples.Max() <= midWidth + 24,
            $"close from {midWidth:F1} overshot to {samples.Max():F1}: [{string.Join(", ", samples.Select(x => x.ToString("F0")))}]");
        await Pump(rig.Window, 500);
        Assert.False(rig.Popup.IsOpen);
        rig.Window.Close();
    }

    /// <summary>The popup carries margin around the capsule so its shadow isn't clipped by the
    /// overlay host; that margin must not catch clicks meant for the rail rows beneath.</summary>
    [AvaloniaFact]
    public async Task ShadowRoom_AroundThePill_DoesNotSwallowClicks()
    {
        var rig = await OpenSettled();
        var below = rig.Capsule.TranslatePoint(new Point(24, rig.Capsule.Bounds.Height + 12), rig.Window)!.Value;
        var right = rig.Capsule.TranslatePoint(new Point(rig.Capsule.Bounds.Width + 10, 20), rig.Window)!.Value;
        foreach (var p in new[] { below, right })
        {
            var hit = rig.Window.InputHitTest(p) as Visual;
            for (Visual? v = hit; v != null; v = v.GetVisualParent())
                Assert.False(v is Avalonia.Controls.Primitives.OverlayPopupHost, $"the popup's shadow room caught a click at {p}");
        }
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task Placeholder_FollowsTheSection_WhileOpen()
    {
        var rig = await OpenSettled();
        rig.TopBar.SearchWatermark = "Search in Albums";
        await Pump(rig.Window, 30);
        Assert.Equal("Search in Albums", rig.Box.Watermark);
        rig.Window.Close();
    }

    [AvaloniaFact]
    public async Task Capsule_FollowsTheRail_WhenTheSidebarMoves()
    {
        var rig = await OpenSettled();
        var railGlyph = rig.RailButton.GetVisualDescendants().OfType<PathIcon>().First();
        var capGlyph = rig.Capsule.GetVisualDescendants().OfType<PathIcon>().First();
        Assert.Equal(Center(railGlyph, rig.Window), Center(capGlyph, rig.Window));

        rig.Sidebar.Margin = new Thickness(30, 40, 0, 0);   // window chrome / layout shift
        rig.Window.Width = 640;
        await Pump(rig.Window, 100);
        Assert.Equal(Center(railGlyph, rig.Window), Center(capGlyph, rig.Window));
        rig.Window.Close();
    }
}
