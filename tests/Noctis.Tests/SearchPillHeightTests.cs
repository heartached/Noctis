using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord 11-21 (Halil, Interface Size 110%): the search bar "gets a bit smaller on focus".
/// The open pill was 40px in the 48px button row, and its popup offsets were applied unscaled
/// while its content took the Interface Size, so above 100% it also sat a few px high, off
/// the row it grows out of. And the lyric-search button's hover box was a small-radius
/// rectangle, wider than tall. Measured in window coordinates (what the user sees).
/// </summary>
public class SearchPillHeightTests : IDisposable
{
    public SearchPillHeightTests() => UiScale.Install();

    public void Dispose() => UiScale.SetFactor(1.0); // process-wide

    private static Rect OnScreen(Visual v, Visual root)
        => new Rect(v.Bounds.Size).TransformToAABB(v.TransformToVisual(root)!.Value);

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

    private static async Task<(Window Window, SidebarView Sidebar, TopBarViewModel TopBar)> Mount()
    {
        var topBar = new TopBarViewModel { CurrentTabName = "Songs" };
        var vm = new SidebarViewModel(new TestPersistenceService(), new FakeLibraryService()) { TopBar = topBar };
        var sidebar = new SidebarView { DataContext = vm, Width = 220, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        var window = new Window { Width = 900, Height = 700, Content = sidebar };
        window.Show();
        await Pump(window, 100);
        return (window, sidebar, topBar);
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.1)]
    [InlineData(0.9)]
    [InlineData(1.5)]
    public async Task OpenPill_CoversTheButtonRow_SameHeightAndCentre(double factor)
    {
        UiScale.SetFactor(factor);
        var (window, sidebar, topBar) = await Mount();
        var row = OnScreen(sidebar.FindControl<Button>("SearchButton")!, window);

        topBar.OpenSearchCommand.Execute(null);
        var capsule = sidebar.FindControl<Border>("SearchPopupContent")!;
        var end = Environment.TickCount64 + 3000;
        while (capsule.Bounds.Width != SidebarView.SearchCapsuleOpenWidth && Environment.TickCount64 < end)
            await Pump(window, 16);
        await Pump(window, 50);
        var pill = OnScreen(capsule, window);
        window.Close();

        Assert.Equal(SidebarView.SearchCapsuleClosedSize, SidebarView.SearchCapsuleOpenHeight);
        Assert.Equal(row.Height, pill.Height, 0.5);
        Assert.Equal(row.Center.Y, pill.Center.Y, 0.5);
        Assert.Equal(row.X, pill.X, 0.5);
        Assert.Equal(SidebarView.SearchCapsuleOpenWidth * factor, pill.Width, 0.5); // still expands
    }

    [AvaloniaFact]
    public async Task PillIconButtons_AreRound()
    {
        var (window, sidebar, topBar) = await Mount();
        topBar.IsLyricsSearchAvailable = true;   // the Songs page (MainWindowViewModel sets it)
        topBar.OpenSearchCommand.Execute(null);
        topBar.SearchText = "queen";   // shows Clear
        await Pump(window, 600);

        foreach (var name in new[] { "SearchCapButton", "SearchClearButton", "SearchLyricsButton" })
        {
            var b = sidebar.FindControl<Button>(name)!;
            Assert.True(b.IsVisible, $"{name} hidden");
            Assert.True(b.Classes.Contains("pill-icon"), $"{name} lacks the pill-icon style");
            Assert.Equal(b.Bounds.Width, b.Bounds.Height);
            Assert.True(b.CornerRadius.TopLeft >= b.Bounds.Height / 2, $"{name} radius {b.CornerRadius}");
        }
        window.Close();
    }
}
