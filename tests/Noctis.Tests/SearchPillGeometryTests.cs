using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The search capsule popup is pinned at a constant X (SidebarView.SearchCapsuleX)
/// derived from where the rail button's icon grid settles. That constant is only
/// honest if BOTH settled rail states — collapsed 60px rail and hover-expanded
/// 220px rail — put SearchIconHost at the same x, and if the capsule's magnifier
/// holds that position through the whole open morph even while the hover rail
/// collapses underneath it (which is exactly what a click-open triggers: the
/// overlay pill steals IsPointerOver from the wrapper). Any disagreement is a
/// visible magnifier jump. These tests measure the real layout frame by frame
/// instead of trusting the margin math in the styles.
/// </summary>
public class SearchPillGeometryTests
{
    // SearchCapsuleX (6) + lip (8) + circle overhang (2).
    private const double ExpectedIconX = 16;

    private static (SidebarView Sidebar, Window Window) Mount(bool expanded)
    {
        var sidebar = new SidebarView { HorizontalAlignment = HorizontalAlignment.Left };
        var window = new Window { Width = 400, Height = 700, Content = sidebar };
        window.Show();
        DriveRailState(sidebar, expanded);
        return (sidebar, window);
    }

    /// <summary>
    /// The rail's expanded/collapsed geometry comes from IsExpanded bindings (panel
    /// alignment + margin via converters, button class, label visibility) plus the
    /// MainWindow wrapper width. No DataContext in this harness, so apply exactly
    /// what those bindings produce for each state.
    /// </summary>
    private static void DriveRailState(SidebarView sidebar, bool expanded)
    {
        sidebar.Width = expanded ? 220 : 60;
        // The rail is a fixed 6px-margin, stretched column in both states (the buttons
        // are nav-row sized: Padding 10 around a 28px icon); only the labels and the
        // inner content alignment follow IsExpanded.
        var rail = sidebar.FindControl<StackPanel>("RailActions")!;
        rail.Margin = new Thickness(6, 2, 6, 6);
        var button = sidebar.FindControl<Button>("SearchButton")!;
        if (expanded) button.Classes.Add("expanded");
        else button.Classes.Remove("expanded");
        sidebar.FindControl<TextBlock>("SearchLabel")!.IsVisible = expanded;
        sidebar.FindControl<TextBlock>("BackLabel")!.IsVisible = expanded;
        (sidebar.GetVisualRoot() as Window)?.UpdateLayout();
    }

    private static double RailIconX(SidebarView sidebar)
    {
        var host = sidebar.FindControl<Grid>("SearchIconHost")!;
        var p = host.TranslatePoint(new Point(0, 0), sidebar);
        Assert.NotNull(p);
        return p!.Value.X;
    }

    /// <summary>Window-space X of the capsule's magnifier glyph (popup content lives
    /// in the window's overlay layer, so window space is the shared frame).</summary>
    private static double CapsuleGlyphX(SidebarView sidebar, Window window)
    {
        var content = sidebar.FindControl<Border>("SearchPopupContent")!;
        var glyph = content.GetVisualDescendants().OfType<PathIcon>().First();
        var p = glyph.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(p);
        return p!.Value.X;
    }

    private static double RailGlyphX(SidebarView sidebar, Window window)
    {
        var host = sidebar.FindControl<Grid>("SearchIconHost")!;
        var glyph = host.GetVisualDescendants().OfType<PathIcon>().First();
        var p = glyph.TranslatePoint(new Point(0, 0), window);
        Assert.NotNull(p);
        return p!.Value.X;
    }

    private static void Tick(Window window)
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [AvaloniaFact]
    public void CollapsedRail_SettlesIconAtCapsuleX()
    {
        var (sidebar, _) = Mount(expanded: false);
        Assert.Equal(ExpectedIconX, RailIconX(sidebar));
    }

    [AvaloniaFact]
    public void ExpandedRail_SettlesIconAtCapsuleX()
    {
        var (sidebar, _) = Mount(expanded: true);
        Assert.Equal(ExpectedIconX, RailIconX(sidebar));
    }

    [AvaloniaFact]
    public void CapsuleGlyph_HoldsPosition_ThroughOpenMorph_AndMidMorphRailCollapse()
    {
        // Click-open scenario: rail hover-expanded at the moment the pill opens,
        // then the pill steals the pointer and the rail collapses mid-morph.
        var (sidebar, window) = Mount(expanded: true);
        var railGlyphBefore = RailGlyphX(sidebar, window);

        var popup = sidebar.FindControl<Popup>("SearchPopup")!;
        popup.IsOpen = true;
        Dispatcher.UIThread.RunJobs(); // run the Render-priority post that starts the morph
        window.UpdateLayout();

        var samples = new List<double> { CapsuleGlyphX(sidebar, window) };
        for (int i = 0; i < 24; i++)
        {
            Tick(window);
            samples.Add(CapsuleGlyphX(sidebar, window));
            if (i == 4)
                DriveRailState(sidebar, expanded: false);
        }

        Assert.Equal(railGlyphBefore, samples[0]); // handoff: capsule glyph appears exactly over the button glyph
        var drift = samples.Max() - samples.Min();
        Assert.True(drift <= 0.5,
            $"capsule glyph drifted {drift:F2}px across the morph: [{string.Join(", ", samples.Select(s => s.ToString("F1")))}]");
    }

    [AvaloniaFact]
    public void CloseHandoff_RestoredButtonGlyph_MatchesCapsuleGlyph()
    {
        var (sidebar, window) = Mount(expanded: false);
        var popup = sidebar.FindControl<Popup>("SearchPopup")!;
        popup.IsOpen = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        for (int i = 0; i < 24; i++) Tick(window); // let the open morph finish

        var capsuleGlyph = CapsuleGlyphX(sidebar, window);
        popup.IsOpen = false; // orphan close: button restore path
        Tick(window);

        Assert.Equal(capsuleGlyph, RailGlyphX(sidebar, window));
    }
}
