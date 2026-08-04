using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The search pill and the volume flyout are persistent (non-light-dismiss) popups.
/// Left at its default, an Avalonia Popup opens a native OS window — WS_EX_TOPMOST
/// on Win32, override-redirect on X11 — which floats over other applications and
/// outlives the host window minimizing. Hosting them in the window's overlay layer
/// is what keeps them inside the window on every platform, so these tests pin the
/// properties that behavior rests on.
/// </summary>
public class SearchPopupOverlayTests
{
    [AvaloniaFact]
    public void SearchPill_IsOverlayHosted_AndStaysNonLightDismiss()
    {
        var sidebar = new SidebarView();
        var popup = Assert.IsType<Popup>(sidebar.FindControl<Popup>("SearchPopup"));

        Assert.True(popup.ShouldUseOverlayLayer);
        Assert.False(popup.IsLightDismissEnabled);
    }

    [AvaloniaFact]
    public void VolumeFlyout_IsOverlayHosted()
    {
        var bar = new PlaybackBarView();
        var popup = Assert.IsType<Popup>(bar.FindControl<Popup>("VolumeFlyout"));

        Assert.True(popup.ShouldUseOverlayLayer);
    }

    // Non-light-dismiss is deliberate while the pill holds text (it stays up while
    // the user works with the filtered page) — but an EMPTY pill filters nothing,
    // so a click anywhere else collapses it. These pin both halves of that rule.

    private static (Window Window, TopBarViewModel TopBar) BuildShownSidebarWindow()
    {
        var topBar = new TopBarViewModel();
        var vm = new SidebarViewModel(new TestPersistenceService(), new FakeLibraryService())
        {
            TopBar = topBar
        };
        var window = new Window { Width = 800, Height = 600, Content = new SidebarView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        topBar.IsSearchOpen = true;
        Dispatcher.UIThread.RunJobs();
        return (window, topBar);
    }

    /// <summary>Pumps the dispatcher until the condition holds (or the budget runs out).</summary>
    private static async Task PumpUntil(Func<bool> condition, int budgetMs)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task EmptyPill_CollapsesOnClickElsewhere()
    {
        var (window, topBar) = BuildShownSidebarWindow();

        window.MouseDown(new Point(600, 400), MouseButton.Left);
        // The dismissal routes through the ~250 ms collapse morph before IsSearchOpen flips.
        await PumpUntil(() => !topBar.IsSearchOpen, 5000);

        Assert.False(topBar.IsSearchOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TypedPill_StaysOpenOnClickElsewhere()
    {
        var (window, topBar) = BuildShownSidebarWindow();
        topBar.SearchText = "queen";

        window.MouseDown(new Point(600, 400), MouseButton.Left);
        // Outlives the collapse animation — nothing may happen while text is present.
        await PumpUntil(() => !topBar.IsSearchOpen, 600);

        Assert.True(topBar.IsSearchOpen);
        window.Close();
    }
}
