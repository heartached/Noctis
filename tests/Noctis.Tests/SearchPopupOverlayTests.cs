using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
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
}
